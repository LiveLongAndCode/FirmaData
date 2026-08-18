using System.Net;
using FirmaData.Domain;
using FluentAssertions;

namespace FirmaData.Cvr.Tests;

public class CvrApiClientTests
{
    // Real response captured live against apicvr.dk (GET /api/v1/16500836) during planning,
    // trimmed of fields this adapter doesn't map. p_units simplified to [] -- its contents are
    // never read, and using the real nested shape would add noise without adding coverage.
    private static string LbForsikringJson(bool bankrupt = false, string status = "NORMAL", string? industryCode = "651200") =>
        $$"""
        {
          "vat": 16500836,
          "name": "LB FORSIKRING A/S",
          "address": "Amerika Plads 15",
          "zipcode": 2100,
          "city": "København Ø",
          "employees": 1010,
          "industrycode": {{(industryCode is null ? "null" : $"\"{industryCode}\"")}},
          "industrydesc": "Anden forsikring",
          "bankrupt": {{(bankrupt ? "true" : "false")}},
          "status": "{{status}}",
          "p_units": []
        }
        """;

    private static readonly Uri BaseAddress = new("https://apicvr.dk/");

    private static CvrApiClient CreateClient(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = BaseAddress });

    [Fact]
    public async Task GetByCvrAsync_WithKnownCvr_ReturnsMappedCompany()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, LbForsikringJson());
        var sut = CreateClient(handler);
        var cvr = CvrNumber.TryCreate("16500836").Value;

        var result = await sut.GetByCvrAsync(cvr, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var company = result.Value;
        company.Cvr.Should().Be(cvr);
        company.Name.Should().Be("LB FORSIKRING A/S");
        company.Address.Should().Be(new Address("Amerika Plads 15", "2100", "København Ø"));
        company.IndustryCode.Value.Should().Be("651200");
        company.IndustryDescription.Should().Be("Anden forsikring");
        company.EmployeeCount.Should().Be(1010);
        company.Status.Should().Be(CompanyStatus.Active);
    }

    [Fact]
    public async Task GetByCvrAsync_RequestsTheCvrNumberPath()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, LbForsikringJson());
        var sut = CreateClient(handler);

        await sut.GetByCvrAsync(CvrNumber.TryCreate("16500836").Value, CancellationToken.None);

        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/api/v1/16500836");
    }

    [Fact]
    public async Task GetByCvrAsync_WithUnknownCvr_ReturnsNotFound()
    {
        // The critical quirk from plan section 4.1: apicvr.dk returns HTTP 200, not 404, for
        // an unknown CVR number. A status-code-only implementation would pass every other test
        // in this file and still be wrong -- this is the one that catches it.
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, """{"error":"NOT_FOUND"}""");
        var sut = CreateClient(handler);

        var result = await sut.GetByCvrAsync(CvrNumber.TryCreate("16500836").Value, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ResultErrorType.NotFound);
    }

    [Fact]
    public async Task GetByCvrAsync_WhenServerReturnsError_ReturnsUnavailable()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.InternalServerError);
        var sut = CreateClient(handler);

        var result = await sut.GetByCvrAsync(CvrNumber.TryCreate("16500836").Value, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ResultErrorType.Unavailable);
    }

    [Fact]
    public async Task GetByCvrAsync_WithMalformedJson_ReturnsUnexpected()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, "{ this is not valid json");
        var sut = CreateClient(handler);

        var result = await sut.GetByCvrAsync(CvrNumber.TryCreate("16500836").Value, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ResultErrorType.Unexpected);
    }

    [Fact]
    public async Task GetByCvrAsync_WhenCancelled_Throws()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, LbForsikringJson());
        var sut = CreateClient(handler);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => sut.GetByCvrAsync(CvrNumber.TryCreate("16500836").Value, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(false, "NORMAL", CompanyStatus.Active)]
    [InlineData(false, "SOMETHING_UNSEEN", CompanyStatus.Unknown)]
    [InlineData(true, "NORMAL", CompanyStatus.Bankrupt)] // bankrupt flag takes priority over status text
    public async Task GetByCvrAsync_MapsStatus(bool bankrupt, string status, CompanyStatus expected)
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, LbForsikringJson(bankrupt, status));
        var sut = CreateClient(handler);

        var result = await sut.GetByCvrAsync(CvrNumber.TryCreate("16500836").Value, CancellationToken.None);

        result.Value.Status.Should().Be(expected);
    }

    [Fact]
    public async Task SearchByNameAsync_WithMatches_ReturnsMappedCompanies()
    {
        // Real response captured live against apicvr.dk (GET /api/v1/search/company/lb%20forsikring).
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, $"[{LbForsikringJson()}]");
        var sut = CreateClient(handler);

        var result = await sut.SearchByNameAsync("lb forsikring", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle(c => c.Name == "LB FORSIKRING A/S");
    }

    [Fact]
    public async Task SearchByNameAsync_WithNoMatches_ReturnsEmptySuccess()
    {
        // Confirmed live: a name search with no matches returns 200 with an empty array, not
        // a failure -- distinct from the single-CVR lookup's NotFound.
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, "[]");
        var sut = CreateClient(handler);

        var result = await sut.SearchByNameAsync("no such company", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchByNameAsync_SkipsRowsThatFailToMap()
    {
        var validRow = LbForsikringJson();
        var invalidRow = LbForsikringJson(industryCode: "BAD"); // fails IndustryCode.TryCreate
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, $"[{validRow}, {invalidRow}]");
        var sut = CreateClient(handler);

        var result = await sut.SearchByNameAsync("lb", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
    }

    [Fact]
    public async Task SearchByNameAsync_UrlEncodesTheName()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, "[]");
        var sut = CreateClient(handler);

        await sut.SearchByNameAsync("lb forsikring", CancellationToken.None);

        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/api/v1/search/company/lb%20forsikring");
    }
}
