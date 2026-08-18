using System.Net;
using System.Net.Http.Json;
using FirmaData.Contracts;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FirmaData.Api.IntegrationTests;

// Opt-in smoke test against the real apicvr.dk / api.statbank.dk (plan section 8) -- excluded
// from CI's default filter (Category!=Live) and run on demand via live-smoke.yml or
// `dotnet test --filter "Category=Live"`. A failure here means the upstream contract drifted,
// not that the code regressed, which is exactly why it must never gate a PR.
//
// Deliberately a plain WebApplicationFactory<Program>, not ApiFactory: no Cvr:BaseUrl /
// Statbank:BaseUrl override, so appsettings.json's real defaults are what the app under test
// actually calls -- the same way this is how the three live-API discrepancies documented in the
// README were originally found.
[Trait("Category", "Live")]
public class LiveSmokeTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task GetByCvr_ForLbForsikring_ReturnsRealEnrichedResponse()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/companies/16500836?year=2022");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<EnrichedCompanyResponse>();
        body!.Company.CvrNumber.Should().Be("16500836");
        body.Company.Name.Should().Be("LB FORSIKRING A/S");
        body.Company.IndustryCode.Should().Be("651200");
        body.StatisticsStatus.Should().Be("Ok");
        body.IndustryStatistics.Should().NotBeNull();
    }

    [Fact]
    public async Task SearchByName_ForLbForsikring_FindsTheRealCompany()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/companies?name=LB%20Forsikring&year=2022");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<List<EnrichedCompanyResponse>>();
        body!.Should().Contain(c => c.Company.CvrNumber == "16500836");
    }
}
