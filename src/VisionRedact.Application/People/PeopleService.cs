using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VisionRedact.Application.Detections;
using VisionRedact.Application.Jobs;
using VisionRedact.Application.Media;
using VisionRedact.Domain.Detections;
using VisionRedact.Domain.Jobs;
using VisionRedact.Domain.People;

namespace VisionRedact.Application.People;

// what the People tab can do: start Identify, "Redact all", "Keep visible".
// a person has no blur flag. both actions change the statuses of its tracks' detections, which export already reads.
// re-scan boxes come in new tracks w/o a person, and Pending is blurred: a decision can only go stale the safe way
public sealed class PeopleService
{
    private readonly IPeopleStore _people;
    private readonly IDetectionRepository _detections;
    private readonly IProcessingJobManager _jobs;
    private readonly IVideoDecoderFactory _decoders;
    private readonly IFaceEmbedderFactory _embedders;
    private readonly IdentityOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private volatile IdentificationJob? _current; // set on the UI thread, read from any

    public PeopleService(
        IPeopleStore people,
        IDetectionRepository detections,
        IProcessingJobManager jobs,
        IVideoDecoderFactory decoders,
        IFaceEmbedderFactory embedders,
        IdentityOptions? options = null,
        ILoggerFactory? loggerFactory = null)
    {
        _people = people;
        _detections = detections;
        _jobs = jobs;
        _decoders = decoders;
        _embedders = embedders;
        _options = options ?? IdentityOptions.Default;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    // false when the face models aren't installed
    public bool IsAvailable => _embedders.IsAvailable;

    public bool IsIdentifying => _current is { } job && !JobStateMachine.IsTerminal(job.State);

    // one run at a time
    public IdentificationJob StartIdentification(string videoPath)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("The face models are not installed. Run scripts/download-face-models.ps1.");
        }

        if (IsIdentifying)
        {
            throw new InvalidOperationException("People are already being identified.");
        }

        var job = new IdentificationJob(videoPath, _decoders, _embedders, _detections, _people, _options, logger: _loggerFactory.CreateLogger<IdentificationJob>());
        _current = job;
        _jobs.Start(job);
        return job;
    }

    // approves (= blurs) every detection of all the person's tracks, suggested and confirmed. returns how many changed
    public int RedactAll(PersonId person)
    {
        var tracks = _people.Current.TracksOf(person).ToHashSet();
        return _detections.UpdateStatusWhere(d => d.TrackId is { } t && tracks.Contains(t), ReviewStatus.Approved);
    }

    // take care here: only the CONFIRMED tracks get Rejected (= shown). a stranger wrongly grouped with this person
    // is only a suggestion, so they stay blurred until a human confirms that track. returns how many changed
    public int KeepVisible(PersonId person)
    {
        var confirmed = _people.Current.Assignments
            .Where(a => a.PersonId == person && a.IsConfirmed)
            .Select(a => a.TrackId)
            .ToHashSet();
        return _detections.UpdateStatusWhere(d => d.TrackId is { } t && confirmed.Contains(t), ReviewStatus.Rejected);
    }

    // for the question PeopleViewModel asks before "Keep visible" (and it refuses at 0 confirmed)
    public (int Confirmed, int Suggested) CountTracks(PersonId person)
    {
        var assignments = _people.Current.Assignments.Where(a => a.PersonId == person).ToArray();
        var confirmed = assignments.Count(a => a.IsConfirmed);
        return (confirmed, assignments.Length - confirmed);
    }
}
