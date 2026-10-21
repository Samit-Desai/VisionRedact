using Microsoft.ML.OnnxRuntime;
using VisionRedact.Application.Detections;
using VisionRedact.Domain.Detections;
using VisionRedact.Domain.Geometry;

namespace VisionRedact.Infrastructure.AI;

// raw model numbers -> candidates normalized to the source frame. one per model family, the profile picks it (ADR-0021)
internal interface IOutputDecoder
{
    // in the order Decode reads them
    IReadOnlyList<string> OutputNames { get; }

    // reads the outputs in place: spans over native memory, the tensors are never copied
    IReadOnlyList<DetectionCandidate> Decode(IReadOnlyList<OrtValue> outputs, LetterboxTransform transform, float confidenceThreshold, double minimumBoxSize);
}

internal static class BoxMapping
{
    // model-input px -> normalized box. clamped (boxes can run into the bars or past the image), empty or tiny ones dropped
    public static bool TryMap(RectD inputPixels, LetterboxTransform transform, double minimumBoxSize, out NormalizedRect box)
    {
        box = transform.ContainerToNormalized(inputPixels);
        return !box.IsEmpty && box.Width >= minimumBoxSize && box.Height >= minimumBoxSize;
    }
}

// UltraFace (version-RFB-320): scores [1, N, 2] (background, face), boxes [1, N, 4] = x1, y1, x2, y2 in 0..1 of the
// model input - bars included
internal sealed class UltraFaceDecoder(string scoresOutput, string boxesOutput, int inputWidth, int inputHeight) : IOutputDecoder
{
    public IReadOnlyList<string> OutputNames { get; } = [scoresOutput, boxesOutput];

    public IReadOnlyList<DetectionCandidate> Decode(IReadOnlyList<OrtValue> outputs, LetterboxTransform transform, float confidenceThreshold, double minimumBoxSize)
    {
        // N from both tensors, the smaller wins, so a malformed output can't index past the end
        var scores = outputs[0].GetTensorDataAsSpan<float>();
        var boxes = outputs[1].GetTensorDataAsSpan<float>();
        var anchors = Math.Min(scores.Length / 2, boxes.Length / 4);
        var candidates = new List<DetectionCandidate>();
        for (var i = 0; i < anchors; i++)
        {
            var confidence = scores[(i * 2) + 1];
            if (!(confidence >= confidenceThreshold)) // also rejects NaN
            {
                continue;
            }

            // 0..1 of the input -> input px -> back through the letterbox
            var b = i * 4;
            var pixels = RectD.FromCorners(boxes[b] * inputWidth, boxes[b + 1] * inputHeight, boxes[b + 2] * inputWidth, boxes[b + 3] * inputHeight);
            if (BoxMapping.TryMap(pixels, transform, minimumBoxSize, out var box))
            {
                candidates.Add(new DetectionCandidate(box, confidence, ObjectClass.Face));
            }
        }

        return candidates;
    }
}

// YOLO v8/v11 detect head [1, 4 + C, N]: per anchor cx, cy, w, h in input px, then C class scores (already sigmoid,
// no separate objectness). transposed [1, N, 4 + C] exports work too. classes we don't map are skipped
internal sealed class YoloDecoder(string outputName, int valuesPerAnchor, int anchors, bool anchorsLast, IReadOnlyDictionary<int, ObjectClass> classes) : IOutputDecoder
{
    public IReadOnlyList<string> OutputNames { get; } = [outputName];

