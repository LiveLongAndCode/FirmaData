using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FirmaData.Api.IntegrationTests;

// Structural guard for plan section 2.3: FirmaData.Api uses [ApiController] controllers, never
// minimal-API MapGet/MapPost delegates. There's no analyzer rule that rejects a stray MapGet, so
// this walks every mapped endpoint and asserts it's backed by a controller action instead.
public class ControllerEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public void All_endpoints_are_controller_actions()
    {
        using var scope = factory.Services.CreateScope();
        var dataSources = scope.ServiceProvider.GetRequiredService<IEnumerable<EndpointDataSource>>();

        // Health checks (MapHealthChecks, section 7.4) and the Prometheus scrape endpoint
        // (MapPrometheusScrapingEndpoint, section 7.2) are deliberately mapped as
        // framework-provided endpoints, not business routes -- the guard here is about the
        // API's own surface, not about infrastructure endpoints having a ControllerActionDescriptor too.
        string[] infrastructurePrefixes = ["/health", "/metrics", "/openapi"];
        var endpoints = dataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => !infrastructurePrefixes.Any(prefix =>
                endpoint.RoutePattern.RawText?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true))
            .ToList();

        endpoints.Should().NotBeEmpty();
        endpoints.Should().OnlyContain(endpoint => endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() != null);
    }
}
