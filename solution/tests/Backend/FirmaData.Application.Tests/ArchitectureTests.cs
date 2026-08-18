using FirmaData.Domain;
using FluentAssertions;
using NetArchTest.Rules;

namespace FirmaData.Application.Tests;

// Enforces the dependency rule from plan section 2.1 mechanically, so the Ports & Adapters
// boundary fails a test instead of rotting silently as the codebase grows.
public class ArchitectureTests
{
    [Fact]
    public void Domain_DoesNotDependOnAnyOtherProject()
    {
        var result = Types.InAssembly(typeof(Company).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "FirmaData.Application",
                "FirmaData.Cvr",
                "FirmaData.Statbank",
                "FirmaData.Contracts",
                "FirmaData.Api")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void Application_DoesNotDependOnAnyAdapter()
    {
        var result = Types.InAssembly(typeof(ICompanyDirectory).Assembly)
            .Should()
            .NotHaveDependencyOnAny("FirmaData.Cvr", "FirmaData.Statbank")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    private static string BuildFailureMessage(TestResult result) =>
        result.IsSuccessful
            ? string.Empty
            : $"Offending types: {string.Join(", ", result.FailingTypeNames ?? [])}";
}
