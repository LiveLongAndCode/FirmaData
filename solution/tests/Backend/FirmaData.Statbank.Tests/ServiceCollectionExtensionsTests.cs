using FirmaData.Application;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace FirmaData.Statbank.Tests;

public class ServiceCollectionExtensionsTests
{
    // AddStandardResilienceHandler().PipelineName is a pure function of the typed client's DI
    // name -- computing it on a throwaway registration is a reliable way to look up the same
    // named HttpStandardResilienceOptions the real pipeline (built via AddStatbankClient +
    // AddStatbankResiliencePipeline) is configured under.
    private static string ResolveStatbankPipelineName() =>
        new ServiceCollection().AddHttpClient<StatbankClient>().AddStandardResilienceHandler().PipelineName;


    [Fact]
    public void AddStatbankClient_RegistersIIndustryStatisticsProvider_ResolvingToTheCachingDecorator()
    {
        // The decorator wraps StatbankClient (plan section 6.2) -- the orchestrator only ever
        // sees the cached/resilient facade, never the raw typed client directly.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Statbank:BaseUrl"] = "https://api.statbank.dk/" })
            .Build();

        var services = new ServiceCollection();
        services.AddStatbankClient(configuration);
        using var provider = services.BuildServiceProvider();

        var statistics = provider.GetRequiredService<IIndustryStatisticsProvider>();

        statistics.Should().BeOfType<CachingIndustryStatisticsProvider>();
        provider.GetRequiredService<StatbankClient>().Should().NotBeNull();
    }

    [Fact]
    public void AddStatbankClient_WithEmptyBaseUrl_ThrowsOnOptionsAccess()
    {
        // [Required] rejects an empty string, unlike a missing key -- a missing key leaves
        // StatbankOptions.BaseUrl at its non-null property-initializer default, which would
        // pass validation and defeat the point of this test.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Statbank:BaseUrl"] = "" })
            .Build();

        var services = new ServiceCollection();
        services.AddStatbankClient(configuration);
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<StatbankOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void AddStatbankResiliencePipeline_WithConfiguredValues_AppliesThemToTheBuiltPipeline()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Statbank:BaseUrl"] = "https://api.statbank.dk/",
                ["Statbank:Resilience:TotalTimeoutSeconds"] = "42",
                ["Statbank:Resilience:AttemptTimeoutSeconds"] = "7",
                ["Statbank:Resilience:MaxRetryAttempts"] = "9",
                ["Statbank:Resilience:CircuitFailureRatio"] = "0.25",
                ["Statbank:Resilience:CircuitMinimumThroughput"] = "20",
                ["Statbank:Resilience:CircuitSamplingDurationSeconds"] = "60",
                ["Statbank:Resilience:CircuitBreakDurationSeconds"] = "90",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddStatbankClient(configuration).AddStatbankResiliencePipeline(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>().Get(ResolveStatbankPipelineName());

        options.TotalRequestTimeout.Timeout.Should().Be(TimeSpan.FromSeconds(42));
        options.AttemptTimeout.Timeout.Should().Be(TimeSpan.FromSeconds(7));
        options.Retry.MaxRetryAttempts.Should().Be(9);
        options.CircuitBreaker.FailureRatio.Should().Be(0.25);
        options.CircuitBreaker.MinimumThroughput.Should().Be(20);
        options.CircuitBreaker.SamplingDuration.Should().Be(TimeSpan.FromSeconds(60));
        options.CircuitBreaker.BreakDuration.Should().Be(TimeSpan.FromSeconds(90));
    }

    [Fact]
    public void AddStatbankResiliencePipeline_WithEmptyConfiguration_KeepsThePreviousHardcodedDefaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Statbank:BaseUrl"] = "https://api.statbank.dk/" })
            .Build();

        var services = new ServiceCollection();
        services.AddStatbankClient(configuration).AddStatbankResiliencePipeline(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>().Get(ResolveStatbankPipelineName());

        options.TotalRequestTimeout.Timeout.Should().Be(TimeSpan.FromSeconds(15));
        options.AttemptTimeout.Timeout.Should().Be(TimeSpan.FromSeconds(5));
        options.Retry.MaxRetryAttempts.Should().Be(3);
        options.CircuitBreaker.FailureRatio.Should().Be(0.5);
        options.CircuitBreaker.MinimumThroughput.Should().Be(10);
        options.CircuitBreaker.SamplingDuration.Should().Be(TimeSpan.FromSeconds(30));
        options.CircuitBreaker.BreakDuration.Should().Be(TimeSpan.FromSeconds(30));
    }
}
