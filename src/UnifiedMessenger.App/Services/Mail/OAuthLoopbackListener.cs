using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace UnifiedMessenger.App.Services.Mail;

public sealed class OAuthLoopbackListenerFactory : IOAuthLoopbackListenerFactory
{
    public IOAuthLoopbackListener Create() => new TcpOAuthLoopbackListener();
}

internal sealed class TcpOAuthLoopbackListener : IOAuthLoopbackListener
{
    private const int MaximumRequestBytes = 16 * 1024;
    private readonly TcpListener _listener;
    private bool _disposed;
    private bool _hasReceivedCallback;

    public TcpOAuthLoopbackListener()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(1);
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        RedirectUri = new Uri($"http://127.0.0.1:{port}{GmailOAuthConstants.CallbackPath}");
    }

    public Uri RedirectUri { get; }

    public async Task<OAuthLoopbackResponse> WaitForCallbackAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hasReceivedCallback)
        {
            throw new InvalidOperationException("This OAuth listener accepts only one callback.");
        }

        _hasReceivedCallback = true;
        using TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken);
        await using NetworkStream stream = client.GetStream();
        Uri requestUri = await ReadRequestUriAsync(stream, cancellationToken);
        if (!string.Equals(requestUri.AbsolutePath, GmailOAuthConstants.CallbackPath, StringComparison.Ordinal))
        {
            await WriteResponseAsync(stream, HttpStatusCode.NotFound, "Страница не найдена.");
            throw new InvalidDataException("Unexpected OAuth callback path.");
        }

        IReadOnlyDictionary<string, string> query = ParseQuery(requestUri.Query);
        await WriteResponseAsync(
            stream,
            HttpStatusCode.OK,
            "Авторизация завершена. Можно вернуться в Lantern.");
        return new OAuthLoopbackResponse(
            query.GetValueOrDefault("code"),
            query.GetValueOrDefault("state"),
            query.GetValueOrDefault("error"));
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _listener.Stop();
        }

        return ValueTask.CompletedTask;
    }

    private async Task<Uri> ReadRequestUriAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[MaximumRequestBytes];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (ContainsHeaderTerminator(buffer.AsSpan(0, total)))
            {
                break;
            }
        }

        if (total == 0 || !ContainsHeaderTerminator(buffer.AsSpan(0, total)))
        {
            throw new InvalidDataException("The OAuth callback request was incomplete.");
        }

        string request = Encoding.ASCII.GetString(buffer, 0, total);
        string firstLine = request.Split("\r\n", 2, StringSplitOptions.None)[0];
        string[] parts = firstLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !string.Equals(parts[0], "GET", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The OAuth callback request was invalid.");
        }

        string requestTarget = parts[1];
        if (!requestTarget.StartsWith("/", StringComparison.Ordinal)
            || requestTarget.StartsWith("//", StringComparison.Ordinal)
            || !Uri.TryCreate(RedirectUri, requestTarget, out Uri? requestUri)
            || requestUri.Scheme != Uri.UriSchemeHttp
            || requestUri.Host != IPAddress.Loopback.ToString()
            || requestUri.Port != RedirectUri.Port)
        {
            throw new InvalidDataException("The OAuth callback URI was invalid.");
        }

        return requestUri;
    }

    private static bool ContainsHeaderTerminator(ReadOnlySpan<byte> bytes) =>
        bytes.IndexOf("\r\n\r\n"u8) >= 0;

    private static IReadOnlyDictionary<string, string> ParseQuery(string query)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            string name = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            string value = parts.Length == 2
                ? Uri.UnescapeDataString(parts[1].Replace('+', ' '))
                : string.Empty;
            values[name] = value;
        }

        return values;
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        HttpStatusCode statusCode,
        string message)
    {
        string html = $"<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>Lantern</title></head><body><p>{WebUtility.HtmlEncode(message)}</p></body></html>";
        byte[] body = Encoding.UTF8.GetBytes(html);
        string headers =
            $"HTTP/1.1 {(int)statusCode} {statusCode}\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            "Cache-Control: no-store\r\n" +
            "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'\r\n" +
            "Connection: close\r\n" +
            $"Content-Length: {body.Length}\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(headerBytes);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }
}
