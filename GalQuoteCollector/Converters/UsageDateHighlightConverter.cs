using System.Globalization;
using System.Windows.Data;

namespace GalQuoteCollector.Converters;

/// <summary>
/// Marks calendar days that have usage records. The date set is supplied by the
/// stats window before the calendar is shown (static because a CalendarDayButton's
/// DataContext is just the DateTime it renders).
/// </summary>
public class UsageDateHighlightConverter : IValueConverter
{
    private static readonly HashSet<string> Dates = new();

    public static void SetDates(IEnumerable<string> dates)
    {
        Dates.Clear();
        foreach (var d in dates) Dates.Add(d);
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DateTime dt && Dates.Contains(dt.ToString("yyyy-MM-dd"));

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
