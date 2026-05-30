using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace GalgameQuoteCollector.Converters;

/// <summary>
/// Converts a screenshot file path to a thumbnail BitmapImage.
/// Loads asynchronously and decodes at small size for performance.
/// </summary>
[ValueConversion(typeof(string), typeof(BitmapImage))]
public class ThumbnailConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var path = value as string;
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            return null!;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.DecodePixelWidth = 84;     // decode at small size (saves memory)
            bitmap.DecodePixelHeight = 64;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.None;
            bitmap.EndInit();
            bitmap.Freeze(); // make cross-thread usable
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
