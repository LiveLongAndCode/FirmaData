using System.Diagnostics;
using FirmaData.Domain;

namespace FirmaData.Application;

// Facade over the two adapters (plan section 4.3). The industry code needed for the Statbank
// call comes from the CVR lookup itself, so a single-CVR enrichment is necessarily sequential --
// the calls cannot be parallelised. Name search resolves statistics for the distinct set of
// industry codes concurrently instead, since several results often share one industry.
//
// maxConcurrentStatisticsCalls is a plain primitive, not an Options type: this project must not
// take a dependency on the Options package or either adapter project (ArchitectureTests), so the
// composition root (FirmaData.Api's Program.cs) reads the actual limit from SearchOptions and
// passes just the int through a factory registration.
public sealed class CompanyEnrichmentService(
    ICompanyDirectory directory, IIndustryStatisticsProvider statistics, int maxConcurrentStatisticsCalls = 4)
    : ICompanyEnrichmentService
{
    public async Task<Result<EnrichedCompany>> EnrichByCvrAsync(CvrNumber cvr, StatisticsYear? year, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var company = await directory.GetByCvrAsync(cvr, ct);
            if (company.IsFailure)
            {
                return company.Error;
            }

            var resolvedYear = await ResolveYearAsync(year, ct);
            var stats = await statistics.GetAsync(company.Value.IndustryCode, resolvedYear, ct);
            var status = stats.ToStatus();

            RecordDegradedIfAny(status);
            return new EnrichedCompany(company.Value, stats.ValueOrDefault, status);
        }
        finally
        {
            EnrichmentMetrics.RecordDuration("cvr", stopwatch.Elapsed.TotalSeconds);
        }
    }

    public async Task<Result<IReadOnlyList<EnrichedCompany>>> SearchAndEnrichAsync(string name, StatisticsYear? year, int limit, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var companies = await directory.SearchByNameAsync(name, ct);
            if (companies.IsFailure)
            {
                return companies.Error;
            }

            if (companies.Value.Count == 0)
            {
                return Array.Empty<EnrichedCompany>();
            }

            // Cap before enrichment, not after: the directory has already ranked its results by
            // relevance, so anything beyond `limit` is both the least relevant and never worth
            // the Statbank call a wide search would otherwise fan out into.
            var limited = companies.Value.Take(limit).ToList();

            var resolvedYear = await ResolveYearAsync(year, ct);

            // Distinct so ten results sharing three industries cost three Statbank calls, not
            // ten. The semaphore bounds how many of those calls run at once, so a wide search
            // against a shared public service can't fan out unbounded.
            var distinctCodes = limited.Select(company => company.IndustryCode).Distinct().ToList();
            using var throttle = new SemaphoreSlim(maxConcurrentStatisticsCalls);
            var lookups = await Task.WhenAll(distinctCodes.Select(async code =>
            {
                await throttle.WaitAsync(ct);
                try
                {
                    return (Code: code, Stats: await statistics.GetAsync(code, resolvedYear, ct));
                }
                finally
                {
                    throttle.Release();
                }
            }));
            var statisticsByCode = lookups.ToDictionary(lookup => lookup.Code, lookup => lookup.Stats);

            var enriched = limited
                .Select(company =>
                {
                    var stats = statisticsByCode[company.IndustryCode];
                    var status = stats.ToStatus();
                    RecordDegradedIfAny(status);
                    return new EnrichedCompany(company, stats.ValueOrDefault, status);
                })
                .ToList();

            return enriched;
        }
        finally
        {
            EnrichmentMetrics.RecordDuration("name", stopwatch.Elapsed.TotalSeconds);
        }
    }

    private static void RecordDegradedIfAny(EnrichmentStatus status)
    {
        if (status != EnrichmentStatus.Ok)
        {
            EnrichmentMetrics.RecordDegraded(status.ToString());
        }
    }

    private async Task<StatisticsYear> ResolveYearAsync(StatisticsYear? year, CancellationToken ct)
    {
        if (year is not null)
        {
            return year.Value;
        }

        var availableYears = await statistics.GetAvailableYearsAsync(ct);

        // GetAvailableYearsAsync always succeeds -- on live-discovery failure it falls back to a
        // configured year rather than surfacing an error (FirmaData.Statbank, plan section 4.2).
        // EarliestYear here only guards the theoretical case of a genuinely empty list.
        var latestYear = availableYears.IsSuccess && availableYears.Value.Count > 0
            ? availableYears.Value.Max()
            : StatisticsYear.EarliestYear;

        return StatisticsYear.TryCreate(latestYear).Value;
    }
}
