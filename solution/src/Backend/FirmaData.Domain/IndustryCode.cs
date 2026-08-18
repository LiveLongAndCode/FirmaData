namespace FirmaData.Domain;

// 6-digit DB07 branchekode. Format-only validation -- an unrecognised code (e.g. Statbank's
// "999999" / Uoplyst aktivitet sentinel) is still a syntactically valid IndustryCode; whether
// it resolves to real statistics is a FirmaData.Statbank concern, not a Domain one.
public readonly record struct IndustryCode
{
    public static Result<IndustryCode> TryCreate(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return Result.Validation("Industry code is required.");
        }

        var trimmed = input.Trim();

        if (trimmed.Length != 6 || !trimmed.All(char.IsAsciiDigit))
        {
            return Result.Validation("Industry code must be exactly 6 digits (DB07 branchekode).");
        }

        return new IndustryCode(trimmed);
    }

    public string Value { get; }

    private IndustryCode(string value) => Value = value;

    public override string ToString() => Value;
}
