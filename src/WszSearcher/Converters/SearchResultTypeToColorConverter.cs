using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using WszSearcher.Core.Models;

namespace WszSearcher.Converters;

/// <summary>SearchResultType → 背景色转换器</summary>
public class SearchResultTypeToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is SearchResultType type)
        {
            var color = type == SearchResultType.FileName
                ? System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD4)
                : System.Windows.Media.Color.FromRgb(0xFF, 0x98, 0x00);
            return new SolidColorBrush(color);
        }
        return new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD4));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
