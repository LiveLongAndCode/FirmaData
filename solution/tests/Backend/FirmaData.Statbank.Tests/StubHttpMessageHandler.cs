using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace FirmaData.Statbank.Tests;

// Hand-rolled fake per the test strategy (plan section 8) -- HttpMessageHandler's SendAsync is
// protected, which makes it awkward to mock with a general mocking library, so a small subclass
// is the idiomatic approach for HttpClient-level tests. Mirrors FirmaData.Cvr.Tests's handler,
// duplicated rather than shared because the two test projects are separate assemblies.
internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }

    public int RequestCount { get; private set; }

    public static StubHttpMessageHandler Returning(HttpStatusCode statusCode, string? content = null) =>
        new(_ => new HttpResponseMessage(statusCode)
        {
            Content = content is null ? null : new StringContent(content, Encoding.UTF8, "application/json")
        });

    // Returns raw bytes verbatim (no re-encoding), so a BOM captured in a fixture file survives
    // into the response -- StringContent would not preserve it.
    public static StubHttpMessageHandler ReturningBytes(HttpStatusCode statusCode, byte[] content, string mediaType = "text/csv") =>
        new(_ => new HttpResponseMessage(statusCode)
        {
            Content = new ByteArrayContent(content) { Headers = { ContentType = new MediaTypeHeaderValue(mediaType) } }
        });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastRequest = request;
        RequestCount++;
        return Task.FromResult(respond(request));
    }
}
