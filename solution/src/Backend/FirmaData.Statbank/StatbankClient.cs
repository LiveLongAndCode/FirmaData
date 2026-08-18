using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FirmaData.Application;
using FirmaData.Domain;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace FirmaData.Statbank;

// Adapter over https://api.statbank.dk's ERHV1 table. Owns the anti-corruption boundary: the
// request/response wire shapes (StatbankDataRequest, StatbankErrorResponse,
// StatbankTableInfoResponse) never escape this project -- every public method returns Domain
// types.
public sealed class StatbankClient(HttpClient httpClient, IMemoryCache cache, IOptions<StatbankOptions> options)
    : IIndustryStatisticsProvider
{
    private const string TableId = "ERHV1";
    private const string YearsCacheKey = "statbank:years";
    private static readonly TimeSpan YearsCacheDuration = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<Result<IndustryStatistics>> GetAsync(IndustryCode code, StatisticsYear year, CancellationToken ct)
    {
        var requestBody = new StatbankDataRequest(
            TableId,
            "CSV",
            "Code",
            new[]
            {
                new StatbankVariable("BRANCHE07", new[] { code.Value }),
                new StatbankVariable("TAL", new[] { "ARBSTED", "ANSATTE", "FULDBESK", "LØNSUM" }),
                new StatbankVariable("TID", new[] { year.Value.ToString(CultureInfo.InvariantCulture) }),
            });

        HttpResponseMessage response;
        try
        {
            response = await httpClient.PostAsJsonAsync("v1/data", requestBody, JsonOptions, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException or BrokenCircuitException)
        {
            // Covers a network failure, the resilience pipeline's own attempt/total timeout, and
            // an open circuit breaker (plan section 6.1) -- all genuine unavailability, not a
            // parseable response.
            return Result.Unavailable($"Statbank API is unavailable: {ex.Message}");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                // An unavailable year is a deterministic client error, not an outage -- mapped to
                // NotFound so the orchestrator (plan section 4.3) reports NotAvailableForYear
                // rather than SourceUnavailable, and so the resilience pipeline never retries it.
                var errorBody = await TryReadErrorAsync(response, ct);
                if (errorBody?.ErrorTypeCode == "EXTRACT-NOTFOUND")
                {
                    return Result.NotFound($"No industry statistics available for {year} (industry {code}).");
                }

                return Result.Unavailable($"Statbank API rejected the request: {errorBody?.ErrorTypeCode ?? "400 Bad Request"}.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return Result.Unavailable($"Statbank API responded with status {(int)response.StatusCode}.");
            }

            Dictionary<string, string> values;
            try
            {
                values = await ParseCsvAsync(response, ct);
            }
            catch (FormatException ex)
            {
                return Result.Unexpected($"Statbank API returned a response that could not be parsed: {ex.Message}");
            }

            var workplaces = ParseNullableLong(values.GetValueOrDefault("ARBSTED"));
            var employees = ParseNullableLong(values.GetValueOrDefault("ANSATTE"));
            var fullTimeEquivalents = ParseNullableLong(values.GetValueOrDefault("FULDBESK"));
            var wageSum = ParseNullableDecimal(values.GetValueOrDefault("LØNSUM"));

            // An unrecognised industry code doesn't error -- Statbank buckets it under the
            // "999999 Uoplyst aktivitet" catch-all and reports zero across every measure. A
            // zero-everything result is treated as "no real data", not presented as fact.
            if (workplaces is 0 && employees is 0 && fullTimeEquivalents is 0 && wageSum is 0)
            {
                return Result.NotFound($"No industry statistics available for {year} (industry {code}).");
            }

            return new IndustryStatistics(code, year, workplaces, employees, fullTimeEquivalents, wageSum);
        }
    }

    public async Task<Result<IReadOnlyList<int>>> GetAvailableYearsAsync(CancellationToken ct)
    {
        if (cache.TryGetValue(YearsCacheKey, out IReadOnlyList<int>? cachedYears) && cachedYears is not null)
        {
            return Result<IReadOnlyList<int>>.Success(cachedYears);
        }

        var years = await TryFetchAvailableYearsAsync(ct);
        if (years is null)
        {
            // Live discovery failed -- fall back to a configured value rather than surfacing an
            // error, since callers (year defaulting, the UI dropdown) need something usable.
            return Result<IReadOnlyList<int>>.Success(new[] { options.Value.FallbackYear });
        }

        // Size must be set explicitly: the shared IMemoryCache is registered with a SizeLimit
        // (plan section 6.2, applied to the whole cache instance in ServiceCollectionExtensions),
        // and a size-limited cache throws on any entry that doesn't declare one.
        cache.Set(YearsCacheKey, years, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = YearsCacheDuration,
            Size = 1,
        });
        return Result<IReadOnlyList<int>>.Success(years);
    }

    private async Task<IReadOnlyList<int>?> TryFetchAvailableYearsAsync(CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient.GetAsync($"v1/tableinfo?id={TableId}&format=JSON", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException or BrokenCircuitException)
        {
            return null;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            StatbankTableInfoResponse? info;
            try
            {
                info = await response.Content.ReadFromJsonAsync<StatbankTableInfoResponse>(JsonOptions, ct);
            }
            catch (JsonException)
            {
                return null;
            }

            var yearValues = info?.Variables?
                .FirstOrDefault(variable => string.Equals(variable.Id, "TID", StringComparison.OrdinalIgnoreCase))
                ?.Values;

            if (yearValues is null)
            {
                return null;
            }

            var years = yearValues
                .Select(value => value.Id)
                .Where(id => int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                .Select(id => int.Parse(id!, CultureInfo.InvariantCulture))
                .Distinct()
                .OrderBy(year => year)
                .ToList();

            return years.Count > 0 ? years : null;
        }
    }

    private static async Task<StatbankErrorResponse?> TryReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<StatbankErrorResponse>(JsonOptions, ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Statbank's CSV response is semicolon-separated and begins with a UTF-8 BOM
    // (BRANCHE07;TAL;TID;INDHOLD header, one row per requested TAL code). StreamReader with
    // BOM detection strips it; re-encoding through a plain string would leave a stray U+FEFF
    // on the first column name.
    private static async Task<Dictionary<string, string>> ParseCsvAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var header = await reader.ReadLineAsync(ct);
        if (header is null)
        {
            throw new FormatException("Statbank CSV response was empty.");
        }

        var values = new Dictionary<string, string>();
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var columns = line.Split(';');
            if (columns.Length < 4)
            {
                continue;
            }

            // BRANCHE07;TAL;TID;INDHOLD
            values[columns[1]] = columns[3];
        }

        return values;
    }

    // Statbank writes ".." for a suppressed/missing value -- distinct from an actual zero, so
    // it maps to null rather than 0 (plan section 4.2).
    private static long? ParseNullableLong(string? raw) =>
        string.IsNullOrEmpty(raw) || raw == ".."
            ? null
            : long.Parse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static decimal? ParseNullableDecimal(string? raw) =>
        string.IsNullOrEmpty(raw) || raw == ".."
            ? null
            : decimal.Parse(raw, NumberStyles.Number, CultureInfo.InvariantCulture);
}
