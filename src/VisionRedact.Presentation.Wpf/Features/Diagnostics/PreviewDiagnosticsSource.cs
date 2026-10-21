using System.Globalization;
using VisionRedact.Application.Preview;
using VisionRedact.Presentation.Wpf.Infrastructure;

namespace VisionRedact.Presentation.Wpf.Features.Diagnostics;

// the "Timeline previews" rows: what hover previews cost on top of playback (decoders, native frames, tile memory),
// to show it stays bounded however long the video and however fast the mouse.
// the frame reader also makes the People tab's face pictures, so its rows count those reads too (the hints say so)
public sealed class PreviewDiagnosticsSource(FramePreviewService previews) : IDiagnosticsSource
{
    private const int MinimumLookups = 8;

    public string Group => "Timeline previews";

    public IReadOnlyList<DiagnosticsMetric> Metrics { get; } =
    [
        new("previewTiles", "Storyboard tiles", "Tiles built / planned for the open video. The plan is capped (240 tiles), so a longer video gets wider spacing, not more tiles"),
        new("previewTileMemory", "Tile memory", "Managed memory of all storyboard tiles (~62 KB each). Bounded by the tile cap: about 15 MB at most"),
        new("previewBuildTime", "Storyboard build time", "Time the background builder has run so far; its final time once every tile is built"),
        new("previewDecoders", "Preview decoders open", "Extra decoders used by previews: frame reader (opened on the first hover or face thumbnail, closed with the video) and storyboard (closed when building ends). Never more than 2", StartsSection: true),
        new("previewFrames", "Preview frames in flight", "Native frames held by the frame reader and the storyboard builder right now; each is released right after it is shrunk. Peak adds up the most each decoder has held: at most 2"),
        new("previewLookup", "Tile lookup", "Median time of the last 64 nearest-tile lookups on mouse moves: a binary search, no decoding. A median, so one slow first call does not hide the typical cost", StartsSection: true),
        new("previewFrameRead", "Frame read time", "Mean seek + decode + resize time of one read on the shared frame reader (hover exact frames and People-tab face thumbnails). The 100 ms hover settle delay is not included"),
        new("previewRequests", "Hover requests", "Exact-frame requests · shown · superseded. Superseded = replaced by a newer hover (or the pointer left) before being shown, usually before any decoding"),
    ];

    public IEnumerable<(string Key, string Value)> Sample(TimeSpan timestamp)
    {
        var c = CultureInfo.CurrentCulture;
        // one snapshot, the preview code updates these on other threads
        var stats = previews.GetStatistics();
        var tiles = $"{stats.TilesBuilt.ToString("N0", c)} / {stats.TilesPlanned.ToString("N0", c)}";
        yield return ("previewTiles", stats.StoryboardBuilding ? tiles + " · building" : tiles);
        yield return ("previewTileMemory", DisplayFormat.FileSize(stats.TileBytes));
        yield return ("previewBuildTime", stats.StoryboardBuildTime == TimeSpan.Zero ? "–" : stats.StoryboardBuildTime.TotalSeconds.ToString("0.0 s", c));
        yield return ("previewDecoders", Decoders(stats));
        yield return ("previewFrames", $"{stats.FramesInFlight} (peak {stats.PeakFramesInFlight})");
        // a median, and nothing until there are a few: the first calls are JIT-cold.
        // good find - the first version showed a mean, and that one cold first call was most of it
        yield return ("previewLookup", stats.TileLookups < MinimumLookups ? "–" : stats.MedianTileLookupNanoseconds.ToString("0 ns median", c));
        yield return ("previewFrameRead", stats.AverageFrameRead == TimeSpan.Zero ? "–" : stats.AverageFrameRead.TotalMilliseconds.ToString("0.0 ms avg", c));
        yield return ("previewRequests", $"{stats.ExactRequests.ToString("N0", c)} · {stats.ExactShown.ToString("N0", c)} shown · {stats.ExactSuperseded.ToString("N0", c)} superseded");
    }

    private static string Decoders(FramePreviewStatistics stats) => (stats.FrameReaderOpen, stats.StoryboardDecoderOpen) switch
    {
        (true, true) => "2 (frame reader + storyboard)",
        (true, false) => "1 (frame reader)",
        (false, true) => "1 (storyboard)",
        _ => "0",
    };
}
