using FluentAssertions;

namespace FirmaData.Domain.Tests;

public class ResultTests
{
    [Fact]
    public void Success_ExposesValue_AndIsNotFailure()
    {
        Result<int> result = 42;

        result.IsSuccess.Should().BeTrue();
        result.IsFailure.Should().BeFalse();
        result.Value.Should().Be(42);
        result.ValueOrDefault.Should().Be(42);
    }

    [Fact]
    public void Success_AccessingError_Throws()
    {
        Result<int> result = 42;

        var act = () => result.Error;

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Failure_ExposesError_AndIsNotSuccess()
    {
        Result<int> result = Result.NotFound("nope");

        result.IsSuccess.Should().BeFalse();
        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ResultErrorType.NotFound);
        result.Error.Message.Should().Be("nope");
    }

    [Fact]
    public void Failure_AccessingValue_Throws()
    {
        Result<int> result = Result.Unavailable("down");

        var act = () => result.Value;

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Failure_ValueOrDefault_ReturnsDefault()
    {
        Result<string> result = Result.Validation("bad");

        result.ValueOrDefault.Should().BeNull();
    }
}
