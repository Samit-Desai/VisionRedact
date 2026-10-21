using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace VisionRedact.Presentation.Wpf.Behaviors;

// makes a TextBox an inline editor (the People tab's names). bind IsEditing two-way: when it turns true the box
// takes focus and selects all. Enter or leaving commits, Esc cancels.
// use w/ UpdateSourceTrigger=Explicit: nothing reaches the view model before a commit, so Esc just re-reads the old
// value (UpdateTarget)
public static class InlineEdit
{
    public static readonly DependencyProperty IsEditingProperty = DependencyProperty.RegisterAttached(
        "IsEditing", typeof(bool), typeof(InlineEdit),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsEditingChanged));

    public static bool GetIsEditing(DependencyObject element) => (bool)element.GetValue(IsEditingProperty);

    public static void SetIsEditing(DependencyObject element, bool value) => element.SetValue(IsEditingProperty, value);

    private static void OnIsEditingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            box.KeyDown += OnKeyDown;
            box.LostKeyboardFocus += OnLostFocus;
            // later, once the box is visible (the same IsEditing change usually shows it)
            box.Dispatcher.BeginInvoke(() =>
            {
                box.Focus();
                box.SelectAll();
            });
        }
        else
        {
            box.KeyDown -= OnKeyDown;
            box.LostKeyboardFocus -= OnLostFocus;
        }
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        if (e.Key == Key.Enter)
        {
            Finish(box, commit: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Finish(box, commit: false);
            e.Handled = true;
        }
    }

    private static void OnLostFocus(object sender, KeyboardFocusChangedEventArgs e) => Finish((TextBox)sender, commit: true);

    private static void Finish(TextBox box, bool commit)
    {
        if (!GetIsEditing(box))
        {
            return; // Enter already finished, the focus loss right after must not commit again
        }

        var binding = box.GetBindingExpression(TextBox.TextProperty);
        if (commit)
        {
            binding?.UpdateSource();
        }
        else
        {
            binding?.UpdateTarget();
        }

        SetIsEditing(box, false);
    }
}
