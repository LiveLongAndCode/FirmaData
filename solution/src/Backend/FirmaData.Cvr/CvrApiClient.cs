using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
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
        var encodedName = Uri.EscapeDataString(name);

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
            // instead of letting one bad record take down an otherwise-useful result set.
            var companies = new List<Company>(dtos.Count);
            foreach (var dto in dtos)
            {
                var mapped = MapToCompany(dto);
                if (mapped.IsSuccess)
                {
                    companies.Add(mapped.Value);
                }
            }

            return companies;
        }
    }

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
