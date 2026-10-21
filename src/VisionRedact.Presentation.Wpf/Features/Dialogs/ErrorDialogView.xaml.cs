using System.Windows.Controls;

namespace VisionRedact.Presentation.Wpf.Features.Dialogs;

// Prism's dialog service puts it in a window (DialogNames.Error)
public partial class ErrorDialogView : UserControl
{
    public ErrorDialogView()
    {
        InitializeComponent();
    }
}
