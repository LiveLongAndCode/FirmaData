using System.Globalization;
using FirmaData.Api.Observability;
using FirmaData.Domain;
using Microsoft.AspNetCore.Mvc;

namespace FirmaData.Api.Errors;

// Maps a ResultError onto the status-code table from plan section 5.2 / 3.1: Validation -> 400,
// NotFound -> 404, Unavailable -> 503 (+ Retry-After, R10), anything else -> 500.
internal static class ResultErrorMapping
{
    private const int RetryAfterSeconds = 30;

    public static ObjectResult ToProblem(this ResultError error, HttpContext httpContext)
    {
        var statusCode = error.Type switch
        {
            ResultErrorType.Validation => StatusCodes.Status400BadRequest,
            ResultErrorType.NotFound => StatusCodes.Status404NotFound,
            ResultErrorType.Unavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError,
        };

        if (error.Type == ResultErrorType.Unavailable)
        {
            httpContext.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        }

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = error.Type.ToString(),
            Detail = error.Message,
            Instance = httpContext.Request.Path,
        };
        problemDetails.Extensions["correlationId"] = httpContext.GetCorrelationId();

        return new ObjectResult(problemDetails) { StatusCode = statusCode };
    }
}
