using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Grpc.Core;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Time.Testing;

namespace Auth.Client.Tests;

/// <summary>
/// Stands in for AuthService at the HTTP level, under a real <see cref="AuthServiceConnection"/> (so a real gRPC
/// client): answers the token request with a token, and gRPC calls with whatever the test sets up.
/// </summary>
internal sealed class FakeBackchannel : HttpMessageHandler
{
    public Func<CancellationToken, Task<HttpResponseMessage>> TokenAnswer { get; set; } = _ => Task.FromResult(Token());

    public Func<CancellationToken, Task<HttpResponseMessage>> GrpcAnswer { get; set; } =
        _ => Task.FromResult(GrpcStatus(StatusCode.Unimplemented));

    public AuthServiceConnection Connect(TimeProvider? clock = null) => new(
        new KaleAuthOptions
        {
            Authority = new Uri("https://auth.example/"),
            ApplicationKey = "billing",
            ClientSecret = "secret",
            BackchannelHandler = this,
        },
        clock ?? new FakeTimeProvider(),
        new HostingEnvironment { EnvironmentName = "Production" });

    /// <summary>A successful client-credentials answer.</summary>
    public static HttpResponseMessage Token() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"access_token":"app-token","expires_in":600}""", Encoding.UTF8, "application/json"),
    };

    /// <summary>A call that ends at once with this status ("trailers-only", the way a refusal is sent).</summary>
    public static HttpResponseMessage GrpcStatus(StatusCode status)
    {
        var response = GrpcResponse(new ByteArrayContent([]));
        response.Headers.Add("grpc-status", ((int)status).ToString(CultureInfo.InvariantCulture));
        return response;
    }

    /// <summary>
    /// A stream that sends <paramref name="headers"/>, then nothing until <paramref name="end"/> completes, then
    /// ends with UNAVAILABLE.
    /// </summary>
    public static HttpResponseMessage OpenStream(Metadata headers, Task end)
    {
        var response = GrpcResponse(new HeldContent(end));
        foreach (var header in headers)
        {
            response.Headers.Add(header.Key, header.Value);
        }

        // Read only after the body has ended, as HTTP/2 trailers are.
        response.TrailingHeaders.Add("grpc-status", ((int)StatusCode.Unavailable).ToString(CultureInfo.InvariantCulture));
        return response;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        request.RequestUri!.AbsolutePath == "/connect/token" ? TokenAnswer(cancellationToken) : GrpcAnswer(cancellationToken);

    private static HttpResponseMessage GrpcResponse(HttpContent content)
    {
        content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        return new HttpResponseMessage(HttpStatusCode.OK) { Version = HttpVersion.Version20, Content = content };
    }

    // A body that arrives (empty) only when the test lets the stream end.
    private sealed class HeldContent(Task end) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => end;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            end.WaitAsync(cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
