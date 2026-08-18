# ADR-0010: Cap search fan-out before enrichment, and validate search input

## Context

`CompanyEnrichmentService.SearchAndEnrichAsync` enriched *every* result the CVR search returned:
all distinct industry codes, in an unbounded `Task.WhenAll` against Statbank — a shared public
service. `CompaniesController` only discarded anything past the first 10 results *afterwards*,
via `.Take(MaxSearchResults)`. A broad search term could return hundreds of matches and therefore
hundreds of concurrent Statbank calls, almost all of them for statistics that were then thrown
away unread.

Separately, `CompaniesController.SearchByName` only checked that `name` was non-empty. A
one-character query produced a wide, mostly-useless upstream search (compounding the fan-out
problem above); nothing bounded the string length either, so an arbitrarily long value went
straight into a path segment. `MaxSearchResults` was a hardcoded controller constant the client
could neither see nor influence.

## Decision

1. Move the result cap into `CompanyEnrichmentService.SearchAndEnrichAsync` itself, as a new
   `limit` parameter, applied **before** computing distinct industry codes and enriching — not
   after. Only candidates that are actually returned are ever looked up.
2. Bound Statbank concurrency with a `SemaphoreSlim` (default 4) around the `Task.WhenAll` loop.
3. Add `SearchOptions` (`FirmaData.Api`, section `Search`) with `MinNameLength` (2),
   `MaxNameLength` (100), `DefaultLimit` (10), `MaxLimit` (25), and
   `MaxConcurrentStatisticsCalls` (4) as defaults — validated at startup like `CvrOptions`/
   `StatbankOptions`. `CompaniesController` rejects a `name` outside the length bounds and a
   `limit` outside `[1, MaxLimit]` with `400`.
4. `CompanyEnrichmentService` takes `maxConcurrentStatisticsCalls` as a plain `int` constructor
   parameter, not `IOptions<SearchOptions>` or the Options package. `FirmaData.Application` must
   not gain a dependency on either adapter project or pick up new infrastructure package
   references (`ArchitectureTests` enforces the layering), so `Program.cs` reads the configured
   value and registers `ICompanyEnrichmentService` via a factory instead of
   `AddScoped<TInterface, TImplementation>`.

## Consequences

- Outward-facing: a search that used to return `200` can now return `400` (too short/long a name,
  or an out-of-range `limit`) — this must be called out in the changelog. Nothing that previously
  succeeded silently changes shape; the new `limit` query parameter is optional and defaults to
  the prior effective cap (10).
- Statbank call volume for a wide search drops from "every distinct industry code in the result
  set" to "every distinct industry code among the first `limit` results, at most
  `MaxConcurrentStatisticsCalls` at a time" — the fix is entirely about *when* the cap and the
  concurrency limit apply, not about changing what a client receives for a normally-sized search.
- The factory registration in `Program.cs` is the one place that owns the actual configured
  concurrency limit; `CompanyEnrichmentService`'s constructor default (4) exists only so
  existing unit tests that construct it directly don't need to pass a value, and is not meant to
  be relied on by the composition root.
