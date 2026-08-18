# Gotchas

Surprises worth knowing before reading the code — the ones that look like bugs but aren't.

## Upstream API discrepancies

Found by probing both APIs directly before writing the adapters:

1. **CVR returns HTTP 200 for unknown companies**, with body `{"error":"NOT_FOUND"}` — not 404.
   Status-code-only error handling would silently produce empty companies, so the adapter inspects
   the body.
2. **Statbank supports `"valuePresentation": "Code"`**, returning stable codes (`ARBSTED`,
   `ANSATTE`, `FULDBESK`, `LØNSUM`) in the CSV instead of Danish display text. The adapter parses
   against those codes rather than translatable prose. The CSV itself is semicolon-separated and
   carries a UTF-8 BOM — `StreamReader` with `detectEncodingFromByteOrderMarks: true` handles it;
   a naive `string` conversion would leave a stray U+FEFF on the first column name.
3. **A valid CVR industry code can still have no Statbank data.** CVR has started issuing
   branchekoder from the 2025 revision (DB25); ERHV1 only understands the older DB07
   classification. `IndustryCode` validates format only (six digits), so a DB25 code passes
   Domain validation and only fails once it reaches Statbank — surfaced as
   `EnrichmentStatus.IndustryCodeNotSupported`, distinct from `NotAvailableForYear` (the year is
   fine, the code isn't) and `SourceUnavailable` (Statbank itself is unreachable). There is no
   DB25→DB07 mapping table (a deliberate fase-6/F5 scope decision — see
   [ADR-0011](adr/0011-statbank-400-classification.md)); the industry code is simply reported as
   unsupported for statistics rather than guessed at.

## Codebase quirks

* **Central Package Management (CPM) is disabled in `FirmaData.Api.csproj`.**
  `OpenTelemetry.Exporter.Prometheus.AspNetCore` has never had a stable release (beta only), and
  CPM rejects prerelease versions via both bracketed pins and `VersionOverride`. The package
  resolves fine in a non-CPM project; its version is kept in sync with `Directory.Packages.props`
  manually.
* **The `Program` class is not `static`**, even though it only contains a `Main` method.
  `WebApplicationFactory<Program>` (used in the integration tests) requires its type argument to be
  a concrete, non-static type — a static class can't be used as a generic type argument (CS0718).
* **Prometheus is pinned to `v2.53.0`** in `docker-compose.yml`, not `:latest`. Newer major
  versions store scraped metric names with dots instead of underscores
  (`firmadata.dependency.requests` rather than `firmadata_dependency_requests`), which silently
  breaks every PromQL query in the provisioned dashboard.
