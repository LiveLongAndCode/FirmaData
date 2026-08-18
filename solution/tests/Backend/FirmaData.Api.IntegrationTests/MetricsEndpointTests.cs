using System.Net;
using FluentAssertions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace FirmaData.Api.IntegrationTests;

// Plan section 7's exit criterion: "curl -s http://localhost:8080/metrics | grep
// firmadata_dependency" shows the dependency histograms with dependency="cvr" and
// dependency="statbank" labels.
public class MetricsEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Metrics_AfterCallingBothDependencies_ExposesDependencyInstrumentsForBoth()
    {
        factory.MockServer.ResetMappings();
        factory.MockServer
            .Given(Request.Create().WithPath("/api/v1/16500836").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """{"vat":16500836,"name":"LB FORSIKRING A/S","address":"Amerika Plads 15","zipcode":2100,"city":"København Ø","employees":1010,"industrycode":"651200","industrydesc":"Anden forsikring","bankrupt":false,"status":"NORMAL"}""")
                .WithHeader("Content-Type", "application/json"));
        factory.MockServer
            .Given(Request.Create().WithPath("/v1/data").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody("BRANCHE07;TAL;TID;INDHOLD\n651200;ARBSTED;2022;166\n651200;ANSATTE;2022;15206\n651200;FULDBESK;2022;13458\n651200;LØNSUM;2022;10380\n")
                .WithHeader("Content-Type", "text/csv"));
        using var client = factory.CreateClient();

        using var enrichResponse = await client.GetAsync("/api/v1/companies/16500836?year=2022");
        enrichResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var metricsResponse = await client.GetAsync("/metrics");
        var body = await metricsResponse.Content.ReadAsStringAsync();

        metricsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("firmadata_dependency_duration_seconds");
        body.Should().Contain("firmadata_dependency_requests_total");
        body.Should().Contain("dependency=\"cvr\"");
        body.Should().Contain("dependency=\"statbank\"");
        body.Should().Contain("firmadata_circuit_state");
        body.Should().Contain("firmadata_enrichment_duration_seconds");
    }
}
