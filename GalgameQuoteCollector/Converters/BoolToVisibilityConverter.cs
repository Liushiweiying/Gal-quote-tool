using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GalgameQuoteCollector.Converters;

[ValueConversion(typeof(object), typeof(Visibility))]
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool boolValue = value switch
        {
            bool b => b,
            int i => i > 0,
            null => false,
            _ => true // non-null reference = visible
        };

        bool invert = parameter is string s && s == "invert";
        return (boolValue ^ invert) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is Visibility.Visible;
    }
}
