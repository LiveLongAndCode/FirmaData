using FirmaData.Contracts;
using FirmaData.Web.Mapping;
using FluentAssertions;

namespace FirmaData.Web.Tests;

public class CompanyViewModelMappingTests
{
    private static readonly CompanyDto LbForsikring = new(
        "16500836", "LB FORSIKRING A/S",
        new AddressDto("Amerika Plads 15", "2100", "København Ø"),
        "651200", "Anden forsikring", 1010);

    private static EnrichedCompanyResponse Response(string statisticsStatus, IndustryStatisticsDto? statistics) =>
        new(LbForsikring, statistics, statisticsStatus, DateTimeOffset.UtcNow, new SourcesDto("apicvr.dk", "api.statbank.dk/ERHV1"));

    [Fact]
    public void ToDetailViewModel_WhenOk_FormatsFiguresWithDanishThousandsSeparators()
    {
        var statistics = new IndustryStatisticsDto("651200", 2022, 166, 15206, 13458, 10380m);
        var response = Response("Ok", statistics);

        var model = response.ToDetailViewModel(2022);

        model.StatisticsAvailable.Should().BeTrue();
        model.StatisticsNotice.Should().BeNull();
        model.WorkplacesDisplay.Should().Be("166");
        model.EmployeesDisplay.Should().Be("15.206");
        model.FullTimeEquivalentsDisplay.Should().Be("13.458");
        model.WageSumDisplay.Should().Be("10.380 mio. kr.");
        model.EmployeeCountDisplay.Should().Be("1.010");
    }

    [Fact]
    public void ToDetailViewModel_WhenNotAvailableForYear_ShowsDanishNoticeNamingTheYear()
    {
        var response = Response("NotAvailableForYear", null);

        var model = response.ToDetailViewModel(2008);

        model.StatisticsAvailable.Should().BeFalse();
        model.StatisticsNotice.Should().Be("Der er ikke branchestatistik tilgængelig for 2008.");
    }

    [Fact]
    public void ToDetailViewModel_WhenSourceUnavailable_ShowsDanishUnavailableNotice()
    {
        var response = Response("SourceUnavailable", null);

        var model = response.ToDetailViewModel(2022);

        model.StatisticsAvailable.Should().BeFalse();
        model.StatisticsNotice.Should().Be("Branchestatistik kunne ikke hentes lige nu. Prøv igen senere.");
    }

    [Fact]
    public void ToDetailViewModel_WhenIndustryCodeNotSupported_ShowsDanishNotice()
    {
        var response = Response("IndustryCodeNotSupported", null);

        var model = response.ToDetailViewModel(2022);

        model.StatisticsAvailable.Should().BeFalse();
        model.StatisticsNotice.Should().Be("Branchekoden understøttes endnu ikke af Danmarks Statistik.");
    }

    [Fact]
    public void ToDetailViewModel_WhenEmployeeCountIsNull_DisplaysUkendtRatherThanZero()
    {
        var company = LbForsikring with { EmployeeCount = null };
        var response = new EnrichedCompanyResponse(company, null, "SourceUnavailable", DateTimeOffset.UtcNow, new SourcesDto("apicvr.dk", "api.statbank.dk/ERHV1"));

        var model = response.ToDetailViewModel(2022);

        model.EmployeeCountDisplay.Should().Be("Ukendt");
    }

    [Fact]
    public void ToSummaryViewModel_ProjectsNameCvrCityAndIndustryOnly()
    {
        var response = Response("Ok", new IndustryStatisticsDto("651200", 2022, 166, 15206, 13458, 10380m));

        var summary = response.ToSummaryViewModel();

        summary.Should().Be(new Models.CompanySummaryViewModel("16500836", "LB FORSIKRING A/S", "København Ø", "Anden forsikring"));
    }
}
