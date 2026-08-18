using FirmaData.Domain;

namespace FirmaData.Application;

public interface ICompanyEnrichmentService
{
    Task<Result<EnrichedCompany>> EnrichByCvrAsync(CvrNumber cvr, StatisticsYear? year, CancellationToken ct);

    Task<Result<IReadOnlyList<EnrichedCompany>>> SearchAndEnrichAsync(string name, StatisticsYear? year, CancellationToken ct);
}
