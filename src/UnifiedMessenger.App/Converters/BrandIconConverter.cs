using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Branding;

namespace UnifiedMessenger.App.Converters;

public sealed class BrandIconConverter : IValueConverter
{
    private static readonly IReadOnlyDictionary<BrandIconKind, ImageSource> Sources =
        Enum.GetValues<BrandIconKind>().ToDictionary(iconKind => iconKind, CreateImageSource);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        BrandIconKind iconKind = ResolveIconKind(value);
        return Sources[iconKind];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    internal static BrandIconKind ResolveIconKind(object? value) => value switch
    {
        NavigationAccountItem item => BrandIconCatalog.Resolve(item),
        ServiceType serviceType => BrandIconCatalog.Resolve(serviceType),
        MailProviderType provider => BrandIconCatalog.Resolve(provider),
        BrandIconKind kind => kind,
        _ => BrandIconKind.Lantern
    };

    private static ImageSource CreateImageSource(BrandIconKind iconKind)
    {
        BitmapImage image = new();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(BrandIconCatalog.GetPackUri(iconKind), UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }
}

public sealed class BrandIconSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BrandIconCatalog.GetSidebarPresentationSize(BrandIconConverter.ResolveIconKind(value));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
