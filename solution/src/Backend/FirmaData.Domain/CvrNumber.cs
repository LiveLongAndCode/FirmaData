namespace FirmaData.Domain;

public readonly record struct CvrNumber
{
    // Danish modulus-11 check: weight each of the 8 digits left-to-right by
    // 2,7,6,5,4,3,2,1 and sum; valid iff the sum is divisible by 11.
    private static readonly int[] Weights = [2, 7, 6, 5, 4, 3, 2, 1];

    public static Result<CvrNumber> TryCreate(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return Result.Validation("CVR number is required.");
        }

        var trimmed = input.Trim();

        if (trimmed.Length != 8 || !trimmed.All(char.IsAsciiDigit))
        {
            return Result.Validation("CVR number must be exactly 8 digits.");
        }

        if (!HasValidChecksum(trimmed))
        {
            return Result.Validation("CVR number failed the modulus-11 checksum.");
        }

        return new CvrNumber(trimmed);
    }

    public string Value { get; }

    private CvrNumber(string value) => Value = value;

    private static bool HasValidChecksum(string digits)
    {
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            sum += (digits[i] - '0') * Weights[i];
        }

        return sum % 11 == 0;
    }

    public override string ToString() => Value;
}
