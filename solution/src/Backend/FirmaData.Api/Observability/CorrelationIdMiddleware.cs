using Serilog.Context;

namespace FirmaData.Api.Observability;

// A correlation id that appears in every log line and error response (plan section 7.1).
// Accepts one from an upstream caller (FirmaData.Web, Phase 8) or generates one; either way it's
// echoed back on the response so the caller can correlate its own logs, and pushed onto
// Serilog's LogContext so every log line written while handling this request carries it.
internal sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var existing) && !string.IsNullOrWhiteSpace(existing)
            ? existing.ToString()
            : Guid.NewGuid().ToString("n");

        context.Items[HeaderName] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }
}

internal static class HttpContextCorrelationIdExtensions
{
    // Falls back to TraceIdentifier only for requests the middleware never saw (there aren't
    // any on the real pipeline, but a unit test might construct a bare HttpContext directly).
    public static string GetCorrelationId(this HttpContext context) =>
        context.Items[CorrelationIdMiddleware.HeaderName] as string ?? context.TraceIdentifier;
}
