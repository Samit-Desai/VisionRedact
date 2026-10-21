using System.Windows.Media;
using Prism.Mvvm;
using VisionRedact.Domain.Detections;
using VisionRedact.Domain.People;

namespace VisionRedact.Presentation.Wpf.Features.People;

// one person row. reused across refreshes (by Id), so a refresh never resets a picture that's still loading or a
// name being edited. the name goes back through a callback, the row holds no services
public sealed class PersonItemViewModel(PersonId id, Action<PersonItemViewModel, string> rename) : BindableBase
{
    private string _name = string.Empty;
    private string _summary = string.Empty;
    private string _redaction = string.Empty;
    private bool _needsCheck;
    private bool _isEditing;
    private ImageSource? _picture;
    private Detection? _pictureFace;

    public PersonId Id { get; } = id;

    // UpdateSourceTrigger=Explicit: only set when the user commits the inline edit
    public string Name
    {
        get => _name;
        set
        {
            if (value != _name)
            {
                rename(this, value);
            }
        }
    }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    // "Redacted", "Visible in export" or "Mixed"
    public string Redaction
    {
        get => _redaction;
        private set => SetProperty(ref _redaction, value);
    }

    // some tracks are only suggested by face matching, a human should confirm them
    public bool NeedsCheck
    {
        get => _needsCheck;
        private set => SetProperty(ref _needsCheck, value);
    }

    public bool IsEditing
    {
        get => _isEditing;
        set => SetProperty(ref _isEditing, value);
    }

    public ImageSource? Picture
    {
        get => _picture;
        private set => SetProperty(ref _picture, value);
    }

    // for "Go to first appearance"
    public long FirstFrame { get; private set; }

    internal Detection? PictureFace => _pictureFace;

    // what screen readers announce for the row
    public override string ToString() => _name;

    internal void Update(string name, string summary, string redaction, bool needsCheck, long firstFrame)
    {
        SetProperty(ref _name, name, nameof(Name));
        Summary = summary;
        Redaction = redaction;
        NeedsCheck = needsCheck;
        FirstFrame = firstFrame;
    }

    // nothing to do if the row already shows this face. the check after the await drops a picture that arrives
    // after the row moved on to another face
    internal async Task ShowPictureAsync(Detection face, Task<ImageSource?> picture)
    {
        if (_pictureFace == face)
        {
            return;
        }

        _pictureFace = face;
        var image = await picture;
        if (_pictureFace == face)
        {
            Picture = image;
        }
    }
}

// one track of the selected person
public sealed class TrackItemViewModel(int trackId, string range, bool isConfirmed, long firstFrame) : BindableBase
{
    private ImageSource? _picture;

    public int TrackId { get; } = trackId;

    public string Title => $"Track {TrackId}";

    public string Range { get; } = range;

    public bool IsConfirmed { get; } = isConfirmed;

    public string Source => IsConfirmed ? "confirmed" : "suggested";

    public long FirstFrame { get; } = firstFrame;

    public ImageSource? Picture
    {
        get => _picture;
        private set => SetProperty(ref _picture, value);
    }

    internal async Task ShowPictureAsync(Task<ImageSource?> picture) => Picture = await picture;

    // for screen readers: title, range, confirmed or suggested
    public override string ToString() => $"{Title}, {Range}, {Source}";
}
