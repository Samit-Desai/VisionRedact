using System.Windows;

namespace VisionRedact.Presentation.Wpf.Infrastructure;

// a yes / no question, so view models don't call MessageBox themselves
public interface IUserPrompt
{
    bool Confirm(string title, string message);
}

// owned by the main window. No is the default button (the safe answer for the export warning)
public sealed class MessageBoxUserPrompt : IUserPrompt
{
    public bool Confirm(string title, string message) =>
        MessageBox.Show(System.Windows.Application.Current?.MainWindow!, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
}
