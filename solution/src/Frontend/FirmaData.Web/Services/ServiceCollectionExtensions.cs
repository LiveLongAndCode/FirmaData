using Microsoft.Extensions.Options;

namespace FirmaData.Web.Services;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFirmaDataApiClient(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<ApiOptions>()
            .Bind(configuration.GetSection(ApiOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpContextAccessor();
        services.AddTransient<CorrelationIdHandler>();

        services.AddHttpClient<IFirmaDataApiClient, FirmaDataApiClient>((provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<ApiOptions>>().Value;
                client.BaseAddress = new Uri(options.BaseUrl);
            })
            .AddHttpMessageHandler<CorrelationIdHandler>()
            .AddStandardResilienceHandler(options =>
            {
                // Still its own retry per plan section 15, but no longer shorter than
                // FirmaData.Api's own per-dependency pipeline (15s total / 5s attempt / 3
                // retries, plan section 6.1): a name search fans out to Statbank once per
                // *distinct* industry code among the matches (CompanyEnrichmentService,
                // concurrently, but still bounded by the slowest one), and on a cold cache plus
                // first-request JIT/TLS warmup that legitimately took longer than the original
                // 4s/10s budget for a broad term like "forsikring" (~30 distinct codes) --
                // timing out here mid-search doesn't mean Api is unhealthy, it means the budget
                // was tuned for a single-CVR lookup only. Api's own resilience pipeline already
                // guards the two real dependencies; this is just "don't give up on our own API
                // before it's had a fair chance to answer."
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(20);
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
                options.Retry.MaxRetryAttempts = 2;
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
                options.CircuitBreaker.MinimumThroughput = 6;
                options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(15);
            });

        return services;
    }
}
