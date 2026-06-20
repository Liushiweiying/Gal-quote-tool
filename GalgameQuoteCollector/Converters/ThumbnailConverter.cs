using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace GalgameQuoteCollector.Converters;

[ValueConversion(typeof(string), typeof(BitmapImage))]
public class ThumbnailConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var path = value as string;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null!;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            // Use file:/// URI to handle Unicode paths correctly
            bitmap.UriSource = new Uri(new Uri("file:///"), path);
            bitmap.DecodePixelWidth = 84;
            bitmap.DecodePixelHeight = 64;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null!;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
