using System.Globalization;
using Prism.Mvvm;
using VisionRedact.Domain.Detections;
using VisionRedact.Presentation.Wpf.Infrastructure;

namespace VisionRedact.Presentation.Wpf.Features.Detections;

// one row of the review list. thin on purpose, a long scan makes a lot of these.
// the strings are built by the bindings, and w/ virtualization only the rows on screen bind
public sealed class DetectionItemViewModel : BindableBase
{
    // the row never writes the repository itself, the list view model does
    private readonly Action<DetectionItemViewModel, ReviewStatus> _setStatus;
    // swapped by Update when the repository has a newer version
    private Detection _detection;

    public DetectionItemViewModel(Detection detection, int number, TimeSpan time, Action<DetectionItemViewModel, ReviewStatus> setStatus)
    {
        _detection = detection;
        Number = number;
        Time = time;
        _setStatus = setStatus;
    }

    public Detection Detection => _detection;

    public Guid Id => _detection.Id;

    public long FrameIndex => _detection.FrameIndex;

    // per class, from the list: the 12 in "Face #12"
    public int Number { get; }

    public TimeSpan Time { get; }

    // the track part only when the tracker linked it
    public string Title => _detection.TrackId is { } track
        ? $"{_detection.Class} #{Number} · Track {track}"
        : $"{_detection.Class} #{Number}";

    public string Confidence => _detection.Confidence.ToString("P0", CultureInfo.CurrentCulture);

    public string Timecode => DisplayFormat.Timecode(Time);

    public ReviewStatus Status => _detection.Status;

    // the Redact checkbox. unchecked = false positive (Rejected), checked = Approved
    public bool IsRedacted
    {
        get => _detection.IsRedacted;
        set
        {
            // nothing changes here. the owner updates the repository and the new value comes back through Update,
            // so the repository stays the one source of truth
            if (value != _detection.IsRedacted)
            {
                _setStatus(this, value ? ReviewStatus.Approved : ReviewStatus.Rejected);
            }
        }
    }

    // UI thread, from the batch flush
    public void Update(Detection detection)
    {
        _detection = detection;
        // only what a review or the tracker can change
        RaisePropertyChanged(nameof(IsRedacted));
        RaisePropertyChanged(nameof(Status));
        RaisePropertyChanged(nameof(Title));
    }
}
