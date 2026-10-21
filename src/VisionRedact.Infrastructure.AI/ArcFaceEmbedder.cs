using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using Microsoft.ML.OnnxRuntime;
using VisionRedact.Application.Detections;
using VisionRedact.Application.Media;
using VisionRedact.Application.People;
using VisionRedact.Domain.Geometry;
using VisionRedact.Domain.People;

namespace VisionRedact.Infrastructure.AI;

// YuNet finds the 5 face points, ArcFace (MobileFaceNet, w600k_mbf) makes the fingerprint.
// don't forget: w600k_mbf is for non-commercial research only. a product would swap in e.g. SFace behind
// IFaceEmbedder and tune SamePersonThreshold again (ADR-0022)
public sealed class ArcFaceEmbedderFactory(DetectorOptions options) : IFaceEmbedderFactory
{
    public const string RecognizerFile = "w600k_mbf.onnx";
    public const string LandmarkFile = "face_detection_yunet_2023mar.onnx";

    public bool IsAvailable => File.Exists(RecognizerPath) && File.Exists(LandmarkPath);

    private string RecognizerPath => Path.Combine(options.ResolveModelsDirectory(), RecognizerFile);

    private string LandmarkPath => Path.Combine(options.ResolveModelsDirectory(), LandmarkFile);

    public IFaceEmbedder Create()
    {
        if (!IsAvailable)
        {
            throw new DetectionException(DetectionErrorKind.ModelNotFound, $"Face models not found in '{options.ResolveModelsDirectory()}'. Run scripts/download-face-models.ps1.");
        }

        return new ArcFaceEmbedder(RecognizerPath, LandmarkPath, options.IntraOpThreads);
    }
}

// face box -> padded crop -> YuNet 5 points -> warp onto the 112x112 template -> ArcFace -> FaceEmbedding.
// IdentificationJob makes one per run and calls Embed once per frame w/ that frame's face shots.
// why align: ArcFace was trained on faces w/ eyes, nose and mouth at fixed px, a plain crop of the box gives a worse
// fingerprint. no face in the crop (false hit, profile, motion blur) -> null, which also keeps junk out of the clusters.
// CPU only: just ShotsPerTrack faces a track, not worth a GPU session + its fallback (ADR-0022). not thread-safe
internal sealed class ArcFaceEmbedder : IFaceEmbedder
{
    private const int AlignedSize = 112;
    private const int LandmarkInput = 160; // YuNet's input side: big enough for small faces, still cheap
    private const float CropPadding = 1.6f; // crop side = box side * 1.6, so YuNet sees the whole head
    private const float LandmarkScore = 0.6f;

    // where ArcFace wants the eyes, nose tip and mouth corners in its 112x112 input (the InsightFace template),
    // in YuNet's point order
    private static readonly PointF[] Template =
    [
        new(38.2946f, 51.6963f), new(73.5318f, 51.5014f), new(56.0252f, 71.7366f), new(41.5493f, 92.3655f), new(70.7299f, 92.2041f),
    ];

    private readonly InferenceSession _session;
    private readonly FaceDetectorYN _landmarks;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly float[] _tensor = new float[3 * AlignedSize * AlignedSize];
    private readonly Mat _crop = new();
    private readonly Mat _aligned = new();
    private readonly Mat _found = new();

    public ArcFaceEmbedder(string recognizerPath, string landmarkPath, int intraOpThreads)
    {
        using var sessionOptions = OnnxDetectorFactory.CreateSessionOptions(intraOpThreads);
        _session = new InferenceSession(recognizerPath, sessionOptions);
        try
        {
            var (input, meta) = _session.InputMetadata.Single();
            if (meta.Dimensions is not [_, 3, AlignedSize, AlignedSize])
            {
                throw new DetectionException(DetectionErrorKind.InvalidModel, $"Expected a face recognizer input [N, 3, 112, 112], got [{string.Join(", ", meta.Dimensions)}].");
            }

            _inputName = input;
            _outputName = _session.OutputMetadata.Keys.First();
            _landmarks = new FaceDetectorYN(landmarkPath, string.Empty, new Size(LandmarkInput, LandmarkInput), LandmarkScore, 0.3f, 5000, Emgu.CV.Dnn.Backend.Default, Emgu.CV.Dnn.Target.Cpu);
        }
        catch
        {
            _session.Dispose();
            throw;
        }
    }

