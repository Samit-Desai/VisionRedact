using VisionRedact.Domain.Detections;

namespace VisionRedact.Application.Detections;

// one batch of changes, raised once per batch, never once per box (one scanned frame can add dozens).
// DetectionsViewModel queues these and applies them to its list on a UI timer
public sealed class DetectionsChangedEventArgs(
    IReadOnlyList<Detection> added,
    IReadOnlyList<Detection> updated,
    IReadOnlyList<Guid> removed,
    bool isReset) : EventArgs
{
    public IReadOnlyList<Detection> Added { get; } = added;

    public IReadOnlyList<Detection> Updated { get; } = updated;

    public IReadOnlyList<Guid> Removed { get; } = removed;

    // new video or project load: re-read a Snapshot
    public bool IsReset { get; } = isReset;
}

// the box store of the open video: every detection by frame, and which frames were scanned.
// AnalysisJob + FrameDetectionService write, review and PeopleService change statuses,
// the player, preview blur, People tab, project save and export read (export from its own copy)
public interface IDetectionRepository
{
    // raised on the writer's thread. UI listeners must marshal (and should batch)
    event EventHandler<DetectionsChangedEventArgs>? Changed;

    // bumped on every change, so pollers (player, People tab) can skip work when nothing changed
    long Version { get; }

    int Count { get; }

    // distinct frames scanned so far
    long AnalyzedFrameCount { get; }

    // highest TrackId stored since the last Reset/Load, 0 if none. never goes down, even when boxes are removed.
    // a new scan numbers its tracks from here + 1 (ADR-0024): reviewed boxes survive a re-scan, and a reused number
    // would make GetForDisplay slide a box toward the wrong face
    int HighestTrackId { get; }

    // how many frames after a scanned frame its boxes still count (sampling interval - 1).
    // e.g. every 5th frame: frame 100's boxes also cover 101..104
    int HoldFrames { get; set; }

    // clears everything and sizes the scanned-frame bit set for this video
    void Reset(long frameCount);

    // one frame's AI result. drops the old Pending boxes on that frame (reviewed ones stay, so a re-scan never
    // throws away human work) and marks the frame scanned, even with no boxes
    void AddFrameResults(long frameIndex, IReadOnlyList<Detection> detections);

    // project open, and the export's private copy. raises one reset event
    void Load(long frameCount, IEnumerable<Detection> detections, IEnumerable<long> analyzedFrames, int holdFrames);

    Detection? Get(Guid id);

    // only what's stored on exactly this frame, no hold, no sliding
    IReadOnlyList<Detection> GetForFrame(long frameIndex);

    /// <summary>Boxes to draw and blur here: the nearest scan within HoldFrames, slid toward the next scan.</summary>
    IReadOnlyList<Detection> GetForDisplay(long frameIndex);

    // all of them, by frame, then confidence high to low
    IReadOnlyList<Detection> Snapshot();

    // ascending, saved with the project
    IReadOnlyList<long> AnalyzedFrames();

    // one review click on one box. false if the id is unknown or it already had that status
    bool UpdateStatus(Guid id, ReviewStatus status);

    // a whole track, "approve all pending", or a person's tracks. returns how many changed
    int UpdateStatusWhere(Func<Detection, bool> predicate, ReviewStatus status);

    bool Remove(Guid id);
}
