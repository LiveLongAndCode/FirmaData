using FirmaData.Application;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FirmaData.Statbank.Tests;

public class ServiceCollectionExtensionsTests
{
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
}
