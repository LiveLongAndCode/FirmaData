using FirmaData.Application;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
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

        services
            .AddOptions<ResilienceOptions>()
            .Bind(configuration.GetSection($"{CvrOptions.SectionName}:{ResilienceOptions.SectionName}"))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // AddMemoryCache uses TryAdd semantics, so this is a harmless no-op if the app's
        // composition root (or another adapter, e.g. FirmaData.Statbank) already registered the
        // shared IMemoryCache -- it just guarantees one exists regardless of registration order.
        services.AddMemoryCache();

        // Resilience is deliberately not chained on here -- FirmaData.Api (the composition root)
        // adds its dependency-metrics handler first, then this pipeline, so the metrics handler
        // wraps the whole resilience pipeline (section 7.2's outcome=circuit_open/timeout only
        // make sense observed from outside it) rather than being wrapped by it.
        //
        // Registered as the concrete CvrApiClient, not directly as ICompanyDirectory (plan fase
        // 7, F9b) -- CachingCompanyDirectory below needs to wrap it, which a direct
        // AddHttpClient<ICompanyDirectory, CvrApiClient> registration can't be decorated after.
        var httpClientBuilder = services.AddHttpClient<CvrApiClient>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<CvrOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);

            // The resilience pipeline (added below) owns the request budget end to end; without
            // this, HttpClient's own default 100s timeout would compete with Polly's much
            // tighter one.
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        // Short TTL (10 min, not Statbank's 24h): master data changes, annual statistics don't.
        // Only GetByCvrAsync is cached -- SearchByNameAsync has high cardinality, low reuse, and
        // a cache key built from a free-text search would carry PII.
        services.AddTransient<ICompanyDirectory>(provider =>
            new CachingCompanyDirectory(provider.GetRequiredService<CvrApiClient>(), provider.GetRequiredService<IMemoryCache>()));

        return httpClientBuilder;
    }

    // Per-dependency Polly pipeline (plan section 6.1): total timeout 15s wraps a circuit
    // breaker (opens after >=10 requests with a >=50% failure ratio in a 30s window, breaks for
    // 30s) wrapping 3 retries (exponential backoff + jitter -- the standard handler's defaults)
    // wrapping a 5s per-attempt timeout. The standard handler's default ShouldHandle predicate
    // already excludes 400/404 from "transient", so a deterministic client error is never
    // retried; apicvr.dk's 200-with-NOT_FOUND body never reaches Polly at all, since it's a
    // successful HTTP response by the time CvrApiClient inspects the payload (section 4.1).
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
    public static IHttpClientBuilder AddCvrResiliencePipeline(this IHttpClientBuilder builder, IConfiguration configuration)
    {
        // Forces CircuitStateMetrics' static constructor (and so its ObservableGauge
        // registration) to run at startup -- otherwise, in a healthy process where the circuit
        // never actually transitions, the type is never touched and the gauge never appears on
        // /metrics at all. The circuit does start Closed, so this is also just correct.
        CircuitStateMetrics.RecordClosed();

        var config = configuration.GetSection($"{CvrOptions.SectionName}:{ResilienceOptions.SectionName}").Get<ResilienceOptions>()
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
