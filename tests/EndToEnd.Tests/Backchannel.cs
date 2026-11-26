using System.Collections.Concurrent;
using System.Net;

namespace EndToEnd.Tests;

/// <summary>
/// Sits between the client app and AuthService: counts the calls per path, remembers which host each path was
/// called on, and can make the first calls to a path fail as if AuthService were down.
/// </summary>
internal sealed class Backchannel(HttpMessageHandler inner, string? failPath = null, int failures = 0) : DelegatingHandler(inner)
{
    private readonly ConcurrentDictionary<string, int> _calls = new();
    private readonly ConcurrentDictionary<string, string> _hosts = new();
    private int _failuresLeft = failures;

    public int Calls(string path) => _calls.GetValueOrDefault(path);

    public string? Host(string path) => _hosts.GetValueOrDefault(path);

    /// <summary>While true, every call fails without an answer, as when AuthService can't be reached at all.</summary>
    public bool Unreachable { get; set; }

    /// <summary>While true, every call waits until it's cancelled, as when AuthService accepts but never answers.</summary>
    public bool Hangs { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        _calls.AddOrUpdate(path, 1, (_, count) => count + 1);
        _hosts[path] = request.RequestUri.Host;
        if (Unreachable)
        {
            throw new HttpRequestException("Connection refused (AuthService made unreachable by the test).");
        }

        if (Hangs)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (path == failPath && Interlocked.Decrement(ref _failuresLeft) >= 0)
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request };
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
