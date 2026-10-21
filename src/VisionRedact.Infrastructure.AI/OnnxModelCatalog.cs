using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using VisionRedact.Application.Detections;
using VisionRedact.Domain.Detections;

namespace VisionRedact.Infrastructure.AI;

// the model picker's list: 3 built-in face models + any YOLO detect model dropped into the models folder.
// a custom YOLO is known by the metadata its exporter embeds (task = detect, names = {0: 'person', ...}), so a
// person / car / plate detector is "copy the .onnx in", no code change. only describes models, never runs one.
// DetectionPreferences (GetModels) -> the picked model -> DetectorHost -> OnnxDetectorFactory
public sealed partial class OnnxModelCatalog : IDetectionModelCatalog
{
    public const string UltraFaceId = "ultraface-rfb-320";
    public const string YoloFaceId = "yolo11n-face";
    public const string Yolo26FaceId = "yolo26l-face";

    private const string UltraFaceFile = "version-RFB-320.onnx";
    private const string YoloFaceFile = "yolo11n-face.onnx";
    private const string Yolo26FaceFile = "yolo26l-face.onnx";

    private static readonly Dictionary<int, ObjectClass> FaceOnly = new() { [0] = ObjectClass.Face };

    private readonly DetectorOptions _options;
    private readonly ILogger _logger;
    // path -> (last write time, the model or null = not a detector). case-insensitive like Windows paths.
    // a new stamp = the file was replaced, read it again
    private readonly Dictionary<string, (DateTime Stamp, DetectionModel? Model)> _customCache = new(StringComparer.OrdinalIgnoreCase);

    // guards _customCache: GetModels runs on the thread pool and two calls can overlap
    private readonly object _gate = new();

    public OnnxModelCatalog(DetectorOptions options, ILogger<OnnxModelCatalog>? logger = null)
    {
        _options = options;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    // built-ins + the face-identity models: known, so skipped w/o loading them
    private static readonly HashSet<string> KnownFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        UltraFaceFile, YoloFaceFile, Yolo26FaceFile, ArcFaceEmbedderFactory.RecognizerFile, ArcFaceEmbedderFactory.LandmarkFile,
    };

    public IReadOnlyList<DetectionModel> GetModels()
    {
        // built-ins are always listed (unavailable if the file is missing), fastest to most accurate.
        // the float after the format = the model's default confidence
        var directory = _options.ResolveModelsDirectory();
        var models = new List<DetectionModel>
        {
            BuiltIn(UltraFaceId, "UltraFace RFB-320 (fastest)", Path.Combine(directory, UltraFaceFile), DetectionModelFormat.UltraFace, 0.7f,
                "ONNX Model Zoo, MIT licence. 320×240 input: very fast on CPU, weaker on small or distant faces."),
            BuiltIn(YoloFaceId, "YOLO11n face (balanced)", Path.Combine(directory, YoloFaceFile), DetectionModelFormat.Yolo, 0.35f,
                "Hugging Face AdamCodd/YOLOv11n-face-detection, trained on WIDER FACE. 640×640 input: better recall on small faces. "
                + "Ultralytics YOLO11 architecture — embedded licence AGPL-3.0 (fine for evaluation; commercial use needs an Ultralytics licence)."),
            BuiltIn(Yolo26FaceId, "YOLO26l face (most accurate, NMS-free)", Path.Combine(directory, Yolo26FaceFile), DetectionModelFormat.YoloEndToEnd, 0.3f,
                "Hugging Face uralman/yolo26l-widerface, trained on WIDER FACE at 1280×1280: best on small and crowded faces, slowest. "
                + "Ultralytics YOLO26 end-to-end head (no NMS needed) — embedded licence AGPL-3.0."),
        };

        // every other .onnx in the folder is opened and added if it's a YOLO detect model
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.onnx"))
            {
                if (!KnownFiles.Contains(Path.GetFileName(file)) && DescribeCustom(file) is { } custom)
                {
                    models.Add(custom);
                }
            }
        }

