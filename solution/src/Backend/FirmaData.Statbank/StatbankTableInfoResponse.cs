namespace FirmaData.Statbank;

// Wire shape (trimmed) of GET /v1/tableinfo?id=ERHV1&format=JSON, used only to discover which
// years the TID variable currently publishes (plan section 4.2's "year discovery").
internal sealed record StatbankTableInfoResponse(IReadOnlyList<StatbankVariableInfo>? Variables);

internal sealed record StatbankVariableInfo(string? Id, IReadOnlyList<StatbankValueInfo>? Values);

internal sealed record StatbankValueInfo(string? Id);
