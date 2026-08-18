using FirmaData.Domain;

namespace FirmaData.Application;

public interface IIndustryStatisticsProvider
{
    Task<Result<IndustryStatistics>> GetAsync(IndustryCode code, StatisticsYear year, CancellationToken ct);

    Task<Result<IReadOnlyList<int>>> GetAvailableYearsAsync(CancellationToken ct);
}
