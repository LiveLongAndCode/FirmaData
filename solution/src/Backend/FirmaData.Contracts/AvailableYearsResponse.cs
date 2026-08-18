namespace FirmaData.Contracts;

// Drives the UI's year dropdown (plan section 4.2): DefaultYear is the latest year Statbank
// currently publishes, so a caller that doesn't ask for a specific year knows what it got.
public sealed record AvailableYearsResponse(IReadOnlyList<int> Years, int DefaultYear);
