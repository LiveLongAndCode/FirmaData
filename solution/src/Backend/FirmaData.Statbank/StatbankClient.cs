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

            long? workplaces, employees, fullTimeEquivalents;
            decimal? wageSum;
            try
            {
                var values = await ParseCsvAsync(response, code, year, ct);
                workplaces = ParseNullableLong(values["ARBSTED"]);
                employees = ParseNullableLong(values["ANSATTE"]);
                fullTimeEquivalents = ParseNullableLong(values["FULDBESK"]);
                wageSum = ParseNullableDecimal(values["LØNSUM"]);
            }
            catch (FormatException ex)
            {
                return Result.Unexpected($"Statbank API returned a response that could not be parsed: {ex.Message}");
            }

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

    private static readonly string[] RequiredMeasures = ["ARBSTED", "ANSATTE", "FULDBESK", "LØNSUM"];

    // Statbank's CSV response is semicolon-separated and begins with a UTF-8 BOM
    // (BRANCHE07;TAL;TID;INDHOLD header, one row per requested TAL code). StreamReader with
    // BOM detection strips it; re-encoding through a plain string would leave a stray U+FEFF
    // on the first column name.
    //
    // Every row is validated against the cell that was actually requested (BRANCHE07/TID) and
    // against the shape of the request itself (all four TAL measures present, no duplicates),
    // rather than trusting positional columns -- a contract drift here (reordered columns, a
    // silently substituted industry code) is a broken integration, not data to render as fact.
    private static async Task<Dictionary<string, string>> ParseCsvAsync(
        HttpResponseMessage response, IndustryCode code, StatisticsYear year, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var header = await reader.ReadLineAsync(ct);
        if (header is null)
        {
            throw new FormatException("Statbank CSV response was empty.");
        }

        var columnIndex = header.Split(';')
            .Select((name, index) => (name, index))
            .ToDictionary(pair => pair.name, pair => pair.index, StringComparer.Ordinal);

        int GetColumn(string name) => columnIndex.TryGetValue(name, out var index)
            ? index
            : throw new FormatException($"Statbank CSV response is missing the '{name}' column.");

        var branchColumn = GetColumn("BRANCHE07");
        var measureColumn = GetColumn("TAL");
        var periodColumn = GetColumn("TID");
        var contentColumn = GetColumn("INDHOLD");
        var minColumnCount = new[] { branchColumn, measureColumn, periodColumn, contentColumn }.Max() + 1;

        var expectedBranch = code.Value;
        var expectedPeriod = year.Value.ToString(CultureInfo.InvariantCulture);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var columns = line.Split(';');
            if (columns.Length < minColumnCount)
            {
                throw new FormatException($"Statbank CSV row has fewer columns than the header declares: '{line}'.");
            }

            if (!string.Equals(columns[branchColumn], expectedBranch, StringComparison.Ordinal))
            {
                throw new FormatException(
                    $"Statbank CSV row reports BRANCHE07 '{columns[branchColumn]}', expected '{expectedBranch}'.");
            }

            if (!string.Equals(columns[periodColumn], expectedPeriod, StringComparison.Ordinal))
            {
                throw new FormatException(
                    $"Statbank CSV row reports TID '{columns[periodColumn]}', expected '{expectedPeriod}'.");
            }

            var measure = columns[measureColumn];
            if (!values.TryAdd(measure, columns[contentColumn]))
            {
                throw new FormatException($"Statbank CSV response has more than one row for TAL '{measure}'.");
            }
        }

        foreach (var measure in RequiredMeasures)
        {
            if (!values.ContainsKey(measure))
            {
                throw new FormatException($"Statbank CSV response is missing a row for TAL '{measure}'.");
            }
        }

        return values;
    }

    // Statbank writes ".." for a suppressed/missing value -- distinct from an actual zero, so it
    // maps to null rather than 0 (plan section 4.2). Any other value must parse; an empty cell is
    // a malformed response, not a suppressed one, so it falls through to Parse and throws.
    private static long? ParseNullableLong(string raw) =>
        raw == ".."
            ? null
            : long.Parse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture);

    // AllowThousands is deliberately excluded: with it, InvariantCulture reads a decimal comma
    // (e.g. "1234,5") as a thousands separator and silently parses it as 12345 -- a factor-10
    // error presented as fact, worse than a rejected value. Without it, the same input throws
    // FormatException and surfaces as a controlled Unexpected/502 instead.
    private static decimal? ParseNullableDecimal(string raw) =>
        raw == ".."
            ? null
            : decimal.Parse(raw, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
}
