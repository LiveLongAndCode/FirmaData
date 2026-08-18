using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using WireMock.Server;

namespace FirmaData.Api.IntegrationTests;

// Hermetic per the test strategy (plan section 8): both external dependencies are stubbed with
// WireMock.Net, so no test in this project touches the real apicvr.dk / api.statbank.dk. A
// single server hosts both, since their request paths never collide.
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    // Named MockServer, not Server, to avoid hiding WebApplicationFactory<Program>.Server (the
    // in-memory TestServer).
    public WireMockServer MockServer { get; } = WireMockServer.Start();

    public ApiFactory()
    {
        // WireMock.Net 2.x answers its first request in ~1.7s (vs ~0.3s on 1.x), which leaves
        // CvrApiHealthCheck's hardcoded 3s timeout with a thin margin on a cold run. One throwaway
        // request here pays that startup cost before any test's health-check assertion depends on it.
        using var warmupClient = new HttpClient();
        try
        {
            warmupClient.Send(new HttpRequestMessage(HttpMethod.Get, MockServer.Url));
        }
        catch (HttpRequestException)
        {
            // The warmup request itself isn't expected to succeed (no stub configured yet) --
            // only to force WireMock past its slow first-response path.
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cvr:BaseUrl"] = MockServer.Url,
                ["Statbank:BaseUrl"] = MockServer.Url,
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            MockServer.Dispose();
        }

        base.Dispose(disposing);
    }
}
