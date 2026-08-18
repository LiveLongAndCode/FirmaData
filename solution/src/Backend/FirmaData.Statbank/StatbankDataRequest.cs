namespace FirmaData.Statbank;

// Wire shape of the POST /v1/data request body (plan section 4.2). Serialized with
// System.Text.Json's camelCase web defaults, so property names map to table/format/
// valuePresentation/variables and code/values without needing explicit [JsonPropertyName]s.
internal sealed record StatbankDataRequest(
    string Table,
    string Format,
    string ValuePresentation,
    IReadOnlyList<StatbankVariable> Variables);

internal sealed record StatbankVariable(string Code, IReadOnlyList<string> Values);
