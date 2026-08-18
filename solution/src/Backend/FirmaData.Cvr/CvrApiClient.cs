using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FirmaData.Application;
using FirmaData.Domain;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace FirmaData.Cvr;

// Adapter over https://apicvr.dk. Owns the anti-corruption boundary: CvrCompanyResponse (the
// external wire shape) never escapes this project -- every public method returns Domain types.
public sealed class CvrApiClient(HttpClient httpClient) : ICompanyDirectory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<Result<Company>> GetByCvrAsync(CvrNumber cvr, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient.GetAsync($"api/v1/{cvr.Value}", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException or BrokenCircuitException)
        {
            // Covers a network failure, the resilience pipeline's own attempt/total timeout, and
            // an open circuit breaker (plan section 6.1) -- all genuine unavailability.
            return Result.Unavailable($"CVR API is unavailable: {ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return Result.Unavailable($"CVR API responded with status {(int)response.StatusCode}.");
            }

            CvrCompanyResponse? dto;
            try
            {
                dto = await response.Content.ReadFromJsonAsync<CvrCompanyResponse>(JsonOptions, ct);
            }
            catch (JsonException ex)
            {
                return Result.Unexpected($"CVR API returned a response that could not be parsed: {ex.Message}");
            }

            // apicvr.dk returns HTTP 200 with {"error":"NOT_FOUND"} for an unknown CVR number
            // rather than a 404 -- this is the case that must be checked explicitly, not
            // inferred from the status code.
            if (dto is null || dto.Error is not null)
            {
                return Result.NotFound($"No company found for CVR number {cvr.Value}.");
            }

            return MapToCompany(dto);
        }
    }

    public async Task<Result<IReadOnlyList<Company>>> SearchByNameAsync(string name, CancellationToken ct)
    {
        var upstreamQuery = NormalizeForUpstream(name);
        var encodedName = Uri.EscapeDataString(upstreamQuery);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.GetAsync($"api/v1/search/company/{encodedName}", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException or BrokenCircuitException)
        {
            return Result.Unavailable($"CVR API is unavailable: {ex.Message}");
        }

        using (response)
        {
            // apicvr.dk's search is path-based, so a query with no matches reports 404 rather
            // than 200 with an empty array -- a status-code-only mapping would otherwise present
            // an ordinary "nothing found" as a 503 outage. Any other non-2xx is a real failure.
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Array.Empty<Company>();
            }

            if (!response.IsSuccessStatusCode)
            {
                return Result.Unavailable($"CVR API responded with status {(int)response.StatusCode}.");
            }

            List<CvrCompanyResponse>? dtos;
            try
            {
                dtos = await response.Content.ReadFromJsonAsync<List<CvrCompanyResponse>>(JsonOptions, ct);
            }
            catch (JsonException ex)
            {
                return Result.Unexpected($"CVR API returned a response that could not be parsed: {ex.Message}");
            }

            if (dtos is null)
            {
                return Result.Unexpected("CVR API returned an unexpected response body.");
            }

            // A row that fails to map (e.g. an unparseable industry code) is dropped rather than
            // failing the whole search -- the anti-corruption layer stays defensive per-row
            // instead of letting one bad record take down an otherwise-useful result set. A
            // bankrupt company is dropped too (only "NORMAL" is confirmed live, so Unknown is
            // kept -- it covers unconfirmed statuses, not confirmed-inactive ones).
            var companies = new List<Company>(dtos.Count);
            foreach (var dto in dtos)
            {
                var mapped = MapToCompany(dto);
                if (mapped.IsSuccess && mapped.Value.Status != CompanyStatus.Bankrupt)
                {
                    companies.Add(mapped.Value);
                }
            }

            // Upstream ranking buries an exact match (e.g. "NOVO NORDISK A/S") under loosely
            // related results (fan clubs, staff associations) that merely contain the query
            // somewhere. Re-rank locally instead: exact match, then prefix match, then the rest,
            // all compared after the same normalization applied to the query itself. OrderBy is
            // a stable sort, so upstream's own ordering survives within each rank group.
            return companies.OrderBy(company => RelevanceRank(company.Name, upstreamQuery)).ToList();
        }
    }

    private static int RelevanceRank(string companyName, string normalizedQuery)
    {
        var normalizedName = NormalizeForUpstream(companyName);
        if (string.Equals(normalizedName, normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return normalizedName.StartsWith(normalizedQuery, StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    // apicvr.dk's search endpoint is path-based: an un-encoded "/" 404s, and a full legal name
    // like "LB Forsikring A/S" contains one via its own company-form suffix. Stripping a
    // trailing suffix (never a leading one -- a name that merely starts with "Aps" must not be
    // touched) covers the common case; any other "/" that's actually part of the name is
    // replaced with a space rather than left for the path encoder to turn into "%2F".
    private static string NormalizeForUpstream(string name)
    {
        // The "A/S" in "LB Forsikring A/S" will cause trouble in the URL - Remove it
        var normalized = CollapseWhitespace(name);
        normalized = StripTrailingCompanyForm(normalized);
        return CollapseWhitespace(normalized.Replace('/', ' '));
    }

    private static readonly string[] CompanyFormSuffixes =
    [
        "A.M.B.A.", "AMBA",
        "A/S",
        "ApS",
        "F.M.B.A.", "FMBA",
        "G/S",
        "I/S",
        "IVS",
        "K/S",
        "P/S",
        "S.M.B.A.", "SMBA",
        "V.M.B.A", "VMBA"
    ];

    private static string StripTrailingCompanyForm(string name)
    {
        foreach (var suffix in CompanyFormSuffixes)
        {
            if (TryStripSuffix(name, suffix, out var stripped))
            {
                return stripped;
            }

            // The suffix's own trailing period (if it doesn't already end in one) may also be
            // written out, e.g. "... A/S." -- try that form too.
            if (!suffix.EndsWith('.') && TryStripSuffix(name, suffix + ".", out stripped))
            {
                return stripped;
            }
        }

        return name;
    }

    // The suffix must be its own trailing token -- separated from the rest of the name by
    // whitespace or a comma -- not just a substring match against the tail, so a name that
    // merely *starts* with e.g. "Aps" is never touched.
    private static bool TryStripSuffix(string name, string candidate, out string stripped)
    {
        if (name.Length > candidate.Length &&
            name.EndsWith(candidate, StringComparison.OrdinalIgnoreCase) &&
            name[^(candidate.Length + 1)] is ' ' or ',')
        {
            stripped = name[..^candidate.Length].TrimEnd(' ', ',');
            return true;
        }

        stripped = name;
        return false;
    }

    private static string CollapseWhitespace(string value) => Regex.Replace(value, @"\s+", " ").Trim();

    private static Result<Company> MapToCompany(CvrCompanyResponse dto)
    {
        // vat is a JSON number, so a hypothetical leading zero in the CVR number would already
        // be lost upstream -- nothing on this side can recover it. PadLeft only guards against
        // a shorter-than-8-digit value reaching CvrNumber.TryCreate unpadded.
        var vatDigits = dto.Vat.ToString(CultureInfo.InvariantCulture).PadLeft(8, '0');

        var cvrResult = CvrNumber.TryCreate(vatDigits);
        if (cvrResult.IsFailure)
        {
            return Result.Unexpected($"CVR API returned an invalid CVR number: {dto.Vat}.");
        }

        var industryResult = IndustryCode.TryCreate(dto.IndustryCode);
        if (industryResult.IsFailure)
        {
            return Result.Unexpected($"CVR API returned an invalid industry code: '{dto.IndustryCode}'.");
        }

        var address = new Address(
            dto.Address ?? string.Empty,
            dto.Zipcode?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            dto.City ?? string.Empty);

        return new Company(
            cvrResult.Value,
            dto.Name ?? string.Empty,
            address,
            industryResult.Value,
            dto.IndustryDescription ?? string.Empty,
            dto.Employees,
            MapStatus(dto));
    }

    private static CompanyStatus MapStatus(CvrCompanyResponse dto)
    {
        if (dto.Bankrupt == true)
        {
            return CompanyStatus.Bankrupt;
        }

        // Only "NORMAL" has been observed live against apicvr.dk during planning. Other status
        // strings (e.g. for a ceased company) are unconfirmed, so they map to Unknown rather
        // than guessing a value that might not match the API's actual vocabulary.
        return dto.Status == "NORMAL" ? CompanyStatus.Active : CompanyStatus.Unknown;
    }
}
