using VisionRedact.Domain.Detections;
using VisionRedact.Domain.People;
using VisionRedact.Domain.Redaction;

namespace VisionRedact.Application.Projects;

// what a .vrproject holds, the port that saves it, and its errors. the JSON version is in Infrastructure.Persistence

// a cheap check that it's the same video: size, resolution, frame count, fps. no hash of a multi-GB file.
// not proof, just enough to warn on open or relink (ProjectOpenResult.SourceChanged)
public sealed record SourceFingerprint(long FileSizeBytes, int Width, int Height, long FrameCount, double FramesPerSecond)
{
    // fps is a double, so a small tolerance
    public bool Matches(SourceFingerprint other) =>
        FileSizeBytes == other.FileSizeBytes && Width == other.Width && Height == other.Height
        && FrameCount == other.FrameCount && Math.Abs(FramesPerSecond - other.FramesPerSecond) < 0.01;
}

// the saved review: the video's path (never the video itself), every detection w/ its status, the scanned frames,
// the drawn regions and the settings. ProjectService builds and reads it.
// Models = the model names picked when saving, for the record only. nothing reads them back
public sealed record ProjectDocument(
    string SourcePath,
    SourceFingerprint Source,
    IReadOnlyList<string> Models,
    IReadOnlyList<Detection> Detections,
    IReadOnlyList<long> AnalyzedFrames,
    int HoldFrames,
    IReadOnlyList<RedactionRegion> Regions,
    RedactionSettings Settings,
    DateTimeOffset SavedAt)
{
    // who is who: names, which tracks are whose, confirmed or not (schema 2). Empty for older projects.
    // never the face fingerprints or face pictures: biometric data, rebuilt from the video when needed (ADR-0022)
    public PeopleIndex People { get; init; } = PeopleIndex.Empty;
}

// JSON on disk today (JsonProjectRepository)
public interface IProjectRepository
{
    /// <summary>Atomic: a temp file, then a rename, so a crash never leaves half a project.</summary>
    Task SaveAsync(string path, ProjectDocument document, CancellationToken cancellationToken = default);

    /// <exception cref="ProjectException">Missing, unreadable, corrupt or saved by a newer version.</exception>
    Task<ProjectDocument> LoadAsync(string path, CancellationToken cancellationToken = default);
}

// the UI shows the exception's Message, only the tests look at the kind so far
public enum ProjectErrorKind
{
    Unknown = 0,
    NotFound,
    AccessDenied,
    Corrupt,
    UnsupportedVersion,
}

// the one exception for expected project problems
public sealed class ProjectException : Exception
{
    public ProjectException(ProjectErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public ProjectException()
        : this(ProjectErrorKind.Unknown, "Project error.")
    {
    }

    public ProjectException(string message)
        : this(ProjectErrorKind.Unknown, message)
    {
    }

    public ProjectException(string message, Exception innerException)
        : this(ProjectErrorKind.Unknown, message, innerException)
    {
    }

    public ProjectErrorKind Kind { get; }
}
