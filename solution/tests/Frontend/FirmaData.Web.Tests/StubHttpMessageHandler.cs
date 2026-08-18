using System.Net;
using System.Text;

namespace FirmaData.Web.Tests;

// Hand-rolled fake per the test strategy (plan section 8) -- HttpMessageHandler's SendAsync is
// protected, which makes it awkward to mock with a general mocking library. Mirrors the backend
// adapters' own StubHttpMessageHandler (FirmaData.Cvr.Tests/FirmaData.Statbank.Tests), duplicated
// rather than shared because each test project is a separate assembly.
internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }

    public static StubHttpMessageHandler Returning(HttpStatusCode statusCode, string? content = null) =>
        new(_ => new HttpResponseMessage(statusCode)
        {
            Content = content is null ? null : new StringContent(content, Encoding.UTF8, "application/json"),
        });

    public static StubHttpMessageHandler Throwing(Exception exception) => new(_ => throw exception);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastRequest = request;
        return Task.FromResult(respond(request));
    }
}
