using FirmaData.Web.Mapping;
using FirmaData.Web.Models;
using FirmaData.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace FirmaData.Web.Controllers;

// Resultater and Virksomhed (plan section 15, screens 2 and 3).
public sealed class CompaniesController(IFirmaDataApiClient apiClient) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Details(string cvrNumber, int? year, CancellationToken ct)
    {
        var resolvedYear = await ResolveYearAsync(year, ct);
        var result = await apiClient.GetByCvrAsync(cvrNumber, resolvedYear, ct);

        switch (result.Outcome)
        {
            case CompanyLookupOutcome.NotFound:
                TempData["SearchError"] = $"Der blev ikke fundet en virksomhed med CVR-nummer {cvrNumber}.";
                return RedirectToAction("Index", "Home");
            case CompanyLookupOutcome.Invalid:
                TempData["SearchError"] = "Det indtastede CVR-nummer er ugyldigt (forkert kontrolciffer).";
                return RedirectToAction("Index", "Home");
            default:
                return View(result.Company!.ToDetailViewModel(resolvedYear));
        }
    }

    [HttpGet]
    public async Task<IActionResult> Results(string name, int? year, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return RedirectToAction("Index", "Home");
        }

        var resolvedYear = await ResolveYearAsync(year, ct);
        var matches = await apiClient.SearchByNameAsync(name, resolvedYear, ct);

        return View(new SearchResultsViewModel
        {
            Query = name,
            Year = resolvedYear,
            Companies = matches.Select(match => match.ToSummaryViewModel()).ToList(),
        });
    }

    // A direct link (bookmarked/typed URL) may omit ?year=; the search form always supplies one
    // via its dropdown's default, so this fallback mainly exists for that edge case.
    private async Task<int> ResolveYearAsync(int? year, CancellationToken ct) =>
        year ?? (await apiClient.GetAvailableYearsAsync(ct)).DefaultYear;
}
