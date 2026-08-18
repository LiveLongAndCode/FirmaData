using FirmaData.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FirmaData.Cvr;

public static class ServiceCollectionExtensions
{
    public static IHttpClientBuilder AddCvrClient(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<CvrOptions>()
            .Bind(configuration.GetSection(CvrOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Resilience is deliberately not chained on here -- FirmaData.Api (the composition root)
        // adds its dependency-metrics handler first, then this pipeline, so the metrics handler
        // wraps the whole resilience pipeline (section 7.2's outcome=circuit_open/timeout only
        // make sense observed from outside it) rather than being wrapped by it.
        return services.AddHttpClient<ICompanyDirectory, CvrApiClient>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<CvrOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
        });
    }

    // Per-dependency Polly pipeline (plan section 6.1): total timeout 15s wraps a circuit
    // breaker (opens after >=10 requests with a >=50% failure ratio in a 30s window, breaks for
    // 30s) wrapping 3 retries (exponential backoff + jitter -- the standard handler's defaults)
    // wrapping a 5s per-attempt timeout. The standard handler's default ShouldHandle predicate
    // already excludes 400/404 from "transient", so a deterministic client error is never
    // retried; apicvr.dk's 200-with-NOT_FOUND body never reaches Polly at all, since it's a
    // successful HTTP response by the time CvrApiClient inspects the payload (section 4.1).
    public static IHttpClientBuilder AddCvrResiliencePipeline(this IHttpClientBuilder builder)
    {
        // Forces CircuitStateMetrics' static constructor (and so its ObservableGauge
        // registration) to run at startup -- otherwise, in a healthy process where the circuit
        // never actually transitions, the type is never touched and the gauge never appears on
        // /metrics at all. The circuit does start Closed, so this is also just correct.
        CircuitStateMetrics.RecordClosed();

        builder.AddStandardResilienceHandler(options =>
        {
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(15);
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(5);
            options.Retry.MaxRetryAttempts = 3;
            options.CircuitBreaker.FailureRatio = 0.5;
            options.CircuitBreaker.MinimumThroughput = 10;
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
            options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);

            // firmadata.circuit.state (section 7.2), dependency="cvr".
            options.CircuitBreaker.OnOpened = _ =>
            {
                CircuitStateMetrics.RecordOpened();
                return ValueTask.CompletedTask;
            };
            options.CircuitBreaker.OnClosed = _ =>
            {
                CircuitStateMetrics.RecordClosed();
                return ValueTask.CompletedTask;
            };
            options.CircuitBreaker.OnHalfOpened = _ =>
            {
                CircuitStateMetrics.RecordHalfOpened();
                return ValueTask.CompletedTask;
            };
        });

        return builder;
    }
}
