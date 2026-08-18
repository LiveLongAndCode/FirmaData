namespace FirmaData.Domain;

public readonly record struct StatisticsYear
{
    // Coarse sanity floor, not the authoritative range. DB07 -- the branchekode standard the
    // task's BRANCHE07 variable refers to -- took effect in 2008, and Statbank's ERHV1 table
    // has never published a year earlier than that (confirmed live via GET /v1/tableinfo, see
    // FirmaData.Statbank). The actual currently-published range is discovered dynamically by
    // IIndustryStatisticsProvider.GetAvailableYearsAsync and can differ from this floor over
    // time; this constant only rejects nonsense input (e.g. year 0) before it reaches the network.
    public const int EarliestYear = 2008;

    // currentYear defaults to the real clock. A DI-based TimeProvider isn't threaded in here --
    // this is a readonly record struct in Domain with no DI (plan fase 7, F9c) -- so a caller
    // that needs deterministic "now" (tests, or a future TimeProvider-aware caller) passes it
    // explicitly instead.
    public static Result<StatisticsYear> TryCreate(int year, int? currentYear = null)
    {
        var resolvedCurrentYear = currentYear ?? DateTime.UtcNow.Year;

        if (year < EarliestYear)
        {
            return Result.Validation($"Year must be {EarliestYear} or later.");
        }

        if (year > resolvedCurrentYear)
        {
            return Result.Validation("Year cannot be in the future.");
        }

        return new StatisticsYear(year);
    }

    public int Value { get; }

    private StatisticsYear(int value) => Value = value;

    public override string ToString() => Value.ToString();
}
