using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace GalQuoteCollector.Converters;

/// <summary>
/// Converts (quote preview text, search keyword) into a TextBlock with highlighted matches.
/// Usage: MultiBinding with two bindings: text, keyword.
/// </summary>
public class SearchHighlightConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = values.Length > 0 ? values[0] as string ?? "" : "";
        var keyword = values.Length > 1 ? values[1] as string ?? "" : "";

        if (string.IsNullOrEmpty(keyword))
            return new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };

        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap };

        int start = 0;
        while (start < text.Length)
        {
            int idx = text.IndexOf(keyword, start, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                tb.Inlines.Add(new Run(text[start..]));
                break;
            }

            if (idx > start)
                tb.Inlines.Add(new Run(text[start..idx]));

            tb.Inlines.Add(new Run(text[idx..(idx + keyword.Length)])
            {
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x80)),
                FontWeight = FontWeights.Bold
            });

            start = idx + keyword.Length;
        }

        return tb;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
