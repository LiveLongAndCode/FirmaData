using System.Text.Json.Serialization;

namespace FirmaData.Cvr;

// Wire shape of a single company from apicvr.dk (GET /api/v1/{cvrNumber} and each element of
// GET /api/v1/search/company/{companyName}), trimmed to the fields this adapter maps -- see
// the mapping table in plan section 4.1. Deserialized case-insensitively, so most properties
// need no [JsonPropertyName]; only IndustryDescription differs from its wire name ("industrydesc")
// by more than casing. Unrecognised extra fields (p_units, fax, etc.) are ignored by default.
//
// An unknown CVR number returns HTTP 200 with body {"error":"NOT_FOUND"} instead of a 404 --
// Error is non-null exactly in that case and is checked before the rest of the DTO is trusted.
internal sealed record CvrCompanyResponse
{
    public long Vat { get; init; }

    public string? Name { get; init; }

    public string? Address { get; init; }

    public int? Zipcode { get; init; }

    public string? City { get; init; }

    public string? IndustryCode { get; init; }

    [JsonPropertyName("industrydesc")]
    public string? IndustryDescription { get; init; }

    public int? Employees { get; init; }

    public string? Status { get; init; }

    public bool? Bankrupt { get; init; }

    public string? Error { get; init; }
}
