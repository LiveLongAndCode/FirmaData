using FirmaData.Application;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FirmaData.Cvr.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddCvrClient_RegistersICompanyDirectory_ResolvingToCvrApiClient()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Cvr:BaseUrl"] = "https://apicvr.dk/" })
            .Build();

        var services = new ServiceCollection();
        services.AddCvrClient(configuration);
        using var provider = services.BuildServiceProvider();

        var directory = provider.GetRequiredService<ICompanyDirectory>();

        directory.Should().BeOfType<CvrApiClient>();
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
}
