using FirmaData.Web.Models;
using FirmaData.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace FirmaData.Web.Controllers;

// Søg (plan section 15, screen 1): one input, auto-detected as a CVR number (8 digits) or a
// name, plus a year dropdown. A submitted search redirects to CompaniesController (PRG) rather
// than rendering inline -- Resultater and Virksomhed are their own screens/URLs.
public sealed class HomeController(IFirmaDataApiClient apiClient) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var model = await BuildSearchViewModelAsync(ct);
        model.SearchError = TempData["SearchError"] as string;
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(SearchViewModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            var reloaded = await BuildSearchViewModelAsync(ct);
            reloaded.Query = model.Query;
            reloaded.Year = model.Year;
            return View(reloaded);
        }

        var query = model.Query!.Trim();

        if (IsCvrNumberShaped(query))
        {
            return RedirectToAction("Details", "Companies", new { cvrNumber = query, year = model.Year });
        }

        return RedirectToAction("Results", "Companies", new { name = query, year = model.Year });
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        // The same id sent as X-Correlation-Id on the API call that failed (CorrelationIdHandler
        // forwards this exact TraceIdentifier), so it's enough on its own to find the request in
        // both processes' logs (plan section 15).
        return View(new ErrorViewModel { RequestId = HttpContext.TraceIdentifier });
    }

    private static bool IsCvrNumberShaped(string query) => query.Length == 8 && query.All(char.IsAsciiDigit);

    private async Task<SearchViewModel> BuildSearchViewModelAsync(CancellationToken ct)
    {
        var years = await apiClient.GetAvailableYearsAsync(ct);
        return new SearchViewModel
        {
            Year = years.DefaultYear,
            AvailableYears = years.Years,
        };
    }
}
