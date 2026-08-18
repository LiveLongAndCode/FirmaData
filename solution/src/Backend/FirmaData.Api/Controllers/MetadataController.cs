using FirmaData.Api.Errors;
using FirmaData.Application;
using FirmaData.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace FirmaData.Api.Controllers;

[ApiController]
[Route("api/v1/metadata")]
public sealed class MetadataController(
    IIndustryStatisticsProvider statisticsProvider, [FromKeyedServices(AppTimeProvider.ServiceKey)] TimeProvider timeProvider)
    : ControllerBase
{
    // Drives the UI's year dropdown and the API's own default year (plan section 4.2/5.1).
    [HttpGet("years")]
    [ProducesResponseType<AvailableYearsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetAvailableYears(CancellationToken ct)
    {
        var result = await statisticsProvider.GetAvailableYearsAsync(ct);
        if (result.IsFailure)
        {
            return result.Error.ToProblem(HttpContext);
        }

        var years = result.Value.OrderBy(year => year).ToList();
        var defaultYear = years.Count > 0 ? years[^1] : timeProvider.GetUtcNow().Year;

        return Ok(new AvailableYearsResponse(years, defaultYear));
    }
}
