using FirmaData.Application;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace FirmaData.Statbank;

public static class ServiceCollectionExtensions
{
    // Bounds the statistics cache so it cannot become a leak (plan section 6.2). Each entry
    // costs Size = 1 (CachingIndustryStatisticsProvider), so this is a count, not a byte budget.
    private const int CacheSizeLimit = 10_000;

    public static IHttpClientBuilder AddStatbankClient(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<StatbankOptions>()
            .Bind(configuration.GetSection(StatbankOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services
            .AddOptions<ResilienceOptions>()
            .Bind(configuration.GetSection($"{StatbankOptions.SectionName}:{ResilienceOptions.SectionName}"))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddMemoryCache(options => options.SizeLimit = CacheSizeLimit);

        // Resilience is deliberately not chained on here -- FirmaData.Api (the composition root)
        // adds its dependency-metrics handler first, then this pipeline, so the metrics handler
        // wraps the whole resilience pipeline (section 7.2's outcome=circuit_open/timeout only
        // make sense observed from outside it) rather than being wrapped by it.
        var httpClientBuilder = services.AddHttpClient<StatbankClient>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<StatbankOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);

            // The resilience pipeline (added below) owns the request budget end to end; without
            // this, HttpClient's own default 100s timeout would compete with Polly's much
            // tighter one.
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        // The caching decorator (section 6.2) wraps the typed client registered above -- a
        // factory, not a plain AddTransient<TService, TImplementation>, because the decorator's
        // constructor takes the IIndustryStatisticsProvider abstraction (so it stays testable
        // with a fake), while only the concrete StatbankClient is registered as itself.
        services.AddTransient<IIndustryStatisticsProvider>(provider =>
            new CachingIndustryStatisticsProvider(provider.GetRequiredService<StatbankClient>(), provider.GetRequiredService<IMemoryCache>()));

        return httpClientBuilder;
    }

    // Per-dependency Polly pipeline (plan section 6.1) -- identical shape to FirmaData.Cvr's:
    // total timeout 15s, circuit breaker (>=10 requests, >=50% failure ratio in a 30s window,
    // 30s break), 3 retries (exponential backoff + jitter), 5s per-attempt timeout. The standard
    // handler's default ShouldHandle predicate excludes 400/404 from "transient" -- an
    // unavailable year (EXTRACT-NOTFOUND, mapped to NotFound in StatbankClient) is never
    // retried.
    //
    // Values now come from ResilienceOptions (plan fase 7, F8) instead of being hardcoded here;
    // defaults are unchanged, so behaviour with no configuration is identical to before.
    //
    // Bound eagerly from IConfiguration here, not via a DI-resolved IOptions<ResilienceOptions>
    // inside AddStandardResilienceHandler's lazy Configure callback -- configuration is read
    // directly here instead, the same IConfiguration instance builder.Configuration already is by
    // the time this method runs, so the integration tests' host overrides (added before
    // Program.Main's builder.Build()) are already present -- this is a one-time eager read of
    // static appsettings, not a value that needs to change at runtime. (While diagnosing this, a
    // DI-resolved IOptions<ResilienceOptions> approach was also tried and reproducibly hung every
    // retried request for ~100s -- that turned out to be an unrelated bug, an unkeyed
    // FakeTimeProvider registration that Polly's own retry delays picked up and froze against;
    // see AppTimeProvider. Both approaches work correctly now; this one was kept for simplicity.)
    public static IHttpClientBuilder AddStatbankResiliencePipeline(this IHttpClientBuilder builder, IConfiguration configuration)
    {
        // Forces CircuitStateMetrics' static constructor (and so its ObservableGauge
        // registration) to run at startup -- otherwise, in a healthy process where the circuit
        // never actually transitions, the type is never touched and the gauge never appears on
        // /metrics at all. The circuit does start Closed, so this is also just correct.
        CircuitStateMetrics.RecordClosed();

        var config = configuration.GetSection($"{StatbankOptions.SectionName}:{ResilienceOptions.SectionName}").Get<ResilienceOptions>()
            ?? new ResilienceOptions();

        builder.AddStandardResilienceHandler(options =>
        {
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(config.TotalTimeoutSeconds);
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(config.AttemptTimeoutSeconds);
            options.Retry.MaxRetryAttempts = config.MaxRetryAttempts;
            options.CircuitBreaker.FailureRatio = config.CircuitFailureRatio;
            options.CircuitBreaker.MinimumThroughput = config.CircuitMinimumThroughput;
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(config.CircuitSamplingDurationSeconds);
            options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(config.CircuitBreakDurationSeconds);

            // firmadata.circuit.state (section 7.2), dependency="statbank".
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
