using FluentAssertions;

namespace FirmaData.Domain.Tests;

public class CvrNumberTests
{
    [Fact]
    public void TryCreate_WithValidRealCvrNumber_Succeeds()
    {
        // LB Forsikring A/S -- confirmed live against apicvr.dk during planning.
        var result = CvrNumber.TryCreate("16500836");

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be("16500836");
    }

    [Fact]
    public void TryCreate_WithFailedChecksum_Fails()
    {
        // Same digits as the valid fixture above with the last one flipped (6 -> 7).
        var result = CvrNumber.TryCreate("16500837");

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ResultErrorType.Validation);
    }

    [Theory]
    [InlineData("1650083")]   // 7 digits
    [InlineData("165008366")] // 9 digits
    [InlineData("1650083X")]  // non-digit
    [InlineData("")]
    [InlineData("   ")]
    public void TryCreate_WithMalformedInput_Fails(string input)
    {
        var result = CvrNumber.TryCreate(input);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ResultErrorType.Validation);
    }

    [Fact]
    public void TryCreate_WithNullInput_Fails()
    {
        var result = CvrNumber.TryCreate(null);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void TryCreate_TrimsSurroundingWhitespace()
    {
        var result = CvrNumber.TryCreate(" 16500836 ");

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be("16500836");
    }

    [Fact]
    public void TwoInstancesWithSameValue_AreEqual()
    {
        var a = CvrNumber.TryCreate("16500836").Value;
        var b = CvrNumber.TryCreate("16500836").Value;

        a.Should().Be(b);
        (a == b).Should().BeTrue();
    }
}
