using VisionRedact.Application.Detections;
using VisionRedact.Application.People;
using VisionRedact.Application.Playback;
using VisionRedact.Application.Redaction;
using VisionRedact.Application.Workspace;
using VisionRedact.Domain.People;

namespace VisionRedact.Application.Projects;

// SourceMissing = the video isn't at the saved path any more: the UI asks where it is, then calls RelinkAsync
public sealed record ProjectOpenResult(ProjectDocument Document, PlaybackSession? Session, bool SourceMissing, bool SourceChanged);

// ShellViewModel -> Save: live stores -> ProjectDocument -> IProjectRepository.
// Open: document -> open the video (resets the stores) -> fill the stores again
public sealed class ProjectService
{
    public const string Extension = ".vrproject";

    private readonly IProjectRepository _repository;
    private readonly VideoWorkspace _workspace;
    private readonly IDetectionRepository _detections;
    private readonly IRedactionRegionStore _regions;
    private readonly TimeProvider _time;
    private readonly IPeopleStore? _people;

    public ProjectService(
        IProjectRepository repository,
        VideoWorkspace workspace,
        IDetectionRepository detections,
        IRedactionRegionStore regions,
        TimeProvider? timeProvider = null,
        IPeopleStore? people = null)
    {
        _repository = repository;
        _workspace = workspace;
        _detections = detections;
        _regions = regions;
        _time = timeProvider ?? TimeProvider.System;
        _people = people;
    }

    public async Task SaveAsync(string path, IReadOnlyList<string> models, CancellationToken cancellationToken = default)
    {
        var session = _workspace.CurrentSession ?? throw new InvalidOperationException("Open a video before saving a project.");

        // copies only (Snapshot(), the immutable region array, the immutable people index), so edits made while
        // it's being written can't change it. the video itself is only a path + fingerprint
        var m = session.Metadata;
        var document = new ProjectDocument(
            m.SourcePath,
            new SourceFingerprint(m.FileSizeBytes, m.Width, m.Height, m.FrameCount, m.FramesPerSecond),
            models,
            _detections.Snapshot(),
            _detections.AnalyzedFrames(),
            _detections.HoldFrames,
            _regions.Regions,
            _regions.Settings,
            _time.GetUtcNow())
        {
            People = _people?.Current ?? PeopleIndex.Empty,
        };
        await _repository.SaveAsync(path, document, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectOpenResult> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        var document = await _repository.LoadAsync(path, cancellationToken).ConfigureAwait(false);

        // moved or deleted: don't fail, hand the document back so the UI can ask where the video is now
        if (!File.Exists(document.SourcePath))
        {
            return new ProjectOpenResult(document, null, SourceMissing: true, SourceChanged: false);
        }

        return await ApplyAsync(document, document.SourcePath, cancellationToken).ConfigureAwait(false);
    }

    public Task<ProjectOpenResult> RelinkAsync(ProjectDocument document, string newSourcePath, CancellationToken cancellationToken = default) =>
        ApplyAsync(document with { SourcePath = newSourcePath }, newSourcePath, cancellationToken);

    private async Task<ProjectOpenResult> ApplyAsync(ProjectDocument document, string sourcePath, CancellationToken cancellationToken)
    {
        // open the video first (that resets the live stores), then fingerprint what really opened.
        // everything below runs after ConfigureAwait(false), so usually not on the UI thread
        var session = await _workspace.OpenAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var m = session.Metadata;
        var actual = new SourceFingerprint(m.FileSizeBytes, m.Width, m.Height, m.FrameCount, m.FramesPerSecond);

        // boxes past the end of this video (another or a truncated file) are dropped, not trusted
        var detections = document.Detections.Where(d => m.ContainsFrame(d.FrameIndex));
        _detections.Load(m.FrameCount, detections, document.AnalyzedFrames, document.HoldFrames);
        // same for regions that start after the end
        _regions.Load(document.Regions.Where(r => r.StartFrame < m.FrameCount), document.Settings);
        // people: keep only the tracks whose boxes survived
        var liveTracks = _detections.Snapshot().Select(d => d.TrackId).OfType<int>().ToHashSet();
        _people?.Load(document.People.Prune(liveTracks));

        // redraw the current frame, so the loaded blurs show right away
        session.Refresh();

        // SourceChanged -> the UI warns that this may not be the same video
        return new ProjectOpenResult(document with { SourcePath = sourcePath }, session, SourceMissing: false, SourceChanged: !actual.Matches(document.Source));
    }
}
