namespace FirmaData.Statbank;

// Wire shape of a POST /v1/data error body. An unavailable year returns HTTP 400 with
// {"errorTypeCode":"EXTRACT-NOTFOUND"} -- that specific code is what maps to
// EnrichmentStatus.NotAvailableForYear rather than a hard failure (plan section 4.2).
internal sealed record StatbankErrorResponse(string? ErrorTypeCode);
