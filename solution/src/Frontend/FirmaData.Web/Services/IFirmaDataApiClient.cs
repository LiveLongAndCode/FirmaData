using FirmaData.Contracts;

namespace FirmaData.Web.Services;

public interface IFirmaDataApiClient
{
    Task<AvailableYearsResponse> GetAvailableYearsAsync(CancellationToken ct);

    Task<CompanyLookupResult> GetByCvrAsync(string cvrNumber, int year, CancellationToken ct);

    Task<IReadOnlyList<EnrichedCompanyResponse>> SearchByNameAsync(string name, int year, CancellationToken ct);
}

public enum CompanyLookupOutcome
{
    Found,
    NotFound,
    Invalid,
}

// Invalid covers the API's 400 response: a query string that is 8 digits (how the Web UI
// detects "this is a CVR number" in the first place) but fails the modulus-11 checksum
// (FirmaData.Domain.CvrNumber). Anything the API can't otherwise answer -- unreachable, 5xx,
// timeout -- is unavailability, not an outcome, so it's thrown as FirmaDataApiUnavailableException
// instead of represented here.
public sealed record CompanyLookupResult(CompanyLookupOutcome Outcome, EnrichedCompanyResponse? Company = null);
