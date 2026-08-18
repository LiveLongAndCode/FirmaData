using System.Net;
using FluentAssertions;

namespace FirmaData.Api.IntegrationTests;

public class HealthEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Live_ReturnsHealthyWithoutCallingDependencies()
    {
        factory.MockServer.ResetMappings();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("Healthy");
    }

    [Fact]
    public async Task Ready_WhenDependenciesAreReachable_ReturnsHealthy()
    {
        // Neither probe needs a specific stub -- the health checks only care that the WireMock
        // server answers at all (even a default 404), not that a business route matches.
        factory.MockServer.ResetMappings();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("Healthy");
    }
}