        return models;
    }

    // model label -> domain class, null = not something we redact
    internal static ObjectClass? MapClassName(string label) => label.Trim().ToLowerInvariant() switch
    {
        "face" or "faces" or "head" => ObjectClass.Face,
        "person" or "people" or "pedestrian" => ObjectClass.Person,
        "car" or "truck" or "bus" or "motorcycle" or "motorbike" or "van" or "vehicle" => ObjectClass.Vehicle,
        "license_plate" or "license plate" or "licence plate" or "plate" or "number_plate" or "number plate" => ObjectClass.LicensePlate,
        "text" => ObjectClass.Text,
        _ => null,
    };

    // the "names" metadata, e.g. {0: 'person', 1: 'bicycle'}. labels we don't redact are left out, so the decoder
    // ignores those output columns
    internal static Dictionary<int, ObjectClass> ParseClassNames(string names)
    {
        var map = new Dictionary<int, ObjectClass>();
        foreach (Match m in NamesPattern().Matches(names))
        {
            if (int.TryParse(m.Groups[1].Value, out var index) && MapClassName(m.Groups[2].Value) is { } objectClass)
            {
                map[index] = objectClass;
            }
        }

        return map;
    }

    private static DetectionModel BuiltIn(string id, string name, string path, DetectionModelFormat format, float confidence, string notes) =>
        new(id, name, path, format, FaceOnly, confidence, notes, File.Exists(path));

    // end2end (YOLO26/YOLOv10 head) or nms (NMS baked into the graph) = final rows [1, K, 6]. else the classic grid
    internal static DetectionModelFormat UltralyticsFormat(IReadOnlyDictionary<string, string> metadata) =>
        IsTrue(metadata, "end2end") || IsTrue(metadata, "nms") ? DetectionModelFormat.YoloEndToEnd : DetectionModelFormat.Yolo;

    private static bool IsTrue(IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(key, out var value) && bool.TryParse(value, out var flag) && flag;

    // a custom model's metadata (cached), or null if it's not a YOLO detect model
    private DetectionModel? DescribeCustom(string file)
    {
        var stamp = File.GetLastWriteTimeUtc(file);
        lock (_gate)
        {
            if (_customCache.TryGetValue(file, out var cached) && cached.Stamp == stamp)
            {
                return cached.Model;
            }
        }

        // the slow part, so outside the lock. two overlapping calls may both load the same file - harmless, same answer.
        // one CPU thread is plenty just to read metadata
        DetectionModel? model = null;
        try
        {
            using var sessionOptions = OnnxDetectorFactory.CreateSessionOptions(1);
            using var session = new InferenceSession(file, sessionOptions);
            var meta = session.ModelMetadata.CustomMetadataMap;
            if (meta.TryGetValue("task", out var task) && task == "detect" && meta.TryGetValue("names", out var names))
            {
                var classes = ParseClassNames(names);
                var fileName = Path.GetFileNameWithoutExtension(file);
                var format = UltralyticsFormat(meta);
                model = new DetectionModel(
                    "custom:" + fileName,
                    format == DetectionModelFormat.YoloEndToEnd ? $"{fileName} (YOLO, NMS-free)" : $"{fileName} (YOLO)",
                    file,
                    format,
                    classes,
                    0.4f,
                    meta.TryGetValue("license", out var licence) ? $"Custom Ultralytics model. Licence: {licence}" : "Custom Ultralytics model.",
                    IsAvailable: classes.Count > 0);
            }
        }
        catch (OnnxRuntimeException ex)
        {
            _logger.LogWarning(ex, "Ignoring {File}: not a loadable ONNX model.", file);
        }

        // "not a model" (null) is cached too, so a broken file isn't loaded again on every refresh
        lock (_gate)
        {
            _customCache[file] = (stamp, model);
        }

        return model;
    }

    // 0: 'person' or 1: "car" -> group 1 = the number, group 2 = the label
    [GeneratedRegex(@"(\d+)\s*:\s*['""]([^'""]*)['""]")]
    private static partial Regex NamesPattern();
}
