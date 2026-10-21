using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VisionRedact.Presentation.Wpf.Infrastructure;

// one-way converters for the XAML, both declared in Themes/Theme.xaml

// true when the value equals ConverterParameter (as strings), e.g. a radio button's IsChecked for one mode.
// TODO: declared as "Equals" but no view uses it. use it or drop it, ok for now
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    // never writes back
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

// true -> Collapsed, anything else -> Visible. the opposite of BooleanToVisibilityConverter
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
