using FluentAssertions;

namespace FirmaData.Domain.Tests;

public class DomainModelTests
{
    // LB Forsikring A/S -- real values confirmed live against apicvr.dk and api.statbank.dk
    // during planning, used here so the fixtures reflect an actual response shape.
    private static Company LbForsikring => new(
        CvrNumber.TryCreate("16500836").Value,
        "LB FORSIKRING A/S",
        new Address("Amerika Plads 15", "2100", "København Ø"),
        IndustryCode.TryCreate("651200").Value,
        "Anden forsikring",
        1010,
        CompanyStatus.Active);

    [Fact]
    public void EnrichedCompany_WithUnavailableSource_HasNullStatisticsAndReasonRecorded()
    {
        var company = LbForsikring;

        var enriched = new EnrichedCompany(company, null, EnrichmentStatus.SourceUnavailable);

        enriched.Statistics.Should().BeNull();
        enriched.StatisticsStatus.Should().Be(EnrichmentStatus.SourceUnavailable);
        enriched.Company.Should().Be(company);
    }

    [Fact]
    public void EnrichedCompany_WithStatistics_ReportsOk()
    {
        var company = LbForsikring;
        var statistics = new IndustryStatistics(
            company.IndustryCode,
            StatisticsYear.TryCreate(2022).Value,
            166,
            15206,
            13458,
            10380);

        var enriched = new EnrichedCompany(company, statistics, EnrichmentStatus.Ok);

        enriched.Statistics.Should().Be(statistics);
        enriched.StatisticsStatus.Should().Be(EnrichmentStatus.Ok);
    }

    [Fact]
    public void IndustryStatistics_SuppressedFields_AreNullNotZero()
    {
        // Statbank reports suppressed values as ".." -- the adapter (Phase 3) maps that to
        // null. Asserting the shape holds null here, distinct from an actual reported zero.
        var statistics = new IndustryStatistics(
            IndustryCode.TryCreate("999999").Value,
            StatisticsYear.TryCreate(2022).Value,
            null,
            null,
            null,
            null);

        statistics.WorkplacesEndNovember.Should().BeNull();
        statistics.JobsEndNovember.Should().BeNull();
        statistics.FullTimeEquivalents.Should().BeNull();
        statistics.WageSumMillionDkk.Should().BeNull();
    }
}
