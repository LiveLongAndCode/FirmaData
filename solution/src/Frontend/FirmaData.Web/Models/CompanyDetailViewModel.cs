namespace FirmaData.Web.Models;

// Virksomhed (plan section 15, screen 3): two cards, Stamdata and "Branchestatistik for {år}".
// All numeric fields arrive pre-formatted (da-DK) so the view stays free of formatting logic --
// Mapping/CompanyViewModelMapping is the one place that runs CultureInfo.GetCultureInfo("da-DK").
public sealed class CompanyDetailViewModel
{
    public required string CvrNumber { get; init; }

    public required string Name { get; init; }

    public required string Street { get; init; }

    public required string PostalCode { get; init; }

    public required string City { get; init; }

    public required string IndustryCode { get; init; }

    public required string IndustryDescription { get; init; }

    public required string EmployeeCountDisplay { get; init; }

    public required int Year { get; init; }

    // When false, the four figures below are null and StatisticsNotice explains why -- never
    // presented as blank or zeroed (plan section 15).
    public required bool StatisticsAvailable { get; init; }

    public string? StatisticsNotice { get; init; }

    public string? WorkplacesDisplay { get; init; }

    public string? EmployeesDisplay { get; init; }

    public string? FullTimeEquivalentsDisplay { get; init; }

    public string? WageSumDisplay { get; init; }
}
