using System.Globalization;
using FirmaData.Contracts;
using FirmaData.Web.Models;

namespace FirmaData.Web.Mapping;

// Contracts DTOs -> Web view models (plan section 15: "view models separate from
// FirmaData.Contracts DTOs"), with da-DK number formatting applied once, here, rather than in
// the views.
public static class CompanyViewModelMapping
{
    private static readonly CultureInfo DanishCulture = CultureInfo.GetCultureInfo("da-DK");

    public static CompanySummaryViewModel ToSummaryViewModel(this EnrichedCompanyResponse response) =>
        new(response.Company.CvrNumber, response.Company.Name, response.Company.Address.City, response.Company.IndustryDescription);

    public static CompanyDetailViewModel ToDetailViewModel(this EnrichedCompanyResponse response, int year)
    {
        var company = response.Company;
        var statistics = response.IndustryStatistics;

        return new CompanyDetailViewModel
        {
            CvrNumber = company.CvrNumber,
            Name = company.Name,
            Street = company.Address.Street,
            PostalCode = company.Address.PostalCode,
            City = company.Address.City,
            IndustryCode = company.IndustryCode,
            IndustryDescription = company.IndustryDescription,
            EmployeeCountDisplay = FormatCount(company.EmployeeCount),
            Year = year,
            StatisticsAvailable = statistics is not null,
            StatisticsNotice = StatisticsNotice(response.StatisticsStatus, year),
            WorkplacesDisplay = FormatCount(statistics?.Workplaces),
            EmployeesDisplay = FormatCount(statistics?.Employees),
            FullTimeEquivalentsDisplay = FormatCount(statistics?.FullTimeEquivalents),
            WageSumDisplay = statistics?.WageSumMillionDkk is { } wageSum
                ? $"{wageSum.ToString("N0", DanishCulture)} mio. kr."
                : null,
        };
    }

    private static string FormatCount(long? value) => value?.ToString("N0", DanishCulture) ?? "Ukendt";

    // "Ok" -> no notice, the figures speak for themselves. The other two are Domain's
    // EnrichmentStatus cases, serialized as a plain string in EnrichedCompanyResponse (Contracts
    // must not depend on Domain -- plan section 2.1) -- matched here by name rather than pulled
    // in as an enum reference.
    private static string? StatisticsNotice(string statisticsStatus, int year) => statisticsStatus switch
    {
        "NotAvailableForYear" => $"Der er ikke branchestatistik tilgængelig for {year}.",
        "SourceUnavailable" => "Branchestatistik kunne ikke hentes lige nu. Prøv igen senere.",
        _ => null,
    };
}
