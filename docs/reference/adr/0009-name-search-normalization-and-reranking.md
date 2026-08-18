# ADR-0009: Normalize the name-search query, and re-rank results locally

## Context

`CvrApiClient.SearchByNameAsync` sends the search string straight into apicvr.dk's path-based
search endpoint (`api/v1/search/company/{name}`) and used to map every non-2xx response,
including 404, onto `Unavailable` (503 + `Retry-After`). Two consequences followed from that:

- A full legal name that includes its own company-form suffix, e.g. `"LB Forsikring A/S"` —
  this project's own test fixture — contains a `/`. Encoded, that becomes `%2F` in the path, and
  apicvr.dk 404s on it, which the client then reported as a 503 outage rather than "not found".
- A search with genuinely no matches also 404s, and was likewise reported as a 503 outage instead
  of an empty result — even though the orchestrator (`CompanyEnrichmentService.SearchAndEnrichAsync`)
  already has a `Count == 0` branch that a 404-as-503 mapping could never reach.

Separately, apicvr.dk's own result ordering is not relevance-ranked: an exact match (e.g.
`"NOVO NORDISK A/S"`) can be buried under loosely related results — fan clubs, staff
associations — that merely contain the query string somewhere in their name.

## Decision

1. Normalize the query before it reaches the upstream path: collapse whitespace, and strip a
   **trailing** company-form suffix (`A/S`, `ApS`, `I/S`, `K/S`, `P/S`, `IVS`, `SMBA`, `AMBA`,
   `FMBA`, `G/S`, and their fully-punctuated forms) when it's the name's own trailing token,
   separated by whitespace or a comma. A name that merely *starts* with a suffix-looking token
   (e.g. `"Aps Rådgivning"`) is left untouched — only the tail is ever inspected. Any remaining
   `/` (one that's actually part of the name, not a suffix) is replaced with a space rather than
   left for the path encoder to turn into `%2F`.
2. Map an upstream 404 to a successful empty result, not `Unavailable`. Any other non-2xx status
   is still a real failure.
3. Re-rank results locally after mapping: exact match (both sides normalized the same way) first,
   then a prefix match, then everything else. `OrderBy` is a stable sort, so upstream's own
   ordering is preserved within each rank group — this is a re-ranking, not a re-invention of
   apicvr.dk's relevance model.
4. Drop a bankrupt company (`CompanyStatus.Bankrupt`) from search results. `Unknown` is kept
   deliberately: only `"NORMAL"` has been confirmed live against apicvr.dk, so `Unknown` covers
   an unconfirmed status string, not a confirmed-inactive company, and filtering it out would cost
   otherwise-valid results.

All four changes live entirely in `FirmaData.Cvr`; nothing in `FirmaData.Domain` or
`FirmaData.Application` changed.

## Consequences

- Purely additive from a client's point of view: a request that used to fail with 503 can now
  succeed (with results, or with `[]`). Nothing that previously succeeded can now fail — the
  bankrupt-company filter is the only change capable of *removing* a result, which is why it was
  scoped narrowly to the one status that's actually confirmed to mean "gone".
- The re-ranking is a deliberate, documented deviation from apicvr.dk's own ordering, not a bug
  fix framed as a pass-through. If apicvr.dk's relevance model improves later, this local
  re-ranking may become redundant, but it costs nothing to keep — it degrades to a no-op stable
  pass-through when upstream order is already relevance-sorted.
