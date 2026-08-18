namespace FirmaData.Web.Services;

// Propagates the current Web request's TraceIdentifier as the correlation id on every call to
// the Firmadata API (plan section 7.1: "a correlation id that flows Web -> Api -> both
// dependencies"). FirmaData.Api's CorrelationIdMiddleware honours an inbound X-Correlation-Id
// header instead of always minting its own, so this is what makes one id show up in both
// processes' logs -- and it's the same id HomeController.Error() shows the user, since that's
// just this request's own TraceIdentifier.
internal sealed class CorrelationIdHandler(IHttpContextAccessor httpContextAccessor) : DelegatingHandler
{
    public const string HeaderName = "X-Correlation-Id";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var correlationId = httpContextAccessor.HttpContext?.TraceIdentifier;
        if (!string.IsNullOrEmpty(correlationId))
        {
            request.Headers.TryAddWithoutValidation(HeaderName, correlationId);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
