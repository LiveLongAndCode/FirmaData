using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
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

    // Overrides Program.cs's TimeProvider.System registration (plan fase 7, F9c) so tests can
    // assert a deterministic RetrievedAtUtc instead of a moving DateTimeOffset.UtcNow.
    public FakeTimeProvider TimeProvider { get; } = new(DateTimeOffset.Parse("2026-08-18T09:12:00Z"));

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

        // Keyed, not a plain AddSingleton<TimeProvider> override -- Microsoft.Extensions.Http.
        // Resilience resolves an unkeyed TimeProvider from this same container to drive Polly's
        // own retry/timeout delays, and overriding that with a frozen FakeTimeProvider stalls
        // every scheduled retry indefinitely (see AppTimeProvider and Program.cs). Only the app's
        // own keyed registration is replaced here.
        // <TimeProvider> is explicit: without it, TService infers as FakeTimeProvider (the
        // property's static type), registering a keyed service nobody asks for and leaving
        // Program.cs's original TimeProvider-keyed registration as the only match.
        builder.ConfigureServices(services => services.AddKeyedSingleton<TimeProvider>(AppTimeProvider.ServiceKey, TimeProvider));
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
