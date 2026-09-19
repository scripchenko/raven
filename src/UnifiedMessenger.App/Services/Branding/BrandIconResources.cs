using System.Drawing;
using System.IO;

namespace UnifiedMessenger.App.Services.Branding;

public static class BrandIconResources
{
    public static Icon LoadSystemIcon()
    {
        Uri resourceUri = new(BrandIdentity.SystemIconPackUri, UriKind.Absolute);
        System.Windows.Resources.StreamResourceInfo resource = System.Windows.Application.GetResourceStream(resourceUri)
            ?? throw new InvalidOperationException("raven system icon resource is unavailable.");

        using Stream stream = resource.Stream;
        using Icon source = new(stream);
        return (Icon)source.Clone();
    }
}
