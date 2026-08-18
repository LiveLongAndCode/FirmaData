using FirmaData.Domain;
using FluentAssertions;
using NSubstitute;

namespace FirmaData.Application.Tests;

public class CompanyEnrichmentServiceTests
{
    private static readonly CvrNumber Cvr = CvrNumber.TryCreate("16500836").Value;
    private static readonly IndustryCode Erhv651200 = IndustryCode.TryCreate("651200").Value;
    private static readonly StatisticsYear Year2022 = StatisticsYear.TryCreate(2022).Value;

    // LB Forsikring A/S -- real values confirmed live against apicvr.dk during planning.
    private static Company LbForsikring => new(
        Cvr,
        "LB FORSIKRING A/S",
        new Address("Amerika Plads 15", "2100", "København Ø"),
        Erhv651200,
        "Anden forsikring",
        1010,
        CompanyStatus.Active);

    private static IndustryStatistics Statistics(IndustryCode? code = null) => new(
        code ?? Erhv651200, Year2022, 166, 15206, 13458, 10380);

    private static (ICompanyDirectory Directory, IIndustryStatisticsProvider Statistics, CompanyEnrichmentService Sut) CreateSut()
    {
        var directory = Substitute.For<ICompanyDirectory>();
        var statistics = Substitute.For<IIndustryStatisticsProvider>();
        var sut = new CompanyEnrichmentService(directory, statistics);
        return (directory, statistics, sut);
    }

    // --- Degradation matrix (plan section 6.3) ---------------------------------------------

