using FirmaData.Api.Errors;
using FirmaData.Api.HealthChecks;
using FirmaData.Api.Observability;
using FirmaData.Application;
using FirmaData.Cvr;
using FirmaData.Statbank;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using Serilog;

namespace FirmaData.Api;

// Not `static`: WebApplicationFactory<Program> (FirmaData.Api.IntegrationTests) needs Program
// as an ordinary reference type to use as a generic type argument -- a static class cannot be
// used as one at all (CS0718), regardless of the top-level-statements question in section 2.2,
// which only cares about how Main itself is written.
public class Program
{
    // Explicit bucket boundaries for a meaningful p95 (plan section 7.2) -- the OTel SDK's
    // default buckets are too coarse at the low end for sub-second HTTP calls.
    private static readonly double[] LatencyBucketBoundaries =
        [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Host.UseSerilog((context, configuration) => configuration
            .ReadFrom.Configuration(context.Configuration)
            .Enrich.FromLogContext()
            .WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter()));

        // Add services to the container.
        builder.Services.AddControllers();
        builder.Services.AddOpenApi();

        // The dependency-metrics handler is added before the resilience pipeline on each typed
        // client, so it wraps the whole pipeline rather than being wrapped by it (see the two
        // ServiceCollectionExtensions for why that ordering matters for outcome=circuit_open).
        builder.Services.AddCvrClient(builder.Configuration)
            .AddDependencyMetrics("cvr")
            .AddCvrResiliencePipeline();
        builder.Services.AddStatbankClient(builder.Configuration)
            .AddDependencyMetrics("statbank")
            .AddStatbankResiliencePipeline();

        builder.Services
            .AddOptions<SearchOptions>()
            .Bind(builder.Configuration.GetSection(SearchOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Factory registration, not AddScoped<ICompanyEnrichmentService, CompanyEnrichmentService>
        // directly: FirmaData.Application must not take a package reference on Options, so this
        // is where SearchOptions.MaxConcurrentStatisticsCalls is read and handed down as a plain
        // int. Read from IOptions<SearchOptions> here (not IOptionsSnapshot/Monitor) rather than
        // capturing a value at registration time, so the composition root -- not the constructor
        // parameter's default -- is the single place that owns the actual value.
        builder.Services.AddScoped<ICompanyEnrichmentService>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<SearchOptions>>().Value;
            return new CompanyEnrichmentService(
                provider.GetRequiredService<ICompanyDirectory>(),
                provider.GetRequiredService<IIndustryStatisticsProvider>(),
                options.MaxConcurrentStatisticsCalls);
        });

        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

        builder.Services
            .AddHealthChecks()
            .AddCheck<CvrApiHealthCheck>("cvr", tags: ["ready"])
            .AddCheck<StatbankApiHealthCheck>("statbank", tags: ["ready"]);

        builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddMeter("FirmaData")
            .AddView("firmadata.dependency.duration", new ExplicitBucketHistogramConfiguration { Boundaries = LatencyBucketBoundaries })
            .AddView("firmadata.enrichment.duration", new ExplicitBucketHistogramConfiguration { Boundaries = LatencyBucketBoundaries })
            .AddView("http.server.request.duration", new ExplicitBucketHistogramConfiguration { Boundaries = LatencyBucketBoundaries })
            .AddView("http.client.request.duration", new ExplicitBucketHistogramConfiguration { Boundaries = LatencyBucketBoundaries })
            .AddPrometheusExporter());

        var app = builder.Build();

        // First, so every log line and error response for this request carries a correlation id
        // (plan section 7.1), even one written or thrown before any other middleware runs.
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseSerilogRequestLogging();

        app.UseExceptionHandler();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "FirmaData API v1"));
        }

        app.UseHttpsRedirection();

        app.UseAuthorization();

        app.MapControllers();

        // Liveness: process is up, no dependency calls. Readiness: cheap dependency probes --
        // Degraded (not Unhealthy) reports as 200, so an orchestrator doesn't depool the service
        // over the enrichment source alone (plan section 7.4).
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

        app.MapPrometheusScrapingEndpoint("/metrics");

        app.Run();
    }
}
