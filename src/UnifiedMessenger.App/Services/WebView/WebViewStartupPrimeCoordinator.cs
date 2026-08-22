using System.Drawing;
using System.IO;
using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.WebView;

public sealed class WebViewStartupPrimeCoordinator(IWebViewSessionManager sessionManager)
    : IWebViewStartupPrimeCoordinator
{
    public async Task<StartupPrimeResult> PrimeAsync(
        IntPtr parentWindow,
        Rectangle bounds,
        IReadOnlyList<ServiceInstance> services,
        Guid? selectedServiceId,
        IProgress<StartupPrimeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        ServiceInstance[] ordered = OrderEnabledServices(services, selectedServiceId);
        if (ordered.Length == 0)
        {
            return StartupPrimeResult.Empty;
        }

        List<Guid> attempted = new(ordered.Length);
        List<Guid> initialized = new(ordered.Length);
        List<Guid> failed = [];

        for (int index = 0; index < ordered.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ServiceInstance service = ordered[index];
            attempted.Add(service.Id);
            progress?.Report(new StartupPrimeProgress(
                service.Id,
                service.DisplayName,
                index + 1,
                ordered.Length));

            bool succeeded;
            try
            {
                succeeded = await sessionManager.PrimeAsync(
                    parentWindow,
                    bounds,
                    service,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or ArgumentException
                    or IOException
                    or System.Runtime.InteropServices.COMException)
            {
                succeeded = false;
            }

            (succeeded ? initialized : failed).Add(service.Id);
        }

        return new StartupPrimeResult(attempted, initialized, failed);
    }

    internal static ServiceInstance[] OrderEnabledServices(
        IReadOnlyList<ServiceInstance> services,
        Guid? selectedServiceId)
    {
        ServiceInstance[] enabled = services
            .Where(service => service.IsEnabled)
            .DistinctBy(service => service.Id)
            .ToArray();
        ServiceInstance? selected = selectedServiceId is Guid id
            ? enabled.FirstOrDefault(service => service.Id == id)
            : null;

        return selected is null
            ? enabled.ToArray()
            : [selected, .. enabled.Where(service => service.Id != selected.Id)];
    }
}
