using VisionRedact.Application.Media;
using VisionRedact.Domain.Geometry;
using VisionRedact.Domain.People;

namespace VisionRedact.Application.People;

// face -> fingerprint. all the model stuff (crop, landmarks, align, input size) stays inside ArcFaceEmbedder,
// so nothing else in the People feature knows which model runs. one job uses one instance
public interface IFaceEmbedder : IDisposable
{
    // stored on every FaceEmbedding, so fingerprints of two models are never compared
    string ModelId { get; }

    /// <summary>One result per box, same order. Null = no usable face (tiny, blurred, turned away, false box).</summary>
    IReadOnlyList<FaceEmbedding?> Embed(OwnedVideoFrame frame, IReadOnlyList<NormalizedRect> faces, CancellationToken cancellationToken = default);
}

// ArcFaceEmbedderFactory
public interface IFaceEmbedderFactory
{
    // false when the face model files are missing. Identify stays off and the People tab says how to get them
    bool IsAvailable { get; }

    IFaceEmbedder Create();
}

// registered as IdentityOptions.Default in App.xaml.cs
public sealed record IdentityOptions
{
    public static IdentityOptions Default { get; } = new();

    // spread over the track's time. more = steadier fingerprint, but slower
    public int ShotsPerTrack { get; init; } = 3;

    // box height in source px. smaller faces have too little detail
    public int MinFacePixels { get; init; } = 32;

    // min average cosine for two groups to be one person. a starting value for ArcFace, kept here so it can be tuned.
    // any other model needs its own
    public float SamePersonThreshold { get; init; } = 0.35f;

    // same frame = two people, unless the boxes overlap at least this much (IoU). then it's one face reported twice
    public double DuplicateOverlap { get; init; } = 0.3;

    // gaps up to this many frames are skipped (decode only), longer ones seek
    public int SeekGap { get; init; } = 60;
}

// one face to fingerprint: which track, which frame, where
public readonly record struct FaceShot(int TrackId, long FrameIndex, NormalizedRect Box);
