using System.Globalization;
using System.Windows.Data;

namespace GalQuoteCollector.Converters;

[ValueConversion(typeof(bool), typeof(string))]
public class ViewToggleTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isGrid)
            return isGrid ? "列表视图" : "网格视图";
        return "网格视图";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
