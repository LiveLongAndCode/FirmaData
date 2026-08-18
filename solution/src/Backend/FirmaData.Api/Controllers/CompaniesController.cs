using FirmaData.Api.Errors;
using FirmaData.Api.Mapping;
using FirmaData.Application;
using FirmaData.Domain;
using Microsoft.AspNetCore.Mvc;

namespace FirmaData.Api.Controllers;

// R1, R2: look up a company by CVR number or name, enriched with industry statistics in one
// response (plan section 5.1).
[ApiController]
[Route("api/v1/companies")]
public sealed class CompaniesController(ICompanyEnrichmentService enrichmentService) : ControllerBase
{
    private const int MaxSearchResults = 10;

    [HttpGet("{cvrNumber}")]
    public async Task<IActionResult> GetByCvr(string cvrNumber, [FromQuery] int? year, CancellationToken ct)
    {
        var cvrResult = CvrNumber.TryCreate(cvrNumber);
        if (cvrResult.IsFailure)
        {
            return cvrResult.Error.ToProblem(HttpContext);
        }

        if (!TryParseYear(year, out var parsedYear, out var yearError))
        {
            return yearError.ToProblem(HttpContext);
        }

        var enriched = await enrichmentService.EnrichByCvrAsync(cvrResult.Value, parsedYear, ct);
        if (enriched.IsFailure)
        {
            return enriched.Error.ToProblem(HttpContext);
        }

        ApplyDegradedSourceHeader(enriched.Value.StatisticsStatus);

        return Ok(enriched.Value.ToResponse());
    }

    [HttpGet]
    public async Task<IActionResult> SearchByName([FromQuery] string? name, [FromQuery] int? year, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result.Validation("The 'name' query parameter is required.").ToProblem(HttpContext);
        }

        if (!TryParseYear(year, out var parsedYear, out var yearError))
        {
            return yearError.ToProblem(HttpContext);
        }

        var enriched = await enrichmentService.SearchAndEnrichAsync(name, parsedYear, ct);
        if (enriched.IsFailure)
        {
            return enriched.Error.ToProblem(HttpContext);
        }

        var results = enriched.Value.Take(MaxSearchResults).ToList();
        if (results.Any(company => company.StatisticsStatus == EnrichmentStatus.SourceUnavailable))
        {
            ApplyDegradedSourceHeader(EnrichmentStatus.SourceUnavailable);
        }

        return Ok(results.Select(company => company.ToResponse()));
    }

    private static bool TryParseYear(int? year, out StatisticsYear? parsedYear, out ResultError error)
    {
        if (year is null)
        {
            parsedYear = null;
            error = null!;
            return true;
        }

        var result = StatisticsYear.TryCreate(year.Value);
        if (result.IsFailure)
        {
            parsedYear = null;
            error = result.Error;
            return false;
        }

        parsedYear = result.Value;
        error = null!;
        return true;
    }

    private void ApplyDegradedSourceHeader(EnrichmentStatus status)
    {
        if (status == EnrichmentStatus.SourceUnavailable)
        {
            // Not the standard HTTP `Warning` header: RFC 9111 removed it from the spec, so this
            // is a plain custom header instead. The JSON body's StatisticsStatus is authoritative;
            // this is a cheap secondary signal for clients that don't want to parse the body.
            Response.Headers["FirmaData-Degraded-Source"] = "statbank";
        }
    }
}
