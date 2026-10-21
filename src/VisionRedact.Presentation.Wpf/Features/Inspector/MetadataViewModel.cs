using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Prism.Events;
using Prism.Mvvm;
using VisionRedact.Application.Playback;
using VisionRedact.Presentation.Wpf.Events;
using VisionRedact.Presentation.Wpf.Infrastructure;

namespace VisionRedact.Presentation.Wpf.Features.Inspector;

// value already formatted, e.g. "1920 x 1080"
public sealed record MetadataItem(string Label, string Value);

// facts about the open video, for the Video tab. only listens to VideoOpenedEvent, knows nothing about the player
public sealed class MetadataViewModel : BindableBase
{
    private string _fileName = "No video loaded";

    public MetadataViewModel(IEventAggregator events)
    {
        // UIThread whoever publishes: Items is bound, so it may only change on the UI thread
        events.GetEvent<VideoOpenedEvent>().Subscribe(OnVideoOpened, ThreadOption.UIThread);
    }

    public string FileName
    {
        get => _fileName;
        private set => SetProperty(ref _fileName, value);
    }

    public ObservableCollection<MetadataItem> Items { get; } = [];

    private void OnVideoOpened(PlaybackSession session)
    {
        // user's culture, this text is for people
        var m = session.Metadata;
        var culture = CultureInfo.CurrentCulture;
        FileName = Path.GetFileName(m.SourcePath);

        Items.Clear();
        Items.Add(new MetadataItem("Resolution", $"{m.Width} × {m.Height}"));
        Items.Add(new MetadataItem("Aspect ratio", m.AspectRatio.ToString("0.###", culture)));
        Items.Add(new MetadataItem("Frame rate", string.Create(culture, $"{m.FramesPerSecond:0.###} fps")));
        Items.Add(new MetadataItem("Frame count", m.FrameCount.ToString("N0", culture)));
        Items.Add(new MetadataItem("Duration", DisplayFormat.Timecode(m.Duration)));
        Items.Add(new MetadataItem("File size", DisplayFormat.FileSize(m.FileSizeBytes)));
        Items.Add(new MetadataItem("Codec", m.Codec));
        Items.Add(new MetadataItem("Decoder", session.DecoderBackend));
        Items.Add(new MetadataItem("Location", Path.GetDirectoryName(m.SourcePath) ?? string.Empty));
    }
}