    public IReadOnlyList<DetectionCandidate> Decode(IReadOnlyList<OrtValue> outputs, LetterboxTransform transform, float confidenceThreshold, double minimumBoxSize)
    {
        var data = outputs[0].GetTensorDataAsSpan<float>();
        var classCount = valuesPerAnchor - 4;
        var candidates = new List<DetectionCandidate>();

        for (var i = 0; i < anchors; i++)
        {
            // best mapped class
            var bestScore = 0f;
            var bestClass = -1;
            for (var c = 0; c < classCount; c++)
            {
                var score = Value(data, 4 + c, i);
                if (score > bestScore && classes.ContainsKey(c))
                {
                    bestScore = score;
                    bestClass = c;
                }
            }

            // !(x >= t) rejects NaN too
            if (bestClass < 0 || !(bestScore >= confidenceThreshold))
            {
                continue;
            }

            // center + size -> top-left + size
            var cx = Value(data, 0, i);
            var cy = Value(data, 1, i);
            var w = Value(data, 2, i);
            var h = Value(data, 3, i);
            if (BoxMapping.TryMap(new RectD(cx - (w / 2), cy - (h / 2), w, h), transform, minimumBoxSize, out var box))
            {
                candidates.Add(new DetectionCandidate(box, bestScore, classes[bestClass]));
            }
        }

        return candidates;
    }

    // anchorsLast = [channel][anchor] (the usual export), else [anchor][channel] (transposed)
    private float Value(ReadOnlySpan<float> data, int channel, int anchor) =>
        anchorsLast ? data[(channel * anchors) + anchor] : data[(anchor * valuesPerAnchor) + channel];
}

// NMS-free YOLO (YOLO26, YOLOv10, or v8/v11 exported w/ nms=True): [1, K, 6], rows = x1, y1, x2, y2, confidence,
// class id in input px. the model already dropped the duplicates, so only filter + cap here.
// the class id comes as a float (the tensor is all floats), so it's rounded
internal sealed class YoloEndToEndDecoder(string outputName, int rows, int maxDetections, IReadOnlyDictionary<int, ObjectClass> classes) : IOutputDecoder
{
    private const int ValuesPerRow = 6;

    public IReadOnlyList<string> OutputNames { get; } = [outputName];

    public IReadOnlyList<DetectionCandidate> Decode(IReadOnlyList<OrtValue> outputs, LetterboxTransform transform, float confidenceThreshold, double minimumBoxSize)
    {
        var data = outputs[0].GetTensorDataAsSpan<float>();
        var count = Math.Min(rows, data.Length / ValuesPerRow); // never index past a malformed tensor
        var candidates = new List<DetectionCandidate>();
        for (var i = 0; i < count; i++)
        {
            var row = data.Slice(i * ValuesPerRow, ValuesPerRow);
            var confidence = row[4];
            if (!(confidence >= confidenceThreshold) || !classes.TryGetValue((int)MathF.Round(row[5]), out var objectClass))
            {
                continue;
            }

            if (BoxMapping.TryMap(RectD.FromCorners(row[0], row[1], row[2], row[3]), transform, minimumBoxSize, out var box))
            {
                candidates.Add(new DetectionCandidate(box, confidence, objectClass));
            }
        }

        // rows normally come best first, but the cap must keep the best whatever the export did
        return candidates.Count <= maxDetections
            ? candidates
            : candidates.OrderByDescending(c => c.Confidence).Take(maxDetections).ToArray();
    }
}

// decorator: NMS around the decoder of a model that isn't NMS-free. the profile decides, so OnnxObjectDetector runs
// every model the same way. NMS used to be hard-wired in Detect, which would also drop overlapping faces an NMS-free
// head reports on purpose (people hugging, ADR-0021)
internal sealed class NmsDecoder(IOutputDecoder inner, float iouThreshold, int maxDetections) : IOutputDecoder
{
    public IReadOnlyList<string> OutputNames => inner.OutputNames;

    // TODO: nothing reads Inner, not even the tests. drop it, ok for now
    internal IOutputDecoder Inner => inner;

    public IReadOnlyList<DetectionCandidate> Decode(IReadOnlyList<OrtValue> outputs, LetterboxTransform transform, float confidenceThreshold, double minimumBoxSize) =>
        NonMaxSuppression.Apply(inner.Decode(outputs, transform, confidenceThreshold, minimumBoxSize), iouThreshold, maxDetections);
}
