using System.Net;
using FirmaData.Web.Services;
using FluentAssertions;

namespace FirmaData.Web.Tests;

public class FirmaDataApiClientTests
{
    private static readonly Uri BaseAddress = new("http://localhost:8080/");

    // Mirrors the real EnrichedCompanyResponse shape from plan section 5.3.
    private const string LbForsikringJson = """
        {
          "company": {
            "cvrNumber": "16500836",
            "name": "LB FORSIKRING A/S",
            "address": { "street": "Amerika Plads 15", "postalCode": "2100", "city": "København Ø" },
            "industryCode": "651200",
            "industryDescription": "Anden forsikring",
            "employeeCount": 1010
          },
          "industryStatistics": {
            "industryCode": "651200",
            "year": 2022,
            "workplaces": 166,
            "employees": 15206,
            "fullTimeEquivalents": 13458,
            "wageSumMillionDkk": 10380
          },
          "statisticsStatus": "Ok",
          "retrievedAt": "2026-08-13T10:00:00Z",
          "sources": { "company": "apicvr.dk", "statistics": "api.statbank.dk/ERHV1" }
        }
        """;

    private static FirmaDataApiClient CreateClient(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = BaseAddress });

    [Fact]
    public async Task GetByCvrAsync_WhenFound_ReturnsFoundWithTheMappedResponse()
    {
        var sut = CreateClient(StubHttpMessageHandler.Returning(HttpStatusCode.OK, LbForsikringJson));

        var result = await sut.GetByCvrAsync("16500836", 2022, CancellationToken.None);

        result.Outcome.Should().Be(CompanyLookupOutcome.Found);
        result.Company!.Company.Name.Should().Be("LB FORSIKRING A/S");
    }

    [Fact]
    public async Task GetByCvrAsync_RequestsTheCvrNumberAndYear()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, LbForsikringJson);
        var sut = CreateClient(handler);

        await sut.GetByCvrAsync("16500836", 2022, CancellationToken.None);

        handler.LastRequest!.RequestUri!.PathAndQuery.Should().Be("/api/v1/companies/16500836?year=2022");
    }

    [Fact]
    public async Task GetByCvrAsync_When404_ReturnsNotFound()
    {
        var sut = CreateClient(StubHttpMessageHandler.Returning(HttpStatusCode.NotFound, """{"title":"NotFound"}"""));

        var result = await sut.GetByCvrAsync("99999999", 2022, CancellationToken.None);

        result.Outcome.Should().Be(CompanyLookupOutcome.NotFound);
    }

    [Fact]
    public async Task GetByCvrAsync_When400_ReturnsInvalid()
    {
        var sut = CreateClient(StubHttpMessageHandler.Returning(HttpStatusCode.BadRequest, """{"title":"Validation"}"""));

        var result = await sut.GetByCvrAsync("16500837", 2022, CancellationToken.None);

        result.Outcome.Should().Be(CompanyLookupOutcome.Invalid);
    }

    [Fact]
    public async Task GetByCvrAsync_When503_ThrowsApiUnavailable()
    {
        var sut = CreateClient(StubHttpMessageHandler.Returning(HttpStatusCode.ServiceUnavailable));

        var act = () => sut.GetByCvrAsync("16500836", 2022, CancellationToken.None);

        await act.Should().ThrowAsync<FirmaDataApiUnavailableException>();
    }

    [Fact]
    public async Task GetAvailableYearsAsync_OnANetworkFailure_ThrowsApiUnavailable()
    {
        var sut = CreateClient(StubHttpMessageHandler.Throwing(new HttpRequestException("connection refused")));

        var act = () => sut.GetAvailableYearsAsync(CancellationToken.None);

        await act.Should().ThrowAsync<FirmaDataApiUnavailableException>();
    }

    [Fact]
    public async Task SearchByNameAsync_WithNoMatches_ReturnsAnEmptyListRatherThanFailing()
    {
        var sut = CreateClient(StubHttpMessageHandler.Returning(HttpStatusCode.OK, "[]"));

        var result = await sut.SearchByNameAsync("Ukendt Firma", 2022, CancellationToken.None);

        result.Should().BeEmpty();
    }
}
