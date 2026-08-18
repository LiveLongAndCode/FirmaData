namespace FirmaData.Domain;

// Not specified by the CVR wire format 1:1 -- FirmaData.Cvr maps the API's `status` and
// `bankrupt` fields onto this set (Phase 2); a value CVR returns that isn't recognised there
// maps to Unknown rather than failing the whole lookup.
public enum CompanyStatus
{
    Active,
    Ceased,
    Bankrupt,
    Unknown
}
