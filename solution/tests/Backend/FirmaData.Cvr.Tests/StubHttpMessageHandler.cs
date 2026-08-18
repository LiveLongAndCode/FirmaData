using System.Net;

namespace FirmaData.Cvr.Tests;

// Hand-rolled fake per the test strategy (plan section 8: "mocked HttpMessageHandler") --
// HttpMessageHandler's SendAsync is protected, which makes it awkward to mock with a general
// mocking library, so a small subclass is the idiomatic approach for HttpClient-level tests.
internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }

    public static StubHttpMessageHandler Returning(HttpStatusCode statusCode, string? content = null) =>
        new(_ => new HttpResponseMessage(statusCode)
        {
            Content = content is null ? null : new StringContent(content, System.Text.Encoding.UTF8, "application/json")
        });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastRequest = request;
        return Task.FromResult(respond(request));
    }
}
