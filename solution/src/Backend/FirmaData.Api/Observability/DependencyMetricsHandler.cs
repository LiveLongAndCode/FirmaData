using System.Diagnostics;
using System.Diagnostics.Metrics;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace FirmaData.Api.Observability;

// firmadata.dependency.duration / .requests (plan section 7.2) -- one implementation, attached
// to both the CVR and Statbank typed clients from Program.cs, no duplication. Registered OUTSIDE
// the resilience pipeline (see FirmaData.Cvr/FirmaData.Statbank's ServiceCollectionExtensions),
// so it observes the final outcome of the whole dependency call -- including a rejection from an
// open circuit breaker or the pipeline's own timeout -- rather than one data point per retry
// attempt.
internal sealed class DependencyMetricsHandler(string dependency) : DelegatingHandler
{
    private static readonly Meter Meter = new("FirmaData");
    private static readonly Histogram<double> DurationHistogram = Meter.CreateHistogram<double>("firmadata.dependency.duration", "s");
    private static readonly Counter<long> RequestCounter = Meter.CreateCounter<long>("firmadata.dependency.requests");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var operation = ClassifyOperation(request);
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage? response = null;
        Exception? exception = null;

        try
        {
            response = await base.SendAsync(request, cancellationToken);
            return response;
        }
        catch (Exception ex)
        {
            exception = ex;
            throw;
        }
        finally
        {
            var outcome = ClassifyOutcome(response, exception);
            var tags = new TagList
            {
                { "dependency", dependency },
                { "operation", operation },
                { "outcome", outcome },
            };
            DurationHistogram.Record(stopwatch.Elapsed.TotalSeconds, tags);
            RequestCounter.Add(1, tags);
        }
    }

    // Deliberately coarse and low-cardinality -- never the raw path (which embeds a CVR number
    // or search term for the CVR client).
    private static string ClassifyOperation(HttpRequestMessage request)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;

        if (path.Contains("/search/", StringComparison.OrdinalIgnoreCase))
        {
            return "search";
        }

        if (path.Contains("/tableinfo", StringComparison.OrdinalIgnoreCase))
        {
            return "years";
        }

        if (path.Contains("/data", StringComparison.OrdinalIgnoreCase))
        {
            return "statistics";
        }

        return "lookup";
    }

    // "Not found" is a valid answer, not a fault (plan section 7.2) -- apicvr.dk's
    // 200-with-NOT_FOUND body is still HTTP 200 at this layer, so it's classified as success
    // here without any special-casing; the domain-level NotFound mapping happens one layer up,
    // in CvrApiClient.
    private static string ClassifyOutcome(HttpResponseMessage? response, Exception? exception) => exception switch
    {
        BrokenCircuitException => "circuit_open",
        TimeoutRejectedException => "timeout",
        not null => "server_error",
        null => (int)response!.StatusCode switch
        {
            >= 200 and < 400 => "success",
            >= 400 and < 500 => "client_error",
            _ => "server_error",
        },
    };
}
