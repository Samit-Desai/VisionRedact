using System.Windows.Controls;
using System.Windows.Input;

namespace VisionRedact.Presentation.Wpf.Features.Player;

// only focus handling here: keyboard focus stays in the player, so Space / arrows / D work right away
public partial class PlayerView : UserControl
{
    public PlayerView()
    {
        InitializeComponent();
        Loaded += (_, _) => Keyboard.Focus(this);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        Keyboard.Focus(this); // and again after a click anywhere in the player
    }
}
