# ADR-0011: Classify Statbank 400s instead of treating them all alike

## Context

`StatbankClient.GetAsync` mapped every `EXTRACT-NOTFOUND` 400 to `NotFound` ("no statistics for
that year"), and every *other* 400 to `Unavailable` (503 + `Retry-After`). Two problems:

- `EXTRACT-NOTFOUND` covers at least two distinct causes. The requested year genuinely has no
  data — the intended case — but CVR has also started issuing industry codes from the 2025
  revision (DB25), which ERHV1 (DB07) doesn't recognise at all. `IndustryCode` validates format
  only (six digits), so a DB25 code passes Domain validation and only fails once it reaches
  Statbank, currently reported identically to a genuinely unavailable year.
- Any other 400 (a rejected variable, a contract drift) became `Unavailable`, telling the client
  to retry — but a malformed request will fail identically on every retry, so `Retry-After` was
  actively misleading.

## Decision

1. Distinguish on the error envelope's message, not just `ErrorTypeCode`: only an
   `EXTRACT-NOTFOUND` 400 whose message explicitly names `BRANCHE07` is classified as "industry
   code not supported"; anything else with that error code stays `NotAvailableForYear`. This
   required extending `StatbankErrorResponse` to also deserialize the message field (previously
   only `ErrorTypeCode` was read).
   ⚠️ **The `message` JSON field name is this implementation's best assumption, not a
   live-confirmed fact.** It must be verified against a real 400 response (the live smoke
   workflow) before this classification can be relied on in production. Until confirmed, an
   unrecognised message shape safely falls back to the pre-existing `NotAvailableForYear`
   behaviour — it degrades to the old behaviour, it doesn't misclassify.
2. Introduce `EnrichmentStatus.IndustryCodeNotSupported` (and the underlying
   `ResultErrorType.IndustryCodeNotSupported`) rather than reusing `NotAvailableForYear` — the two
   causes have different implications for a consumer (retry with a different year vs. the code
   will never resolve against this table) and reusing an existing value would hide that.
3. Any other 400 (not `EXTRACT-NOTFOUND`) now maps to `Unexpected` → 502, not `Unavailable` → 503.
   A broken integration shouldn't tell the client to retry.

In search results, an unsupported industry code degrades only the companies that share it — this
already worked, since `SearchAndEnrichAsync` looks up statistics per distinct industry code and
keeps `stats.ValueOrDefault` per company; it now has a test locking that behaviour in.

## Consequences

- Both `IndustryCodeNotSupported` and `NotFound` are cached as negative results by
  `CachingIndustryStatisticsProvider` (5 minutes) — both are definitive answers, not transient
  failures, so treating them differently there would be inconsistent.
- Outward-facing: `statisticsStatus` gains a new value in the JSON body. This is additive, not a
  breaking change — the field is a string, not a fixed status code — but any consumer that
  switches on its value (this repo's own `FirmaData.Web`, in particular) needs a case for it. The
  frontend's Danish label was updated in the same change.
- **Deliberately no DB25→DB07 mapping table.** Building and maintaining a mapping between two
  classification revisions is a standalone effort with its own accuracy and upkeep burden, out of
  scope for this fase. The industry code is reported as unsupported rather than guessed at.
- `ResultErrorMapping` (the generic `ResultError` → HTTP status mapper) has no explicit case for
  `IndustryCodeNotSupported`: in the current architecture, a Statbank result is always laundered
  through `ResultExtensions.ToStatus()` into an `EnrichmentStatus` before it can reach the API
  boundary (graceful degradation — see [api.md](../api.md#partial-success-is-not-failure)), so
  this error type never reaches `ResultErrorMapping` in practice. If that assumption ever changes,
  it falls through to the `_ => 500` default, same as any other unmapped case.
