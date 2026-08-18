namespace FirmaData.Web.Models;

// Resultater (plan section 15, screen 2): a table of matches for a name search. Skipped
// entirely on a direct CVR hit, which goes straight to Virksomhed.
public sealed class SearchResultsViewModel
{
    public required string Query { get; init; }

    public required int Year { get; init; }

    public required IReadOnlyList<CompanySummaryViewModel> Companies { get; init; }
}
