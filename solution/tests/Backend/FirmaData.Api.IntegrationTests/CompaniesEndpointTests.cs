using System.Net;
using System.Net.Http.Json;
using FirmaData.Contracts;
using FluentAssertions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace FirmaData.Api.IntegrationTests;

public class CompaniesEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    // Real values confirmed live against apicvr.dk / api.statbank.dk during planning.
    private const string LbForsikringCvrJson = """
        {
          "vat": 16500836,
          "name": "LB FORSIKRING A/S",
          "address": "Amerika Plads 15",
          "zipcode": 2100,
          "city": "København Ø",
          "employees": 1010,
          "industrycode": "651200",
          "industrydesc": "Anden forsikring",
          "bankrupt": false,
          "status": "NORMAL"
        }
        """;

    private const string Erhv651200Csv =
        "BRANCHE07;TAL;TID;INDHOLD\n" +
        "651200;ARBSTED;2022;166\n" +
        "651200;ANSATTE;2022;15206\n" +
        "651200;FULDBESK;2022;13458\n" +
        "651200;LØNSUM;2022;10380\n";

    private void GivenCvrLookupSucceeds() =>
        factory.MockServer
            .Given(Request.Create().WithPath("/api/v1/16500836").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(LbForsikringCvrJson).WithHeader("Content-Type", "application/json"));

    private void GivenStatbankLookupSucceeds() =>
        factory.MockServer
            .Given(Request.Create().WithPath("/v1/data").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Erhv651200Csv).WithHeader("Content-Type", "text/csv"));

    private void GivenStatbankLookupIsDown() =>
        factory.MockServer
            .Given(Request.Create().WithPath("/v1/data").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500));

    [Fact]
    public async Task GetByCvr_WithKnownCvrAndExplicitYear_ReturnsFullEnrichedResponse()
    {
        factory.MockServer.ResetMappings();
        GivenCvrLookupSucceeds();
        GivenStatbankLookupSucceeds();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/companies/16500836?year=2022");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<EnrichedCompanyResponse>();
        body!.Company.Name.Should().Be("LB FORSIKRING A/S");
        body.IndustryStatistics!.WorkplacesEndNovember.Should().Be(166);
        body.StatisticsStatus.Should().Be("Ok");
        // Deterministic via ApiFactory's FakeTimeProvider (plan fase 7, F9c), not a moving
        // DateTimeOffset.UtcNow.
        body.RetrievedAtUtc.Should().Be(factory.TimeProvider.GetUtcNow());
    }

    [Fact]
    public async Task GetByCvr_WithMalformedCvr_ReturnsProblemDetails400()
    {
        factory.MockServer.ResetMappings();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/companies/123");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Validation");
    }

    [Fact]
    public async Task GetByCvr_WithInvalidYear_ReturnsProblemDetails400()
    {
        factory.MockServer.ResetMappings();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/companies/16500836?year=2007");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Validation");
    }

    [Fact]
    public async Task GetByCvr_WithInvalidIndustryCodeFromCvrApi_Returns502()
    {
        // The CVR API's own response fails the anti-corruption mapping (industrycode isn't a
        // valid 6-digit DB07 code) -- a broken upstream contract, not a client input problem, so
        // it must surface as 502 rather than 500.
        //
        // A different CVR number than the "known CVR" tests (30000005 -- checksum-valid, not a
        // real registered company): CachingCompanyDirectory (plan fase 7, F9b) caches a
        // successful GetByCvrAsync result for 10 minutes, shared across the whole class fixture.
        // Reusing 16500836 here risks this test silently reading another test's already-cached
        // good company data instead of ever calling this test's own malformed-response mock.
        factory.MockServer.ResetMappings();
        factory.MockServer
            .Given(Request.Create().WithPath("/api/v1/30000005").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""
                {
                  "vat": 30000005,
                  "name": "LB FORSIKRING A/S",
                  "industrycode": "NOT-A-CODE"
                }
                """).WithHeader("Content-Type", "application/json"));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/companies/30000005");

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Unexpected");
    }

    [Fact]
    public async Task GetByCvr_WithUnknownCvr_Returns404()
    {
        // A different CVR number than the other tests in this class -- CachingCompanyDirectory
        // (plan fase 7, F9b) now caches a NotFound result for 10 minutes, shared across the whole
        // class fixture, so reusing 16500836 here would poison the "known CVR" tests with a
        // stale 404 depending on execution order.
        factory.MockServer.ResetMappings();
        factory.MockServer
            .Given(Request.Create().WithPath("/api/v1/25313763").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"error":"NOT_FOUND"}""").WithHeader("Content-Type", "application/json"));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/companies/25313763");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetByCvr_WhenStatbankIsDown_ReturnsMasterDataDegradedWithDegradedSourceHeader()
    {
        factory.MockServer.ResetMappings();
        GivenCvrLookupSucceeds();
        GivenStatbankLookupIsDown();
        using var client = factory.CreateClient();

        // A different year than the other tests in this class (2022) -- the caching decorator
        // stores positive results keyed by industry code + year for 24h in an IMemoryCache shared
        // across the whole class fixture, so reusing 2022 here would silently hit the cache
        // entry another test populated instead of ever calling this test's "down" mock.
        using var response = await client.GetAsync("/api/v1/companies/16500836?year=2021");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("FirmaData-Degraded-Source").Should().ContainSingle("statbank");
        var body = await response.Content.ReadFromJsonAsync<EnrichedCompanyResponse>();
        body!.StatisticsStatus.Should().Be("SourceUnavailable");
        body.IndustryStatistics.Should().BeNull();
    }

    [Fact]
    public async Task SearchByName_WithNoUpstreamMatches_Returns200WithEmptyArray()
    {
        // apicvr.dk reports "no matches" as an upstream 404 -- before this fase's fix, that was
        // indistinguishable from a real outage and came back as 503.
        factory.MockServer.ResetMappings();
        factory.MockServer
            .Given(Request.Create().WithPath("/api/v1/search/company/no such company").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/companies?name=no%20such%20company");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<List<EnrichedCompanyResponse>>();
        body.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchByName_WithNameShorterThanMinLength_Returns400()
    {
        factory.MockServer.ResetMappings();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/companies?name=a");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(26)]
    public async Task SearchByName_WithLimitOutOfRange_Returns400(int limit)
    {
        factory.MockServer.ResetMappings();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/v1/companies?name=lb%20forsikring&limit={limit}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SearchByName_WithLimit_ReturnsAtMostThatManyResults()
    {
        // Three matches, all sharing the same industry code, so the existing Statbank stub
        // covers all of them regardless of how many the `limit` cap lets through.
        factory.MockServer.ResetMappings();
        factory.MockServer
            .Given(Request.Create().WithPath("/api/v1/search/company/lb forsikring").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody($"[{LbForsikringCvrJson}, {LbForsikringCvrJson}, {LbForsikringCvrJson}]")
                .WithHeader("Content-Type", "application/json"));
        GivenStatbankLookupSucceeds();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/companies?name=lb%20forsikring&limit=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<List<EnrichedCompanyResponse>>();
        body.Should().HaveCount(2);
    }

    [Fact]
    public async Task SearchByName_WithoutNameQuery_Returns400()
    {
        factory.MockServer.ResetMappings();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/companies");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetAvailableYears_ReturnsSortedYearsWithDefault()
    {
        factory.MockServer.ResetMappings();
        factory.MockServer
            .Given(Request.Create().WithPath("/v1/tableinfo").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                variables = new[] { new { id = "TID", values = new[] { new { id = "2022" }, new { id = "2008" }, new { id = "2024" } } } },
            }));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/metadata/years");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AvailableYearsResponse>();
        body!.Years.Should().Equal(2008, 2022, 2024);
        body.DefaultYear.Should().Be(2024);
    }
}
