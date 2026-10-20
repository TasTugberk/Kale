using System.Collections.Concurrent;
using System.Net;

namespace EndToEnd.Tests;

/// <summary>
/// Sits between the client app and AuthService: counts the calls per path, and can make the first calls to a
/// path fail as if AuthService were down.
/// </summary>
internal sealed class Backchannel(HttpMessageHandler inner, string? failPath = null, int failures = 0) : DelegatingHandler(inner)
{
    private readonly ConcurrentDictionary<string, int> _calls = new();
    private int _failuresLeft = failures;

    public int Calls(string path) => _calls.GetValueOrDefault(path);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        _calls.AddOrUpdate(path, 1, (_, count) => count + 1);
        if (path == failPath && Interlocked.Decrement(ref _failuresLeft) >= 0)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request });
        }

        return base.SendAsync(request, cancellationToken);
    }
}
