using Microsoft.Extensions.Logging;
using VisionRedact.Application.Detections;
using VisionRedact.Application.Jobs;
using VisionRedact.Application.Media;
using VisionRedact.Domain.Detections;
using VisionRedact.Domain.People;

namespace VisionRedact.Application.People;

// "Identify people", and running it again is the regroup. its own pass after the scan, so the scan stays as fast as
// before and it works on a project opened from disk (ADR-0022).
// Snapshot faces -> FaceShotSelector -> decode only those frames -> Embed -> Mean per track
//   -> LinkConstraints + TrackClusterer -> Prune + ApplyGroups (confirmed links never move)
// fingerprints only live in this job's memory and are gone when it ends, never saved
public sealed class IdentificationJob : ProcessingJob
{
    private readonly string _videoPath;
    private readonly IVideoDecoderFactory _decoders;
    private readonly IFaceEmbedderFactory _embedders;
    private readonly IDetectionRepository _detections;
    private readonly IPeopleStore _people;
    private readonly IdentityOptions _options;

    public IdentificationJob(
        string videoPath,
        IVideoDecoderFactory decoders,
        IFaceEmbedderFactory embedders,
        IDetectionRepository detections,
        IPeopleStore people,
        IdentityOptions? options = null,
        TimeProvider? timeProvider = null,
        ILogger<IdentificationJob>? logger = null)
        : base("Identify people", timeProvider, logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        _videoPath = videoPath;
        _decoders = decoders;
        _embedders = embedders;
        _detections = detections;
        _people = people;
        _options = options ?? IdentityOptions.Default;
    }

    // tracks that got a fingerprint and the groups found, for the People tab's summary line
    public int IdentifiedTracks { get; private set; }

    public int Groups { get; private set; }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // decode + inference block, fine on the job's pool thread
        using var decoder = _decoders.Open(_videoPath);
        // rejected and tiny faces stay in here: they still count for the same-frame rule and for Prune.
        // only the shot picking skips them
        var faces = _detections.Snapshot().Where(d => d is { Class: ObjectClass.Face, TrackId: not null }).ToArray();
        var shots = FaceShotSelector.Select(faces, decoder.Metadata.Width, decoder.Metadata.Height, _options);

        var samples = EmbedShots(decoder, shots, cancellationToken);
        var tracks = samples.ToDictionary(kv => kv.Key, kv => FaceEmbedding.Mean(kv.Value));

        // the rules come from the confirmed links as they are now
        var people = _people.Current;
        var constraints = LinkConstraints.Build(faces, people, _options.DuplicateOverlap);
        var groups = TrackClusterer.Cluster(tracks, constraints, _options.SamePersonThreshold);
        cancellationToken.ThrowIfCancellationRequested();

        // Apply runs this on the newest index, so a rename or merge made while we ran isn't lost
        var live = faces.Select(d => d.TrackId!.Value).ToHashSet();
        _people.Apply(index => index.Prune(live).ApplyGroups(groups));
        (IdentifiedTracks, Groups) = (tracks.Count, groups.Count);
        ReportProgress(shots.Count, shots.Count, $"{groups.Count} people from {tracks.Count} tracks", force: true);
        return Task.CompletedTask;
    }

    // one frame can hold shots of several tracks, so group by frame and decode each frame once
    private Dictionary<int, List<FaceEmbedding>> EmbedShots(IVideoDecoder decoder, IReadOnlyList<FaceShot> shots, CancellationToken cancellationToken)
    {
        var samples = new Dictionary<int, List<FaceEmbedding>>();
        using var embedder = _embedders.Create();
        var byFrame = shots.GroupBy(s => s.FrameIndex).OrderBy(g => g.Key).ToArray();
        var done = 0;
        foreach (var frameShots in byFrame)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var frame = ReadFrame(decoder, frameShots.Key);
            if (frame is not null)
            {
                var embeddings = embedder.Embed(frame, frameShots.Select(s => s.Box).ToArray(), cancellationToken);
                foreach (var (shot, embedding) in frameShots.Zip(embeddings))
                {
                    if (embedding is not null)
                    {
                        (samples.TryGetValue(shot.TrackId, out var list) ? list : samples[shot.TrackId] = []).Add(embedding);
                    }
                }
            }

            done += frameShots.Count();
            ReportProgress(done, shots.Count, $"Reading faces: {done:N0} / {shots.Count:N0}");
        }

        return samples;
    }

    // frames come in ascending order: a short gap is skipped (decode, no picture), a long one (or going back) seeks
    private OwnedVideoFrame? ReadFrame(IVideoDecoder decoder, long target)
    {
        var gap = target - decoder.Position;
        if (gap is < 0 or > int.MaxValue || gap > _options.SeekGap)
        {
            decoder.Seek(target);
        }
        else
        {
            for (var i = 0L; i < gap; i++)
            {
                if (!decoder.Skip())
                {
                    return null;
                }
            }
        }

        return decoder.ReadNext();
    }
}