    public string ModelId => "arcface-w600k-mbf";

    public IReadOnlyList<FaceEmbedding?> Embed(OwnedVideoFrame frame, IReadOnlyList<NormalizedRect> faces, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(faces);

        // view over the frame's native px, no copy. the frame outlives this call
        using var image = new Mat(frame.Height, frame.Width, DepthType.Cv8U, 3, frame.DataPointer, frame.Stride);
        var results = new FaceEmbedding?[faces.Count];
        for (var i = 0; i < faces.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results[i] = TryAlign(image, faces[i]) ? Recognize() : null;
        }

        return results;
    }

    public void Dispose()
    {
        _session.Dispose();
        _landmarks.Dispose();
        _crop.Dispose();
        _aligned.Dispose();
        _found.Dispose();
    }

    // padded square crop -> YuNet points -> similarity transform onto Template -> _aligned (112x112)
    private bool TryAlign(Mat image, NormalizedRect face)
    {
        var box = face.ToPixels(image.Width, image.Height);
        var side = (int)(Math.Max(box.Width, box.Height) * CropPadding);
        var crop = Rectangle.Intersect(
            new Rectangle(box.X + (box.Width / 2) - (side / 2), box.Y + (box.Height / 2) - (side / 2), side, side),
            new Rectangle(0, 0, image.Width, image.Height));
        if (crop.Width < 8 || crop.Height < 8)
        {
            return false;
        }

        using (var view = new Mat(image, crop))
        {
            CvInvoke.Resize(view, _crop, new Size(LandmarkInput, LandmarkInput));
        }

        _landmarks.Detect(_crop, _found);
        if (_found.Rows == 0 || _found.GetData() is not float[,] rows)
        {
            return false;
        }

        // a padded crop can hold two faces (people side by side), take the one nearest the crop center
        var best = Enumerable.Range(0, rows.GetLength(0))
            .OrderBy(r => Math.Pow(rows[r, 0] + (rows[r, 2] / 2) - (LandmarkInput / 2f), 2) + Math.Pow(rows[r, 1] + (rows[r, 3] / 2) - (LandmarkInput / 2f), 2))
            .First();

        // YuNet row: x, y, w, h, then 5 points (x, y) in crop px, then the score. map the points back to frame px
        var (scaleX, scaleY) = ((float)crop.Width / LandmarkInput, (float)crop.Height / LandmarkInput);
        var points = Enumerable.Range(0, 5).Select(k => new PointF(crop.X + (rows[best, 4 + (2 * k)] * scaleX), crop.Y + (rows[best, 5 + (2 * k)] * scaleY))).ToArray();

        // partial affine = rotate + uniform scale + shift only, so the face is never sheared
        using var source = new VectorOfPointF(points);
        using var target = new VectorOfPointF(Template);
        using var inliers = new Mat();
        using var transform = CvInvoke.EstimateAffinePartial2D(source, target, inliers, RobustEstimationAlgorithm.LMEDS, 3, 2000, 0.99, 10);
        if (transform.IsEmpty)
        {
            return false;
        }

        CvInvoke.WarpAffine(image, _aligned, transform, new Size(AlignedSize, AlignedSize), Inter.Linear, Warp.Default, BorderType.Constant, new MCvScalar(0));
        return true;
    }

    // BGR bytes -> RGB planes, (x - 127.5) / 127.5 = -1..1 like InsightFace -> ONNX -> fingerprint
    private unsafe FaceEmbedding Recognize()
    {
        const int plane = AlignedSize * AlignedSize;
        var pixels = new ReadOnlySpan<byte>((void*)_aligned.DataPointer, plane * 3); // WarpAffine output is continuous
        for (var i = 0; i < plane; i++)
        {
            _tensor[i] = (pixels[(i * 3) + 2] - 127.5f) / 127.5f;
            _tensor[plane + i] = (pixels[(i * 3) + 1] - 127.5f) / 127.5f;
            _tensor[(2 * plane) + i] = (pixels[i * 3] - 127.5f) / 127.5f;
        }

        using var input = OrtValue.CreateTensorValueFromMemory(_tensor, [1, 3, AlignedSize, AlignedSize]);
        using var runOptions = new RunOptions();
        using var outputs = _session.Run(runOptions, [_inputName], [input], [_outputName]);
        return new FaceEmbedding(ModelId, outputs[0].GetTensorDataAsSpan<float>());
    }
}
