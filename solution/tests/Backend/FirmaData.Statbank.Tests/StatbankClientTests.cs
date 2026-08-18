using System.Net;
using FirmaData.Domain;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace FirmaData.Statbank.Tests;

public class StatbankClientTests
{
    private static readonly Uri BaseAddress = new("https://api.statbank.dk/");
    private static readonly IndustryCode Erhv651200 = IndustryCode.TryCreate("651200").Value;
    private static readonly StatisticsYear Year2022 = StatisticsYear.TryCreate(2022).Value;

    private static string Csv(string workplaces = "166", string employees = "15206", string fullTimeEquivalents = "13458", string wageSum = "10380") =>
        "BRANCHE07;TAL;TID;INDHOLD\n" +
        $"651200;ARBSTED;2022;{workplaces}\n" +
        $"651200;ANSATTE;2022;{employees}\n" +
        $"651200;FULDBESK;2022;{fullTimeEquivalents}\n" +
        $"651200;LØNSUM;2022;{wageSum}\n";

    private static StatbankClient CreateClient(StubHttpMessageHandler handler, int fallbackYear = 2022) =>
        new(
            new HttpClient(handler) { BaseAddress = BaseAddress },
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new StatbankOptions { BaseUrl = BaseAddress.ToString(), FallbackYear = fallbackYear }));

    [Fact]
    public async Task GetAsync_WithKnownIndustryAndYear_ReturnsMappedStatistics()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, Csv());
        var sut = CreateClient(handler);

        var result = await sut.GetAsync(Erhv651200, Year2022, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var stats = result.Value;
        stats.IndustryCode.Should().Be(Erhv651200);
        stats.Year.Should().Be(Year2022);
        stats.Workplaces.Should().Be(166);
        stats.Employees.Should().Be(15206);
        stats.FullTimeEquivalents.Should().Be(13458);
        stats.WageSumMillionDkk.Should().Be(10380);
    }

    [Fact]
    public async Task GetAsync_ParsesTheRealFixtureWithBomAndSemicolons()
    {
        // Real response body, captured live against api.statbank.dk during planning, including
        // its UTF-8 BOM -- stripping the BOM while saving the fixture would defeat this test.
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "erhv1-651200-2022.csv");
        var bytes = await File.ReadAllBytesAsync(fixturePath);
        var handler = StubHttpMessageHandler.ReturningBytes(HttpStatusCode.OK, bytes);
        var sut = CreateClient(handler);

        var result = await sut.GetAsync(Erhv651200, Year2022, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Workplaces.Should().Be(166);
        result.Value.Employees.Should().Be(15206);
        result.Value.FullTimeEquivalents.Should().Be(13458);
        result.Value.WageSumMillionDkk.Should().Be(10380);
    }

    [Fact]
    public async Task GetAsync_MapsDotDotToNull()
    {
        // ".." is Statbank's suppressed/missing-value marker -- distinct from an actual zero.
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, Csv(employees: ".."));
        var sut = CreateClient(handler);

        var result = await sut.GetAsync(Erhv651200, Year2022, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Employees.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_SendsRequestBodyWithValuePresentationCode()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, Csv());
        var sut = CreateClient(handler);

        await sut.GetAsync(Erhv651200, Year2022, CancellationToken.None);

        var body = await handler.LastRequest!.Content!.ReadAsStringAsync();
        body.Should().Contain("\"table\":\"ERHV1\"");
        body.Should().Contain("\"format\":\"CSV\"");
        body.Should().Contain("\"valuePresentation\":\"Code\"");
        body.Should().Contain("\"BRANCHE07\"");
        body.Should().Contain("\"651200\"");
        body.Should().Contain("\"TID\"");
        body.Should().Contain("\"2022\"");
        handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/v1/data");
    }

    [Fact]
    public async Task GetAsync_WhenYearUnavailable_ReturnsNotFound()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.BadRequest, """{"errorTypeCode":"EXTRACT-NOTFOUND"}""");
        var sut = CreateClient(handler);

        var result = await sut.GetAsync(Erhv651200, Year2022, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ResultErrorType.NotFound);
    }

    [Fact]
    public async Task GetAsync_WhenYearUnavailableWithAnUnrelatedMessage_StillReturnsNotFound()
    {
        // A message present but not naming BRANCHE07 -- the year-unavailable case, not the
        // industry-code case, so it must not be misclassified.
        var handler = StubHttpMessageHandler.Returning(
            HttpStatusCode.BadRequest, """{"errorTypeCode":"EXTRACT-NOTFOUND","message":"TID value 2099 is not valid"}""");
        var sut = CreateClient(handler);

        var result = await sut.GetAsync(Erhv651200, Year2022, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ResultErrorType.NotFound);
    }

    [Fact]
    public async Task GetAsync_WhenIndustryCodeNotRecognised_ReturnsIndustryCodeNotSupported()
    {
        // Same errorTypeCode as the year-unavailable case, but the message explicitly names the
        // rejected variable -- plan fase 6, F5's DB25/DB07 classification-drift scenario.
        var handler = StubHttpMessageHandler.Returning(
            HttpStatusCode.BadRequest, """{"errorTypeCode":"EXTRACT-NOTFOUND","message":"BRANCHE07 value 651210 is not valid"}""");
        var sut = CreateClient(handler);

        var result = await sut.GetAsync(Erhv651200, Year2022, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ResultErrorType.IndustryCodeNotSupported);
    }

    [Fact]
    public async Task GetAsync_WhenServerReturnsError_ReturnsUnavailable()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.InternalServerError);
        var sut = CreateClient(handler);

        var result = await sut.GetAsync(Erhv651200, Year2022, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ResultErrorType.Unavailable);
    }

    [Fact]
    public async Task GetAsync_WithUnrecognisedBadRequestBody_ReturnsUnexpectedNotUnavailable()
    {
        // Any 400 other than EXTRACT-NOTFOUND is a broken integration, not a transient outage --
        // Unexpected (502), not Unavailable (503 + Retry-After), since retrying can't help.
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.BadRequest, """{"errorTypeCode":"SOMETHING-ELSE"}""");
        var sut = CreateClient(handler);

        var result = await sut.GetAsync(Erhv651200, Year2022, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ResultErrorType.Unexpected);
    }

    [Fact]
    public async Task GetAsync_WithAllZeroValues_ReturnsNotFound()
    {
        // The "999999 Uoplyst aktivitet" sentinel for an unrecognised industry code doesn't
        // error -- it reports zero across every measure, which is treated as no real data.
        var handler = StubHttpMessageHandler.Returning(
            HttpStatusCode.OK,
            Csv(workplaces: "0", employees: "0", fullTimeEquivalents: "0", wageSum: "0"));
        var sut = CreateClient(handler);

        var result = await sut.GetAsync(Erhv651200, Year2022, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ResultErrorType.NotFound);
    }

    [Fact]
    public async Task GetAsync_WithMalformedCsv_ReturnsUnexpected()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, "");
        var sut = CreateClient(handler);

        var result = await sut.GetAsync(Erhv651200, Year2022, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ResultErrorType.Unexpected);
    }

    [Fact]
    public async Task GetAsync_WithDecimalCommaInWageSum_ReturnsUnexpectedInsteadOfSilentlyMisreading()
    {
        // Regression for the bug this fase fixes: NumberStyles.Number previously read "1234,5"
        // as a thousands separator and silently parsed it as 12345 -- a factor-10 error
        // presented as fact. It must now be rejected, not misread.
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, Csv(wageSum: "1234,5"));
        var sut = CreateClient(handler);

        var result = await sut.GetAsync(Erhv651200, Year2022, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ResultErrorType.Unexpected);
    }

    [Fact]
    public async Task GetAsync_WithNonNumericAnsatte_ReturnsUnexpectedNotAnUnhandledException()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, Csv(employees: "abc"));
        var sut = CreateClient(handler);

        var result = await sut.GetAsync(Erhv651200, Year2022, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ResultErrorType.Unexpected);
    }

    [Fact]
    public async Task GetAsync_WithMismatchedBrancheCode_ReturnsUnexpected()
    {
        var handler = StubHttpMessageHandler.Returning(
            HttpStatusCode.OK,
            "BRANCHE07;TAL;TID;INDHOLD\n" +
            "999999;ARBSTED;2022;166\n" +
            "999999;ANSATTE;2022;15206\n" +
            "999999;FULDBESK;2022;13458\n" +
            "999999;LØNSUM;2022;10380\n");
        var sut = CreateClient(handler);

        var result = await sut.GetAsync(Erhv651200, Year2022, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ResultErrorType.Unexpected);
    }

    [Fact]
    public async Task GetAsync_WithMismatchedYear_ReturnsUnexpected()
    {
        var handler = StubHttpMessageHandler.Returning(
            HttpStatusCode.OK,
            "BRANCHE07;TAL;TID;INDHOLD\n" +
            "651200;ARBSTED;2021;166\n" +
            "651200;ANSATTE;2021;15206\n" +
            "651200;FULDBESK;2021;13458\n" +
            "651200;LØNSUM;2021;10380\n");
        var sut = CreateClient(handler);

        var result = await sut.GetAsync(Erhv651200, Year2022, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ResultErrorType.Unexpected);
    }

    [Fact]
    public async Task GetAsync_WithDuplicateRowForSameMeasure_ReturnsUnexpected()
    {
        var handler = StubHttpMessageHandler.Returning(
            HttpStatusCode.OK,
            "BRANCHE07;TAL;TID;INDHOLD\n" +
            "651200;ARBSTED;2022;166\n" +
            "651200;ARBSTED;2022;200\n" +
            "651200;ANSATTE;2022;15206\n" +
            "651200;FULDBESK;2022;13458\n" +
            "651200;LØNSUM;2022;10380\n");
        var sut = CreateClient(handler);

        var result = await sut.GetAsync(Erhv651200, Year2022, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ResultErrorType.Unexpected);
    }

    [Fact]
    public async Task GetAsync_WithMissingMeasureRow_ReturnsUnexpected()
    {
        // Only three of the four requested TAL measures came back -- GetValueOrDefault used to
        // turn this into a silent null, indistinguishable from Statbank's own ".." marker.
        var handler = StubHttpMessageHandler.Returning(
            HttpStatusCode.OK,
            "BRANCHE07;TAL;TID;INDHOLD\n" +
            "651200;ARBSTED;2022;166\n" +
            "651200;ANSATTE;2022;15206\n" +
            "651200;FULDBESK;2022;13458\n");
        var sut = CreateClient(handler);

        var result = await sut.GetAsync(Erhv651200, Year2022, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ResultErrorType.Unexpected);
    }

    [Fact]
    public async Task GetAsync_WithEmptyCellForAMeasure_ReturnsUnexpectedNotNull()
    {
        // An empty cell is a malformed response, distinct from Statbank's explicit ".." marker
        // for a suppressed value -- it must not be read as null.
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, Csv(fullTimeEquivalents: ""));
        var sut = CreateClient(handler);

        var result = await sut.GetAsync(Erhv651200, Year2022, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ResultErrorType.Unexpected);
    }

    [Fact]
    public async Task GetAsync_WithReorderedHeaderColumns_StillParsesCorrectly()
    {
        // Column order comes from the header, not a hardcoded index -- a reordering upstream
        // must be tolerated rather than silently reading the wrong column.
        var handler = StubHttpMessageHandler.Returning(
            HttpStatusCode.OK,
            "TID;INDHOLD;BRANCHE07;TAL\n" +
            "2022;166;651200;ARBSTED\n" +
            "2022;15206;651200;ANSATTE\n" +
            "2022;13458;651200;FULDBESK\n" +
            "2022;10380;651200;LØNSUM\n");
        var sut = CreateClient(handler);

        var result = await sut.GetAsync(Erhv651200, Year2022, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Workplaces.Should().Be(166);
    }

    [Fact]
    public async Task GetAsync_WhenCancelled_Throws()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, Csv());
        var sut = CreateClient(handler);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => sut.GetAsync(Erhv651200, Year2022, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static string TableInfoJson(params int[] years) =>
        $$"""
        {
          "variables": [
            { "id": "BRANCHE07", "values": [] },
            { "id": "TID", "values": [ {{string.Join(", ", years.Select(y => $"{{ \"id\": \"{y}\" }}"))}} ] }
          ]
        }
        """;

    [Fact]
    public async Task GetAvailableYearsAsync_WithSuccessfulTableInfo_ReturnsSortedYears()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, TableInfoJson(2010, 2008, 2024, 2015));
        var sut = CreateClient(handler);

        var result = await sut.GetAvailableYearsAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Equal(2008, 2010, 2015, 2024);
        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/v1/tableinfo");
        handler.LastRequest.RequestUri.Query.Should().Contain("id=ERHV1").And.Contain("format=JSON");
    }

    [Fact]
    public async Task GetAvailableYearsAsync_CachesAcrossCalls()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, TableInfoJson(2022));
        var sut = CreateClient(handler);

        await sut.GetAvailableYearsAsync(CancellationToken.None);
        await sut.GetAvailableYearsAsync(CancellationToken.None);

        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task GetAvailableYearsAsync_WhenTableInfoFails_ReturnsFallbackYear()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.ServiceUnavailable);
        var sut = CreateClient(handler, fallbackYear: 2021);

        var result = await sut.GetAvailableYearsAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Equal(2021);
    }

    [Fact]
    public async Task GetAvailableYearsAsync_WithMalformedJson_ReturnsFallbackYear()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, "{ this is not valid json");
        var sut = CreateClient(handler, fallbackYear: 2021);

        var result = await sut.GetAvailableYearsAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Equal(2021);
    }
}
