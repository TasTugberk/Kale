using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web;

namespace Auth.Desktop;

/// <summary>
/// Waits on 127.0.0.1, on a free port, for the browser to come back from Kale (RFC 8252: a loopback address on any
/// port; Kale ignores the port for desktop clients).
/// </summary>
/// <remarks>
/// A plain TCP listener rather than HttpListener: on Windows, HttpListener needs a URL reservation (admin rights)
/// for http://127.0.0.1:port/, and it can't pick a free port by itself. Here the operating system picks the port
/// (port 0), and only one request line is read: GET /path?query.
/// </remarks>
internal sealed class LoopbackListener : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public LoopbackListener()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    /// <summary>The address of <paramref name="path"/> (e.g. "/callback") on this listener.</summary>
    public Uri AddressOf(string path) => new($"http://127.0.0.1:{Port}{path}");

    /// <summary>The first request for <paramref name="path"/>. Anything else (e.g. /favicon.ico) gets a 404.</summary>
    public async Task<LoopbackRequest> WaitForAsync(string path, CancellationToken cancellationToken)
    {
        while (true)
        {
            var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            var request = await LoopbackRequest.ReadAsync(client, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                continue;
            }

            if (request.Path == path)
            {
                return request;
            }

            await request.RespondAsync(HttpStatusCode.NotFound, "", cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose() => _listener.Dispose();
}

/// <summary>One request to the <see cref="LoopbackListener"/>; answering it closes the connection.</summary>
internal sealed class LoopbackRequest : IDisposable
{
    // A request that doesn't arrive in this time isn't the browser; it mustn't hold up the real one.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpClient _client;

    private LoopbackRequest(TcpClient client, string path, NameValueCollection query)
    {
        _client = client;
        Path = path;
        Query = query;
    }

    public string Path { get; }

    public NameValueCollection Query { get; }

    /// <summary>Reads the request line and the headers; null (connection closed) when it isn't a readable request.</summary>
    public static async Task<LoopbackRequest?> ReadAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadTimeout);
        LoopbackRequest? request = null;
        try
        {
            // leaveOpen: the answer goes back on the same stream. The headers are read only to get past them.
            using var reader = new StreamReader(client.GetStream(), Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false)))
            {
            }

            // "GET /callback?code=...&state=... HTTP/1.1"
            if (requestLine?.Split(' ') is [_, var target, _] && target.StartsWith('/'))
            {
                var uri = new Uri(new Uri("http://127.0.0.1/"), target);
                request = new LoopbackRequest(client, uri.AbsolutePath, HttpUtility.ParseQueryString(uri.Query));
            }
        }
        catch (IOException)
        {
            // The connection closed: not the browser's answer.
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Only the read timeout ran out: a connection that stayed silent, not the browser's answer.
        }
        finally
        {
            // A connection that didn't become a request is closed here, also when the sign-in was cancelled (the
            // exception goes on to the caller). A request's connection closes with its answer.
            if (request is null)
            {
                client.Dispose();
            }
        }

        return request;
    }

    public async Task RespondAsync(HttpStatusCode status, string html, CancellationToken cancellationToken)
    {
        try
        {
            var body = Encoding.UTF8.GetBytes(html);
            var head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {(int)status} {status}\r\n" +
                "Content-Type: text/html; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Cache-Control: no-store\r\n" +
                "Connection: close\r\n\r\n");
            var stream = _client.GetStream();
            await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The browser tab closed before the page arrived: nothing left to tell it.
        }
        finally
        {
            _client.Dispose();
        }
    }

    public void Dispose() => _client.Dispose();
}
