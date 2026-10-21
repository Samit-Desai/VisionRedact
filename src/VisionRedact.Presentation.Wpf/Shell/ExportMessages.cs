using System.Globalization;
using System.Text;
using VisionRedact.Application.Detections;
using VisionRedact.Application.Export;

namespace VisionRedact.Presentation.Wpf.Shell;

// what the user is told an export does and doesn't cover: scan coverage before it starts, coverage + whether the
// source was read to the end when it's done. pure functions, so the wording lives in one place
internal static class ExportMessages
{
    public const string CoverageWarningTitle = "Not every frame is covered by the face scan";

    private const int ListedGaps = 3;

    // the question before exporting a video the face scan doesn't fully cover
    public static string CoverageWarning(RedactionCoverage coverage, double fps)
    {
        var text = new StringBuilder();
        if (coverage.IsNone)
        {
            text.Append("This video has not been scanned for faces yet.\n\n");
            text.Append("Without a scan there are no face boxes, so no face will be blurred in the export. Only manual regions apply.");
        }
        else
        {
            text.Append(CultureInfo.CurrentCulture, $"Only {Percent(coverage.Fraction)} of this video is covered by the face scan.\n\n");
            text.Append("Frames the scan doesn't cover have no face boxes, so faces on them will NOT be blurred. Manual regions still apply.\n\n");
            text.Append("Not covered: ").Append(Ranges(coverage, fps)).Append('.');
        }

        text.Append("\n\nTo cover the whole video, run \"Analyze video\" first.\n\nExport anyway?");
        return text.ToString();
    }

    // true when the "export complete" dialog has something the user must read
    public static bool HasWarnings(ExportResult result, RedactionCoverage coverage) => result.SourceEndedEarly || !coverage.IsComplete;

    public static string Completed(ExportResult result, RedactionCoverage coverage)
    {
        var culture = CultureInfo.CurrentCulture;
        var text = new StringBuilder();
        text.Append(culture, $"{result.FramesWritten:N0} frames written ({result.FramesRedacted:N0} with redactions) in {result.Elapsed:mm\\:ss}.\n");
        text.Append(culture, $"Codec: {result.Codec}\n{result.OutputPath}\n\n");

        if (result.SourceEndedEarly)
        {
            text.Append(culture, $"Warning: the source stopped after {result.FramesWritten:N0} of about {result.FramesExpected:N0} frames. ");
            text.Append("The end of the file may be damaged or missing; the export contains only the frames that could be read.\n\n");
        }

        // "scanned" only when the AI looked at every frame itself. with sampling the frames in between use a nearby
        // scanned frame's boxes, and the dialog says so
        text.Append(!coverage.IsComplete
            ? $"Warning: only {Percent(coverage.Fraction)} of the video is covered by the face scan; faces on the other frames were not blurred automatically.\n\n"
            : coverage.IsEveryFrameScanned
                ? "Every frame was scanned for faces (a model can still miss a face).\n\n"
                : string.Create(culture, $"Every frame is covered by the face scan: {coverage.ScannedFrames:N0} of {coverage.TotalFrames:N0} frames were scanned, and the rest use boxes from a nearby scanned frame. A model can still miss a face.\n\n"));

        text.Append(result.Audio switch
        {
            ExportAudio.Included => "The source audio is included.",
            ExportAudio.SourceHasNone => "The source has no audio track, so the export has none either.",
            ExportAudio.Unreadable => "The source's audio could not be read, so the export has no audio.",
            _ => "Audio is not included: FFmpeg is not installed (scripts/download-ffmpeg.ps1 adds it).",
        });
        text.Append(" The source file was not modified.");
        return text.ToString();
    }

    // rounded down, so 99.96 % never reads as a reassuring "100 %"
    private static string Percent(double fraction) =>
        (Math.Floor(fraction * 1000) / 10).ToString("0.#", CultureInfo.CurrentCulture) + " %";

    private static string Ranges(RedactionCoverage coverage, double fps)
    {
        var listed = coverage.Gaps.Take(ListedGaps).Select(g => $"{Time(g.First, fps)}–{Time(g.Last + 1, fps)}");
        var text = string.Join(", ", listed);
        var more = coverage.GapCount - Math.Min(ListedGaps, coverage.Gaps.Count);
        return more > 0 ? $"{text} and {more} more" : text;
    }

    // start of the frame, m:ss or h:mm:ss
    private static string Time(long frame, double fps)
    {
        var time = TimeSpan.FromSeconds(fps > 0 ? frame / fps : 0);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}
