using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Prism.Commands;
using VisionRedact.Application.Media;
using VisionRedact.Application.Preview;
using VisionRedact.Presentation.Wpf.Infrastructure;

namespace VisionRedact.Presentation.Wpf.Features.Player;

// the timeline hover card: the nearest storyboard tile at once on every mouse move, then the exact frame from
// FramePreviewService once the pointer rests (ADR-0023). the first version only had the exact frame, so the card
// showed nothing while the mouse moved.
// tiles are read on the UI thread (the storyboard is immutable). Ready comes on a pool thread -> Post, and a picture
// for a frame the pointer already left is dropped.
// one bitmap: tiles and exact frames have the same size, so only its pixels change, no new objects per mouse move
public sealed partial class PlayerViewModel
{
    private FramePreviewService _previews = null!;
    private WriteableBitmap? _hoverBitmap;
    private long? _hoverFrame;
    private long? _shownPictureFrame; // what's in the bitmap now, tile or exact
    private string? _hoverText;

    // from the timeline: the hovered frame, or null when the pointer leaves
    public DelegateCommand<long?> HoverFrameCommand { get; private set; } = null!;

    // null until the first tile or frame of this video
    public ImageSource? HoverPreview => _hoverBitmap;

    public string? HoverText
    {
        get => _hoverText;
        private set => SetProperty(ref _hoverText, value);
    }

    private void InitializeHoverPreview(FramePreviewService previews)
    {
        _previews = previews;
        HoverFrameCommand = new DelegateCommand<long?>(OnHoverFrame);
        _previews.Ready += OnPreviewReady;
    }

    private void DisposeHoverPreview()
    {
        _previews.Ready -= OnPreviewReady;
        _previews.Cancel();
    }

    // new video: drop the old picture (its size may differ), start the new storyboard
    private void ResetHoverPreview()
    {
        _hoverFrame = null;
        _shownPictureFrame = null;
        _hoverBitmap = null;
        RaisePropertyChanged(nameof(HoverPreview));
        HoverText = null;
        _previews.LoadStoryboard();
    }

    private void OnHoverFrame(long? frame)
    {
        _hoverFrame = frame;
        if (frame is not { } f || _metadata is null)
        {
            _previews.Cancel();
            return;
        }

        // instant: timecode + nearest tile, no decoding. the exact frame only decodes after the pointer rests
        HoverText = DisplayFormat.Timecode(_metadata.TimeOfFrame(f));
        if (_previews.NearestTile(f) is { } tile && tile.FrameIndex != _shownPictureFrame)
        {
            ShowPicture(tile.FrameIndex, tile.Image);
        }

        _previews.Request(f);
    }

    // pool thread -> UI thread. the image is an immutable managed buffer, safe to hand over
    private void OnPreviewReady(object? sender, FramePreview preview) => _ui.Post(() =>
    {
        if (preview.FrameIndex == _hoverFrame)
        {
            ShowPicture(preview.FrameIndex, preview.Image); // pointer still there: the exact frame replaces the tile
        }
    });

    private void ShowPicture(long frameIndex, ImageBuffer image)
    {
        if (_hoverBitmap is null || _hoverBitmap.PixelWidth != image.Width || _hoverBitmap.PixelHeight != image.Height)
        {
            _hoverBitmap = new WriteableBitmap(image.Width, image.Height, 96, 96, PixelFormats.Bgr24, null);
            RaisePropertyChanged(nameof(HoverPreview));
        }

        _hoverBitmap.WritePixels(new Int32Rect(0, 0, image.Width, image.Height), image.Pixels, image.Stride, 0);
        _shownPictureFrame = frameIndex;
    }
}
