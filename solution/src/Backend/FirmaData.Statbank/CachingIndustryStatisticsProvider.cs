using System.Collections.Concurrent;
using FirmaData.Application;
using FirmaData.Domain;
using Microsoft.Extensions.Caching.Memory;

namespace FirmaData.Statbank;

// Decorator over the real StatbankClient (plan section 6.2) -- the orchestrator cannot tell the
// difference, so caching is added without modifying either side (Open/Closed in its textbook
// form). Industry statistics are annual, so a 24h TTL is nearly free correctness-wise; a
// NotAvailableForYear result is cached too, briefly, so a nonsense year can't hammer the source.
public sealed class CachingIndustryStatisticsProvider(IIndustryStatisticsProvider inner, IMemoryCache cache) : IIndustryStatisticsProvider
{
    private const string CacheName = "statbank";
    private static readonly TimeSpan PositiveCacheDuration = TimeSpan.FromHours(24);
    private static readonly TimeSpan NegativeCacheDuration = TimeSpan.FromMinutes(5);

    // Process-wide by design (a static field, not an instance field) so concurrent requests for
    // the same key coalesce onto one upstream call regardless of this decorator's own DI
    // lifetime (it's registered Transient, matching the typed HttpClient it wraps).
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();

    public async Task<Result<IndustryStatistics>> GetAsync(IndustryCode code, StatisticsYear year, CancellationToken ct)
    {
        var key = $"statbank:{code.Value}:{year.Value}";

        if (cache.TryGetValue(key, out Result<IndustryStatistics> cached))
        {
            CacheMetrics.RecordHit(CacheName);
            return cached;
        }

        var gate = Gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Another caller may have populated the cache while this one waited on the gate.
            if (cache.TryGetValue(key, out cached))
            {
                CacheMetrics.RecordHit(CacheName);
                return cached;
            }

            CacheMetrics.RecordMiss(CacheName);
            var result = await inner.GetAsync(code, year, ct);

            // Only a definitive answer is cached -- a transient Unavailable is left to the
            // resilience pipeline (section 6.1) to retry, not remembered as fact for hours.
            if (result.IsSuccess || result.Error.Type == ResultErrorType.NotFound)
            {
                cache.Set(key, result, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = result.IsSuccess ? PositiveCacheDuration : NegativeCacheDuration,
                    Size = 1,
                });
            }

            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    // Year discovery has its own 24h cache inside StatbankClient itself (plan section 4.2) --
    // nothing to add here.
    public Task<Result<IReadOnlyList<int>>> GetAvailableYearsAsync(CancellationToken ct) => inner.GetAvailableYearsAsync(ct);
}
