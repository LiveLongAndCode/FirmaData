namespace FirmaData.Statbank;

// Wire shape of a POST /v1/data error body. An unavailable year returns HTTP 400 with
// {"errorTypeCode":"EXTRACT-NOTFOUND"} -- that specific code is what maps to
// EnrichmentStatus.NotAvailableForYear rather than a hard failure (plan section 4.2).
//
// Message is used to distinguish two things that share the same ErrorTypeCode (plan fase 6, F5):
// the requested year has no data, versus the requested industry code isn't one ERHV1 (DB07)
// recognises at all (e.g. a DB25-revision code). ⚠️ The "message" JSON field name is this
// implementation's best assumption, not a live-confirmed fact -- verify it against a real 400
// response (live smoke workflow) before relying on IndustryCodeNotSupported classification in
// production; until confirmed, an unrecognised message shape safely falls back to the existing
// NotAvailableForYear behaviour rather than misclassifying.
internal sealed record StatbankErrorResponse(string? ErrorTypeCode, string? Message);
