using FirmaData.Api.Observability;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace FirmaData.Api.Errors;

// Unhandled: 500, correlation id in body, details logged not returned (plan section 5.2).
internal sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var correlationId = httpContext.GetCorrelationId();

        logger.LogError(
            exception,
            "Unhandled exception while processing {Method} {Path} ({CorrelationId})",
            httpContext.Request.Method,
            httpContext.Request.Path,
            correlationId);

        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "An unexpected error occurred.",
            Instance = httpContext.Request.Path,
        };
        problemDetails.Extensions["correlationId"] = correlationId;

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);

        return true;
    }
}
