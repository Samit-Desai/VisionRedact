using Microsoft.Win32;
using VisionRedact.Application.Export;

namespace VisionRedact.Presentation.Wpf.Infrastructure;

// view models ask "which file?" here instead of opening a modal dialog themselves. UI thread only.
// every Pick returns null when the user cancels
public interface IFileDialogService
{
    string? PickVideoFile(string title = "Open video");

    string? PickProjectToOpen();

    string? PickProjectSavePath(string suggestedName);

    // + the format picked in the "Save as type" list
    (string Path, ExportFormat Format)? PickExportPath(string suggestedName);
}

// the standard Windows Open / Save dialogs
public sealed class Win32FileDialogService : IFileDialogService
{
    // "Label|pattern;pattern|Label|pattern", the user sees the labels
    private const string VideoFilter =
        "Video files|*.mp4;*.mov;*.m4v;*.avi;*.mkv;*.wmv;*.webm;*.mpg;*.mpeg;*.ts;*.mts;*.m2ts|All files|*.*";

    private const string ProjectFilter = "VisionRedact project (*.vrproject)|*.vrproject";

    // modal to the main window, centered on it
    private static System.Windows.Window? Owner => System.Windows.Application.Current.MainWindow;

    public string? PickVideoFile(string title = "Open video")
    {
        var dialog = new OpenFileDialog { Title = title, Filter = VideoFilter, CheckFileExists = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? PickProjectToOpen()
    {
        var dialog = new OpenFileDialog { Title = "Open project", Filter = ProjectFilter, CheckFileExists = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? PickProjectSavePath(string suggestedName)
    {
        var dialog = new SaveFileDialog { Title = "Save project", Filter = ProjectFilter, FileName = suggestedName, AddExtension = true, DefaultExt = ".vrproject" };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public (string Path, ExportFormat Format)? PickExportPath(string suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export redacted video",
            Filter = "MP4 video (*.mp4)|*.mp4|AVI, Motion-JPEG (*.avi)|*.avi",
            FileName = suggestedName,
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(Owner) != true)
        {
            return null;
        }

        // FilterIndex is 1-based: 1 = MP4, 2 = AVI. the chosen filter decides the format
        return (dialog.FileName, dialog.FilterIndex == 2 ? ExportFormat.AviMjpeg : ExportFormat.Mp4);
    }
}
