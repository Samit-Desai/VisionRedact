using VisionRedact.Domain.Geometry;

namespace VisionRedact.Domain.Detections;

// what kind of thing a box is. the scan doesn't care, new detectors just add a value
public enum ObjectClass
{
    Face = 0,
    LicensePlate,
    Person,
    Vehicle,
    Text,
}

// Pending is blurred too (ADR-0014). a missed face is a leak, an extra blur is only cosmetic
public enum ReviewStatus
{
    Pending = 0,
    Approved,
    Rejected,
}

/// <summary>One box the AI found on one frame.</summary>
// detector -> tracker (TrackId) -> review -> blur unless rejected
// immutable on purpose, the scan thread and the UI share these w/o locks
public sealed record Detection(
    Guid Id,
    long FrameIndex,
    NormalizedRect Box,
    float Confidence,
    ObjectClass Class,
    ReviewStatus Status = ReviewStatus.Pending,
    // good find - ids used to restart at 1 each scan, so a re-scan could reuse an approved face's id (ADR-0024)
    int? TrackId = null)
{
    // only a reject un-blurs
    public bool IsRedacted => Status != ReviewStatus.Rejected;

    public static Detection Create(long frameIndex, NormalizedRect box, float confidence, ObjectClass objectClass) =>
        new(Guid.NewGuid(), frameIndex, box, confidence, objectClass);
}
