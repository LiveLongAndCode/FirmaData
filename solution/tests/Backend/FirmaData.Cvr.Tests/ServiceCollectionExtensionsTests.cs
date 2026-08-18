using FirmaData.Application;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace FirmaData.Cvr.Tests;

public class ServiceCollectionExtensionsTests
{
    // AddStandardResilienceHandler().PipelineName is a pure function of the typed client's DI
    // name -- computing it on a throwaway registration is a reliable way to look up the same
    // named HttpStandardResilienceOptions the real pipeline (built via AddCvrClient +
    // AddCvrResiliencePipeline) is configured under.
    private static string ResolveCvrPipelineName() =>
        new ServiceCollection().AddHttpClient<CvrApiClient>().AddStandardResilienceHandler().PipelineName;


    [Fact]
    public void AddCvrClient_RegistersICompanyDirectory_ResolvingToTheCachingDecorator()
    {
        // The decorator wraps CvrApiClient (plan fase 7, F9b) -- the orchestrator only ever sees
        // the cached facade, never the raw typed client directly, mirroring FirmaData.Statbank's
        // AddStatbankClient/CachingIndustryStatisticsProvider pattern.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Cvr:BaseUrl"] = "https://apicvr.dk/" })
            .Build();

        var services = new ServiceCollection();
        services.AddCvrClient(configuration);
        using var provider = services.BuildServiceProvider();

        var directory = provider.GetRequiredService<ICompanyDirectory>();

        directory.Should().BeOfType<CachingCompanyDirectory>();
        provider.GetRequiredService<CvrApiClient>().Should().NotBeNull();
    }

    [Fact]
    public void AddCvrClient_WithEmptyBaseUrl_ThrowsOnOptionsAccess()
    {
        // [Required] rejects an empty string, unlike a missing key -- a missing key leaves
        // CvrOptions.BaseUrl at its non-null property-initializer default, which would pass
        // validation and defeat the point of this test.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Cvr:BaseUrl"] = "" })
            .Build();

        var services = new ServiceCollection();
        services.AddCvrClient(configuration);
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<CvrOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void AddCvrResiliencePipeline_WithConfiguredValues_AppliesThemToTheBuiltPipeline()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cvr:BaseUrl"] = "https://apicvr.dk/",
                ["Cvr:Resilience:TotalTimeoutSeconds"] = "42",
                ["Cvr:Resilience:AttemptTimeoutSeconds"] = "7",
                ["Cvr:Resilience:MaxRetryAttempts"] = "9",
                ["Cvr:Resilience:CircuitFailureRatio"] = "0.25",
                ["Cvr:Resilience:CircuitMinimumThroughput"] = "20",
                ["Cvr:Resilience:CircuitSamplingDurationSeconds"] = "60",
                ["Cvr:Resilience:CircuitBreakDurationSeconds"] = "90",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddCvrClient(configuration).AddCvrResiliencePipeline(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>().Get(ResolveCvrPipelineName());

        options.TotalRequestTimeout.Timeout.Should().Be(TimeSpan.FromSeconds(42));
        options.AttemptTimeout.Timeout.Should().Be(TimeSpan.FromSeconds(7));
        options.Retry.MaxRetryAttempts.Should().Be(9);
        options.CircuitBreaker.FailureRatio.Should().Be(0.25);
        options.CircuitBreaker.MinimumThroughput.Should().Be(20);
        options.CircuitBreaker.SamplingDuration.Should().Be(TimeSpan.FromSeconds(60));
        options.CircuitBreaker.BreakDuration.Should().Be(TimeSpan.FromSeconds(90));
    }

    [Fact]
    public void AddCvrResiliencePipeline_WithEmptyConfiguration_KeepsThePreviousHardcodedDefaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Cvr:BaseUrl"] = "https://apicvr.dk/" })
            .Build();

        var services = new ServiceCollection();
        services.AddCvrClient(configuration).AddCvrResiliencePipeline(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>().Get(ResolveCvrPipelineName());

        options.TotalRequestTimeout.Timeout.Should().Be(TimeSpan.FromSeconds(15));
        options.AttemptTimeout.Timeout.Should().Be(TimeSpan.FromSeconds(5));
        options.Retry.MaxRetryAttempts.Should().Be(3);
        options.CircuitBreaker.FailureRatio.Should().Be(0.5);
        options.CircuitBreaker.MinimumThroughput.Should().Be(10);
        options.CircuitBreaker.SamplingDuration.Should().Be(TimeSpan.FromSeconds(30));
        options.CircuitBreaker.BreakDuration.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void AddCvrClient_WithConfiguredHealthCheckTimeout_BindsResilienceOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cvr:BaseUrl"] = "https://apicvr.dk/",
                ["Cvr:Resilience:HealthCheckTimeoutSeconds"] = "9",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddCvrClient(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<ResilienceOptions>>().Value;

        options.HealthCheckTimeoutSeconds.Should().Be(9);
    }
}
