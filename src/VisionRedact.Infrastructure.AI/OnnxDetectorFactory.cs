using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using VisionRedact.Application.Detections;

namespace VisionRedact.Infrastructure.AI;

// loads a model on the chosen device and checks its input/output shape before any frame, so a wrong or broken model
// fails once, w/ a clear message, instead of giving junk boxes on every frame.
// DetectorHost (caches one per model + device) -> Create -> OnnxObjectDetector
public sealed class OnnxDetectorFactory : IObjectDetectorFactory
{
    private readonly DetectorOptions _options;
    private readonly ILogger _logger;

    // a new model family = one more profile here (ADR-0021)
    private readonly IReadOnlyDictionary<DetectionModelFormat, IModelFormatProfile> _profiles;

    public OnnxDetectorFactory(DetectorOptions options, ILogger<OnnxDetectorFactory>? logger = null)
    {
        _options = options;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _profiles = new IModelFormatProfile[] { new UltraFaceProfile(options), new YoloProfile(options), new YoloEndToEndProfile(options) }
            .ToDictionary(p => p.Format);
    }

    public IObjectDetector Create(DetectionModel model, InferenceTarget target)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(target);

        if (!File.Exists(model.FilePath))
        {
            throw new DetectionException(
                DetectionErrorKind.ModelNotFound,
                $"The model file for '{model.DisplayName}' was not found at '{model.FilePath}'. Run scripts/download-model.ps1 to download the built-in models.");
        }

        // set to null once the detector owns it, so finally only disposes it on a failure
        InferenceSession? session = null;
        try
        {
            string provider;
            try
            {
                (session, provider) = CreateSession(model.FilePath, target);
            }
            catch (OnnxRuntimeException ex) when (target.Device == InferenceDevice.DirectML)
            {
                // the GPU is there but this model/driver can't run on it: go CPU, don't fail (ADR-0017).
                // the 2nd fallback: no DirectML provider at all is handled in ConfigureExecutionProvider
                _logger.LogWarning(ex, "DirectML session creation failed on {Gpu}; falling back to CPU.", target.Name);
                (session, provider) = CreateSession(model.FilePath, InferenceTarget.Cpu);
                provider = $"CPU (GPU failed: {target.Name})";
            }

            // input here, the profile does the outputs, preprocessing and decoder
            var (inputName, width, height) = ValidateInput(session, model);
            if (!_profiles.TryGetValue(model.Format, out var profile))
            {
                throw new DetectionException(DetectionErrorKind.InvalidModel, $"Unsupported model format {model.Format}.");
            }

            var (spec, decoder) = profile.Bind(session, model, width, height);

            // info = what the UI shows: model, input size, the device really used
            var info = new DetectorInfo(model.DisplayName, Path.GetFileName(model.FilePath), model.ClassSummary, width, height, provider);
            var detector = new OnnxObjectDetector(session, info, inputName, spec, decoder, _options, _options.ConfidenceFor(model));
            session = null; // the detector owns it now
            return detector;
        }
        // our own DetectionException passes through. raw ONNX / native-load errors get a kind, so the UI shows one
        // clear message
        catch (DetectionException)
        {
            throw;
        }
        catch (OnnxRuntimeException ex)
        {
            _logger.LogError(ex, "ONNX Runtime could not load {Model}.", model.FilePath);
            throw new DetectionException(DetectionErrorKind.InvalidModel, $"'{Path.GetFileName(model.FilePath)}' is not a valid ONNX model: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or TypeInitializationException)
        {
            _logger.LogError(ex, "ONNX Runtime native library could not be loaded.");
            throw new DetectionException(DetectionErrorKind.RuntimeUnavailable, "The ONNX Runtime native library (onnxruntime.dll / DirectML.dll) could not be loaded.", ex);
        }
        finally
        {
            session?.Dispose();
        }
    }

    private (InferenceSession Session, string Provider) CreateSession(string path, InferenceTarget target)
    {
        using var sessionOptions = CreateSessionOptions(_options.IntraOpThreads);
        var provider = ConfigureExecutionProvider(sessionOptions, target);
        return (new InferenceSession(path, sessionOptions), provider);
    }

    // also used by ArcFaceEmbedder and the catalog's metadata read.
    // ENABLE_ALL = let ORT fuse/simplify nodes. SEQUENTIAL = DirectML needs it. IntraOpNumThreads = CPU threads inside one op
    internal static SessionOptions CreateSessionOptions(int intraOpThreads) => new()
    {
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        IntraOpNumThreads = intraOpThreads,

        // some model-zoo exports keep initializers as graph inputs = one warning per tensor. real problems throw anyway
        LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
    };

    private string ConfigureExecutionProvider(SessionOptions sessionOptions, InferenceTarget target)
    {
        if (target.Device != InferenceDevice.DirectML)
        {
            return "CPU";
        }

        try
        {
            // DirectML wants sequential execution (CreateSessionOptions) and no memory pattern.
            // device id = the DXGI adapter index (DxgiAdapterCatalog). nodes it can't run go to the CPU provider by itself
            sessionOptions.EnableMemoryPattern = false;
            sessionOptions.AppendExecutionProvider_DML(target.AdapterIndex);
            return $"DirectML: {target.Name}";
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or EntryPointNotFoundException or NotSupportedException)
        {
            _logger.LogWarning(ex, "DirectML execution provider unavailable; falling back to CPU.");
            return "CPU (DirectML unavailable)";
        }
    }

    // one float input [N, 3, H, W] -> its name and px size
    private static (string Name, int Width, int Height) ValidateInput(InferenceSession session, DetectionModel model)
    {
        if (session.InputMetadata.Count != 1)
        {
            throw ModelContract.Violation(model, "expected exactly one input");
        }

        var (name, meta) = session.InputMetadata.First();
        var dims = meta.Dimensions;
        if (meta.ElementType != typeof(float) || dims.Length != 4 || dims[1] != 3)
        {
            throw ModelContract.Violation(model, $"expected a float input [N, 3, H, W], got [{string.Join(", ", dims)}]");
        }

        // dynamic H/W (-1): the size it was trained at (YOLO exports store "imgsz"), else 640.
        // this used to be a flat 640, but YOLO26l's input is dynamic and it was trained at 1280 (ADR-0021)
        var (trainedWidth, trainedHeight) = TrainedImageSize(session) ?? (640, 640);
        var height = dims[2] > 0 ? dims[2] : trainedHeight;
        var width = dims[3] > 0 ? dims[3] : trainedWidth;
        return (name, width, height);
    }

    // "imgsz" metadata: "[1280, 1280]" (h, w) or "640"
    internal static (int Width, int Height)? TrainedImageSize(InferenceSession session) =>
        session.ModelMetadata.CustomMetadataMap.TryGetValue("imgsz", out var text) ? ParseImageSize(text) : null;

    // anything else, or a size outside 1..8192 -> null
    internal static (int Width, int Height)? ParseImageSize(string text)
    {
        var numbers = text.Trim('[', ']', ' ').Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, out var n) && n is > 0 and <= 8192 ? n : 0)
            .ToArray();
        return numbers switch
        {
            [var size] when size > 0 => (size, size),
            [var height, var width] when height > 0 && width > 0 => (width, height),
            _ => null,
        };
    }
}
