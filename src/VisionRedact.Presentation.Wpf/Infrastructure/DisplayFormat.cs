using System.Globalization;

namespace VisionRedact.Presentation.Wpf.Infrastructure;

// shared text formats (times, file sizes), so every screen shows the same
public static class DisplayFormat
{
    // mm:ss.fff, h:mm:ss.fff from an hour on. invariant culture keeps ':' and '.' fixed on every PC, like a real timecode
    public static string Timecode(TimeSpan time) =>
        time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss\.fff" : @"mm\:ss\.fff", CultureInfo.InvariantCulture);

    // e.g. 1536 -> "1.5 KB". the user's culture here: it's prose, not a machine format
    public static string FileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.CurrentCulture, $"{value:0.##} {units[unit]}");
    }
}
