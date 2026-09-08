using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.IO;
using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Mail;

public interface IRemoteMailImageLoader
{
    Task<IReadOnlyDictionary<string, MailImageContent>> LoadAsync(
        IReadOnlyList<MailRemoteImageReference> images,
        CancellationToken cancellationToken = default);
}

public interface IRemoteMailImageHttpClient
{
    Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken);
}

public interface IRemoteMailImageUriValidator
{
    Task<bool> IsAllowedAsync(Uri uri, CancellationToken cancellationToken = default);
}

public sealed class RemoteMailImageHttpClient : IRemoteMailImageHttpClient, IDisposable
{
    public const bool CookiesEnabled = false;
    public const bool AutomaticRedirectsEnabled = false;
    public const bool DefaultCredentialsEnabled = false;

    private readonly HttpClient _client;

    public RemoteMailImageHttpClient()
    {
        HttpClientHandler handler = new()
        {
            AllowAutoRedirect = AutomaticRedirectsEnabled,
            UseCookies = CookiesEnabled,
            UseDefaultCredentials = DefaultCredentialsEnabled,
            Credentials = null,
            PreAuthenticate = false
        };
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    public void Dispose() => _client.Dispose();
}

public sealed class RemoteMailImageUriValidator : IRemoteMailImageUriValidator
{
    public async Task<bool> IsAllowedAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || !string.IsNullOrEmpty(uri.UserInfo)
            || string.IsNullOrWhiteSpace(uri.DnsSafeHost)
            || string.Equals(uri.DnsSafeHost, "localhost", StringComparison.OrdinalIgnoreCase)
            || uri.DnsSafeHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || uri.DnsSafeHost.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken);
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            return false;
        }

        return addresses.Length > 0 && addresses.All(IsPublicAddress);
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.None)
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.IPv6None)
            || address.IsIPv6LinkLocal
            || address.IsIPv6Multicast
            || address.IsIPv6SiteLocal)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily is AddressFamily.InterNetworkV6)
        {
            return bytes.Length == 16 && (bytes[0] & 0xFE) != 0xFC;
        }

        if (bytes.Length != 4)
        {
            return false;
        }

        return bytes[0] is not (0 or 10 or 127)
            && bytes[0] < 224
            && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
            && !(bytes[0] == 169 && bytes[1] == 254)
            && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            && !(bytes[0] == 192 && bytes[1] == 168)
            && !(bytes[0] == 192 && bytes[1] == 0)
            && !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2)
            && !(bytes[0] == 198 && bytes[1] is 18 or 19)
            && !(bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
            && !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
    }
}

public sealed class RemoteMailImageLoader(
    IRemoteMailImageHttpClient httpClient,
    IRemoteMailImageUriValidator uriValidator) : IRemoteMailImageLoader
{
    public const int MaximumImageBytes = 5 * 1024 * 1024;
    public const int MaximumTotalBytes = 20 * 1024 * 1024;
    public const int MaximumImageCount = 32;
    public const int MaximumRedirects = 3;
    public const int MaximumConcurrency = 4;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    public async Task<IReadOnlyDictionary<string, MailImageContent>> LoadAsync(
        IReadOnlyList<MailRemoteImageReference> images,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(images);
        MailRemoteImageReference[] selected = images
            .Where(image => !string.IsNullOrWhiteSpace(image.ImageId))
            .DistinctBy(image => image.ImageId, StringComparer.Ordinal)
            .Take(MaximumImageCount)
            .ToArray();
        using SemaphoreSlim concurrency = new(MaximumConcurrency, MaximumConcurrency);
        Task<LoadedImage?>[] tasks = selected
            .Select(image => LoadWithConcurrencyAsync(image, concurrency, cancellationToken))
            .ToArray();
        LoadedImage?[] loaded = await Task.WhenAll(tasks);

        Dictionary<string, MailImageContent> result = new(StringComparer.Ordinal);
        int totalBytes = 0;
        foreach (LoadedImage image in loaded.OfType<LoadedImage>())
        {
            if (totalBytes + image.Content.Bytes.Length > MaximumTotalBytes)
            {
                continue;
            }

            result.Add(image.ImageId, image.Content);
            totalBytes += image.Content.Bytes.Length;
        }

        return result;
    }

    private async Task<LoadedImage?> LoadWithConcurrencyAsync(
        MailRemoteImageReference image,
        SemaphoreSlim concurrency,
        CancellationToken cancellationToken)
    {
        await concurrency.WaitAsync(cancellationToken);
        try
        {
            return await LoadOneAsync(image, cancellationToken);
        }
        finally
        {
            concurrency.Release();
        }
    }

    private async Task<LoadedImage?> LoadOneAsync(
        MailRemoteImageReference image,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        Uri current = image.SourceUri;
        try
        {
            for (int redirectCount = 0; redirectCount <= MaximumRedirects; redirectCount++)
            {
                if (!await uriValidator.IsAllowedAsync(current, timeout.Token))
                {
                    return null;
                }

                using HttpRequestMessage request = new(HttpMethod.Get, current);
                using HttpResponseMessage response = await httpClient.SendAsync(request, timeout.Token);
                if (IsRedirect(response.StatusCode))
                {
                    if (redirectCount == MaximumRedirects
                        || response.Headers.Location is not Uri location)
                    {
                        return null;
                    }

                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                string contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? string.Empty;
                if (response.Content.Headers.ContentLength is long length && length > MaximumImageBytes)
                {
                    return null;
                }

                await using Stream source = await response.Content.ReadAsStreamAsync(timeout.Token);
                byte[] bytes = await ReadBoundedAsync(source, timeout.Token);
                if (!TryResolveTrustedContentType(contentType, bytes, out string trustedContentType))
                {
                    return null;
                }

                return new LoadedImage(image.ImageId, new MailImageContent(trustedContentType, bytes));
            }
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or IOException
                or OperationCanceledException
                or InvalidDataException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }

        return null;
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream source, CancellationToken cancellationToken)
    {
        using MemoryStream destination = new();
        byte[] buffer = new byte[81920];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return destination.ToArray();
            }

            if (destination.Length + read > MaximumImageBytes)
            {
                throw new InvalidDataException("Remote image exceeds the in-memory size limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.Moved
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static bool TryResolveTrustedContentType(
        string responseContentType,
        ReadOnlySpan<byte> bytes,
        out string trustedContentType)
    {
        if (MailHtmlSanitizer.IsSupportedImage(responseContentType, bytes))
        {
            trustedContentType = responseContentType;
            return true;
        }

        bool isGenericBinary = string.Equals(
                responseContentType,
                "binary/octet-stream",
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                responseContentType,
                "application/octet-stream",
                StringComparison.OrdinalIgnoreCase);
        if (isGenericBinary
            && MailHtmlSanitizer.TryDetectSupportedImageContentType(bytes, out trustedContentType))
        {
            return true;
        }

        trustedContentType = string.Empty;
        return false;
    }

    private sealed record LoadedImage(string ImageId, MailImageContent Content);
}
