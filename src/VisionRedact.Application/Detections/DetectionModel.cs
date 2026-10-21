using VisionRedact.Domain.Detections;

namespace VisionRedact.Application.Detections;

// output layouts the ONNX side can decode. each one has its own profile + decoder (ADR-0021)
public enum DetectionModelFormat
{
    // scores [1,N,2] + boxes [1,N,4], corners already normalized
    UltraFace = 0,

    // YOLO v8/v11 detect head: [1, 4 + classes, N] = cx, cy, w, h in input px + one score per class
    Yolo,

    // NMS-free YOLO (YOLO26, YOLOv10): [1, K, 6] rows = x1, y1, x2, y2, conf, class id in input px.
    // the model already removed duplicates, so no NMS after it. NMS again would drop one of two people hugging
    YoloEndToEnd,
}

// one model in the picker. only a description, DetectorHost does the loading.
// Classes = model class index -> our class, anything not listed (e.g. COCO "cup") is ignored.
// each model scores on its own scale, hence its own DefaultConfidence
public sealed record DetectionModel(
    string Id,
    string DisplayName,
    string FilePath,
    DetectionModelFormat Format,
    IReadOnlyDictionary<int, ObjectClass> Classes,
    float DefaultConfidence,
    // where it's from + license, shown in the picker
    string Notes,
    bool IsAvailable)
{
    // e.g. "Face, Person"
    public string ClassSummary => string.Join(", ", Classes.Values.Distinct());

    public override string ToString() => DisplayName;
}

// OnnxModelCatalog: the built-ins + every YOLO detect .onnx in the models folder
public interface IDetectionModelCatalog
{
    // NOTE: a new custom model gets loaded once just to read its metadata (then cached by file time).
    // so keep it off the UI thread, DetectionPreferences runs it in Task.Run
    IReadOnlyList<DetectionModel> GetModels();
}
