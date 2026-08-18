using FirmaData.Application;
using FirmaData.Domain;
using Microsoft.Extensions.Caching.Memory;

namespace FirmaData.Cvr;

// Decorator over CvrApiClient (plan fase 7, F9b), mirroring
// FirmaData.Statbank.CachingIndustryStatisticsProvider's shape but with two differences: a short
// 10-minute TTL, since master data changes where annual statistics don't, and only GetByCvrAsync
// is cached. SearchByNameAsync is deliberately left uncached -- free-text search has high
// cardinality and low reuse, and a cache key built from the search string would carry PII.
public sealed class CachingCompanyDirectory(ICompanyDirectory inner, IMemoryCache cache) : ICompanyDirectory
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public async Task<Result<Company>> GetByCvrAsync(CvrNumber cvr, CancellationToken ct)
    {
        var key = $"cvr:{cvr.Value}";

        if (cache.TryGetValue(key, out Result<Company> cached))
        {
            return cached;
        }

        var result = await inner.GetByCvrAsync(cvr, ct);

        // Only a definitive answer is cached -- a transient Unavailable is left to the resilience
        // pipeline to retry, not remembered as fact for minutes.
        if (result.IsSuccess || result.Error.Type == ResultErrorType.NotFound)
        {
            cache.Set(key, result, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = CacheDuration,
                Size = 1,
            });
        }

        return result;
    }

    public Task<Result<IReadOnlyList<Company>>> SearchByNameAsync(string name, CancellationToken ct) =>
        inner.SearchByNameAsync(name, ct);
}
