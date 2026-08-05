using System.Globalization;
using System.Windows.Data;
using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Converters;

public sealed class ServiceGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ServiceType serviceType
            ? serviceType switch
            {
                ServiceType.Telegram => "T",
                ServiceType.WhatsApp => "W",
                ServiceType.Max => "M",
                ServiceType.VkMessenger => "VK",
                ServiceType.Gmail => "G",
                _ => "?"
            }
            : "?";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