    [Fact]
    public async Task EnrichByCvrAsync_WhenBothSourcesOk_ReturnsFullResponseWithOkStatus()
    {
        var (directory, statistics, sut) = CreateSut();
        directory.GetByCvrAsync(Cvr, Arg.Any<CancellationToken>()).Returns(LbForsikring);
        statistics.GetAsync(Erhv651200, Year2022, Arg.Any<CancellationToken>()).Returns(Statistics());

        var result = await sut.EnrichByCvrAsync(Cvr, Year2022, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Company.Should().Be(LbForsikring);
        result.Value.Statistics.Should().Be(Statistics());
        result.Value.StatisticsStatus.Should().Be(EnrichmentStatus.Ok);
    }

    [Fact]
    public async Task EnrichByCvrAsync_WhenStatisticsSourceUnavailable_ReturnsMasterDataDegraded()
    {
        var (directory, statistics, sut) = CreateSut();
        directory.GetByCvrAsync(Cvr, Arg.Any<CancellationToken>()).Returns(LbForsikring);
        statistics.GetAsync(Erhv651200, Year2022, Arg.Any<CancellationToken>())
            .Returns(Result.Unavailable("Statbank is down."));

        var result = await sut.EnrichByCvrAsync(Cvr, Year2022, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Company.Should().Be(LbForsikring);
        result.Value.Statistics.Should().BeNull();
        result.Value.StatisticsStatus.Should().Be(EnrichmentStatus.SourceUnavailable);
    }

    [Fact]
    public async Task EnrichByCvrAsync_WhenYearUnavailable_ReturnsMasterDataWithNotAvailableForYear()
    {
        var (directory, statistics, sut) = CreateSut();
        directory.GetByCvrAsync(Cvr, Arg.Any<CancellationToken>()).Returns(LbForsikring);
        statistics.GetAsync(Erhv651200, Year2022, Arg.Any<CancellationToken>())
            .Returns(Result.NotFound("No statistics for that year."));

        var result = await sut.EnrichByCvrAsync(Cvr, Year2022, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Statistics.Should().BeNull();
        result.Value.StatisticsStatus.Should().Be(EnrichmentStatus.NotAvailableForYear);
    }

    [Fact]
    public async Task EnrichByCvrAsync_WhenCvrLookupFails_PropagatesErrorAndNeverCallsStatistics()
    {
        var (directory, statistics, sut) = CreateSut();
        directory.GetByCvrAsync(Cvr, Arg.Any<CancellationToken>())
            .Returns(Result.Unavailable("CVR API is down."));

        var result = await sut.EnrichByCvrAsync(Cvr, Year2022, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ResultErrorType.Unavailable);
        await statistics.DidNotReceive().GetAsync(Arg.Any<IndustryCode>(), Arg.Any<StatisticsYear>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnrichByCvrAsync_WhenCvrNotFound_PropagatesNotFound()
    {
        var (directory, _, sut) = CreateSut();
        directory.GetByCvrAsync(Cvr, Arg.Any<CancellationToken>()).Returns(Result.NotFound());

        var result = await sut.EnrichByCvrAsync(Cvr, Year2022, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ResultErrorType.NotFound);
    }

    // --- Year resolution ---------------------------------------------------------------------

    [Fact]
    public async Task EnrichByCvrAsync_WithExplicitYear_DoesNotQueryAvailableYears()
    {
        var (directory, statistics, sut) = CreateSut();
        directory.GetByCvrAsync(Cvr, Arg.Any<CancellationToken>()).Returns(LbForsikring);
        statistics.GetAsync(Erhv651200, Year2022, Arg.Any<CancellationToken>()).Returns(Statistics());

        await sut.EnrichByCvrAsync(Cvr, Year2022, CancellationToken.None);

        await statistics.DidNotReceive().GetAvailableYearsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnrichByCvrAsync_WithNoYear_ResolvesLatestAvailableYear()
    {
        var (directory, statistics, sut) = CreateSut();
        var latestYear = StatisticsYear.TryCreate(2024).Value;
        directory.GetByCvrAsync(Cvr, Arg.Any<CancellationToken>()).Returns(LbForsikring);
        statistics.GetAvailableYearsAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<int>>.Success([2020, 2022, 2024]));
        statistics.GetAsync(Erhv651200, latestYear, Arg.Any<CancellationToken>()).Returns(Statistics());

        var result = await sut.EnrichByCvrAsync(Cvr, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await statistics.Received(1).GetAsync(Erhv651200, latestYear, Arg.Any<CancellationToken>());
    }

    // --- Name search ---------------------------------------------------------------------------

    [Fact]
    public async Task SearchAndEnrichAsync_WhenDirectorySearchFails_PropagatesError()
    {
        var (directory, _, sut) = CreateSut();
        directory.SearchByNameAsync("lb", Arg.Any<CancellationToken>())
            .Returns(Result.Unavailable("CVR API is down."));

        var result = await sut.SearchAndEnrichAsync("lb", Year2022, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ResultErrorType.Unavailable);
    }

    [Fact]
    public async Task SearchAndEnrichAsync_WithNoMatches_ReturnsEmptySuccessWithoutQueryingStatistics()
    {
        var (directory, statistics, sut) = CreateSut();
        directory.SearchByNameAsync("no such company", Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<Company>>.Success([]));

        var result = await sut.SearchAndEnrichAsync("no such company", Year2022, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
        await statistics.DidNotReceive().GetAsync(Arg.Any<IndustryCode>(), Arg.Any<StatisticsYear>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAndEnrichAsync_DedupesStatisticsLookupsByDistinctIndustryCode()
    {
        var (directory, statistics, sut) = CreateSut();
        var otherIndustry = IndustryCode.TryCreate("620100").Value;

        // Ten results, but only two distinct industry codes.
        var companies = Enumerable.Range(0, 10)
            .Select(i => LbForsikring with
            {
                Cvr = CvrNumber.TryCreate("16500836").Value,
                IndustryCode = i % 2 == 0 ? Erhv651200 : otherIndustry,
            })
            .ToList();

        directory.SearchByNameAsync("lb", Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<Company>>.Success(companies));
        statistics.GetAsync(Erhv651200, Year2022, Arg.Any<CancellationToken>()).Returns(Statistics(Erhv651200));
        statistics.GetAsync(otherIndustry, Year2022, Arg.Any<CancellationToken>()).Returns(Statistics(otherIndustry));

        var result = await sut.SearchAndEnrichAsync("lb", Year2022, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(10);
        result.Value.Should().OnlyContain(company => company.StatisticsStatus == EnrichmentStatus.Ok);
        await statistics.Received(1).GetAsync(Erhv651200, Year2022, Arg.Any<CancellationToken>());
        await statistics.Received(1).GetAsync(otherIndustry, Year2022, Arg.Any<CancellationToken>());
    }
}
