namespace VisionRedact.Presentation.Wpf;

// the two regions of the main window. one place for the names, so ShellWindow.xaml and the code that fills
// them (App.OnInitialized, ShellViewModel) can't drift apart
public static class RegionNames
{
    public const string Player = nameof(Player);
    public const string Inspector = nameof(Inspector);
}

// the Inspector tabs. App.RegisterTypes registers each name, ShellViewModel navigates to it
public static class ViewNames
{
    public const string Metadata = "MetadataView";
    public const string Detections = "DetectionsView";
    public const string Redaction = "RedactionView";
    public const string People = "PeopleView";
    public const string Diagnostics = "DiagnosticsView";
}

public static class DialogNames
{
    public const string Error = "ErrorDialog";
}
