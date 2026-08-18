using FirmaData.Contracts;
using FirmaData.Domain;

namespace FirmaData.Api.Mapping;

// The only place Domain types are translated to the wire contract (§5.3) -- Contracts has no
// dependency on Domain (§2.1), so this mapping necessarily lives in the composition root.
internal static class EnrichedCompanyMapping
{
    private const string CvrSourceName = "apicvr.dk";
    private const string StatbankSourceName = "api.statbank.dk/ERHV1";

    // Takes TimeProvider as a parameter from the controller rather than making this class
    // instance-based (plan fase 7, F9c) -- ToDto below stays a plain static mapping.
    public static EnrichedCompanyResponse ToResponse(this EnrichedCompany enriched, TimeProvider timeProvider) => new(
        enriched.Company.ToDto(),
        enriched.Statistics?.ToDto(),
        enriched.StatisticsStatus.ToString(),
        timeProvider.GetUtcNow(),
        new SourcesDto(CvrSourceName, StatbankSourceName));

    private static CompanyDto ToDto(this Company company) => new(
        company.Cvr.Value,
        company.Name,
        new AddressDto(company.Address.Street, company.Address.PostalCode, company.Address.City),
        company.IndustryCode.Value,
        company.IndustryDescription,
        company.EmployeeCount);

    private static IndustryStatisticsDto ToDto(this IndustryStatistics statistics) => new(
        statistics.IndustryCode.Value,
        statistics.Year.Value,
        statistics.Workplaces,
        statistics.Employees,
        statistics.FullTimeEquivalents,
        statistics.WageSumMillionDkk);
}
