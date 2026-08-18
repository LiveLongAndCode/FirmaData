using FluentAssertions;

namespace FirmaData.Domain.Tests;

public class StatisticsYearTests
{
    [Fact]
    public void TryCreate_WithEarliestSupportedYear_Succeeds()
    {
        var result = StatisticsYear.TryCreate(StatisticsYear.EarliestYear);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(StatisticsYear.EarliestYear);
    }

    [Fact]
    public void TryCreate_OneYearBeforeEarliestSupportedYear_Fails()
    {
        var result = StatisticsYear.TryCreate(StatisticsYear.EarliestYear - 1);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ResultErrorType.Validation);
    }

    [Fact]
    public void TryCreate_WithCurrentYear_Succeeds()
    {
        var result = StatisticsYear.TryCreate(DateTime.UtcNow.Year);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void TryCreate_WithFutureYear_Fails()
    {
        var result = StatisticsYear.TryCreate(DateTime.UtcNow.Year + 1);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ResultErrorType.Validation);
    }
}
