using System.Windows;

// no per-OS-theme dictionaries, and Themes/Generic.xaml is in this assembly.
// without it WPF finds no template for VideoTimelineControl / VideoSurface and they draw nothing
[assembly: ThemeInfo(ResourceDictionaryLocation.None, ResourceDictionaryLocation.SourceAssembly)]
