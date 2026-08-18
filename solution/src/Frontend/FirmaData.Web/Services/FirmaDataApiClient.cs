using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FirmaData.Contracts;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace FirmaData.Web.Services;

// The typed client over FirmaData.Api (plan section 15). Mirrors the backend adapters' own
// convention for which exceptions mean "the dependency is unavailable" (FirmaData.Cvr.CvrApiClient),
// except there is no Result<T> to return into here -- FirmaData.Web deliberately depends on
// Contracts only, not Domain (plan section 2.1). Unavailability is reported by throwing instead;
// Program.cs's exception handler turns that into the Danish error page.
public sealed class FirmaDataApiClient(HttpClient httpClient) : IFirmaDataApiClient
{
    public async Task<AvailableYearsResponse> GetAvailableYearsAsync(CancellationToken ct)
    {
        using var response = await SendAsync("api/v1/metadata/years", ct);
        return await ReadAsync<AvailableYearsResponse>(response, ct);
    }

    public async Task<CompanyLookupResult> GetByCvrAsync(string cvrNumber, int year, CancellationToken ct)
    {
        var requestUri = $"api/v1/companies/{Uri.EscapeDataString(cvrNumber)}?year={year}";
        using var response = await SendAsync(requestUri, ct, HttpStatusCode.NotFound, HttpStatusCode.BadRequest);

        return response.StatusCode switch
        {
            HttpStatusCode.NotFound => new CompanyLookupResult(CompanyLookupOutcome.NotFound),
            HttpStatusCode.BadRequest => new CompanyLookupResult(CompanyLookupOutcome.Invalid),
            _ => new CompanyLookupResult(CompanyLookupOutcome.Found, await ReadAsync<EnrichedCompanyResponse>(response, ct)),
        };
    }

    public async Task<IReadOnlyList<EnrichedCompanyResponse>> SearchByNameAsync(string name, int year, CancellationToken ct)
    {
        var requestUri = $"api/v1/companies?name={Uri.EscapeDataString(name)}&year={year}";
        using var response = await SendAsync(requestUri, ct);
        return await ReadAsync<IReadOnlyList<EnrichedCompanyResponse>>(response, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(string requestUri, CancellationToken ct, params HttpStatusCode[] alsoAccept)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient.GetAsync(requestUri, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException or BrokenCircuitException)
        {
            // A network failure, the typed client's own resilience pipeline timing out, or an
            // open circuit breaker (ServiceCollectionExtensions.AddFirmaDataApiClient) -- all
            // genuine unavailability.
            throw new FirmaDataApiUnavailableException("The Firmadata API is unavailable.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // The HttpClient's own request timed out without going through the resilience
            // pipeline's TimeoutRejectedException (e.g. socket-level hang before a response
            // starts). ct.IsCancellationRequested distinguishes this from the caller itself
            // cancelling the request, which should propagate as-is, not be reported as
            // unavailability.
            throw new FirmaDataApiUnavailableException("The Firmadata API did not respond in time.", ex);
        }

        if (response.IsSuccessStatusCode || alsoAccept.Contains(response.StatusCode))
        {
            return response;
        }

        var statusCode = response.StatusCode;
        response.Dispose();
        throw new FirmaDataApiUnavailableException($"The Firmadata API responded with status {(int)statusCode}.");
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        T? value;
        try
        {
            value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            throw new FirmaDataApiUnavailableException("The Firmadata API returned a response that could not be parsed.", ex);
        }

        return value ?? throw new FirmaDataApiUnavailableException("The Firmadata API returned an empty response body.");
    }
}
