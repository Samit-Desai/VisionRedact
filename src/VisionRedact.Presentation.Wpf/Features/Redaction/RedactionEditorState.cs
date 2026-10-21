using Prism.Mvvm;
using VisionRedact.Application.Redaction;

namespace VisionRedact.Presentation.Wpf.Features.Redaction;

// editing state the player and the Redaction tab share (one singleton). click a region on the video and the tab
// selects it too, and back, w/o the two view models knowing each other. UI thread only
public sealed class RedactionEditorState : BindableBase
{
    private readonly RedactionPreviewProcessor _preview;
    private Guid? _selectedRegionId;
    private bool _isDrawing;

    public RedactionEditorState(RedactionPreviewProcessor preview)
    {
        _preview = preview;
    }

    // an id, not a row, so it survives list rebuilds. null = none
    public Guid? SelectedRegionId
    {
        get => _selectedRegionId;
        set => SetProperty(ref _selectedRegionId, value);
    }

    // drag on the video = a new region
    public bool IsDrawing
    {
        get => _isDrawing;
        set => SetProperty(ref _isDrawing, value);
    }

    // on: the preview shows what the export will produce. off: outlines only
    public bool IsPreviewEnabled
    {
        get => _preview.IsEnabled;
        set
        {
            // no backing field: the flag lives in the preview processor, the playback redaction task reads it
            if (_preview.IsEnabled != value)
            {
                _preview.IsEnabled = value;
                RaisePropertyChanged();
            }
        }
    }
}
