using FluentAssertions;

namespace FirmaData.Domain.Tests;

public class IndustryCodeTests
{
    [Theory]
    [InlineData("651200")] // Anden forsikring -- LB Forsikring's real branchekode
    [InlineData("999999")] // Statbank's "Uoplyst aktivitet" sentinel -- still a valid *format*
    public void TryCreate_WithSixDigits_Succeeds(string input)
    {
        var result = IndustryCode.TryCreate(input);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(input);
    }

    [Theory]
    [InlineData("65120")]   // 5 digits
    [InlineData("6512000")] // 7 digits
    [InlineData("65120X")]  // non-digit
    [InlineData("")]
    public void TryCreate_WithMalformedInput_Fails(string input)
    {
        var result = IndustryCode.TryCreate(input);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ResultErrorType.Validation);
    }

    [Fact]
    public void TryCreate_WithNullInput_Fails()
    {
        var result = IndustryCode.TryCreate(null);

        result.IsFailure.Should().BeTrue();
    }
}
