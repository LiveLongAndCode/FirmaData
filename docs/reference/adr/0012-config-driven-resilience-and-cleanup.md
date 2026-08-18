# ADR-0012: Config-driven resilience budgets, CVR caching, PII-safe logging, and TimeProvider

## Context

Four smaller, independent items bundled into one fase (plan fase 7, F8 + F9a/b/c) because none
individually warranted its own PR:

- **F8**: CVR/Statbank resilience budgets (timeouts, retry counts, circuit-breaker thresholds)
  were hardcoded in each adapter's `ServiceCollectionExtensions`. Adjusting one in production
  required a rebuild. `CvrApiHealthCheck`'s hardcoded 3s timeout had also become tight since the
  .NET 10 upgrade (WireMock.Net 2.x's slower first-response time, see ADR-0007), making it worth
  fixing alongside the rest of the resilience configuration.
- **F9a**: `appsettings.json` had no `Serilog` section, so Serilog fell back to defaults that log
  `System.Net.Http.HttpClient` at `Information` — including the outgoing request URL, which for
  `GET /api/v1/companies?name=` is the search string, potentially a person's name.
- **F9b**: `CachingIndustryStatisticsProvider` decorates `StatbankClient`, but no equivalent
  existed for CVR lookups — the same CVR number looked up repeatedly cost one upstream call each
  time.
- **F9c**: `EnrichedCompanyMapping`, `MetadataController`, and `StatisticsYear` all read
  `DateTime.UtcNow`/`DateTimeOffset.UtcNow` directly, making `RetrievedAt` and year-validation
  behaviour untestable deterministically and coupled to the real clock at test time.

## Decision

1. **F8**: new `ResilienceOptions` (one class per adapter project, matching the existing
   `CvrOptions`/`StatbankOptions` pattern) bound from `Cvr:Resilience`/`Statbank:Resilience`, with
   the previously-hardcoded values as defaults. Bound *eagerly* from `IConfiguration` inside
   `AddCvrResiliencePipeline`/`AddStatbankResiliencePipeline` (not via a DI-resolved
   `IOptions<ResilienceOptions>` inside the resilience pipeline's own lazy `Configure` callback) —
   both approaches were verified to apply the configured values correctly, but the eager read was
   kept for simplicity once the actual regression below was root-caused elsewhere.
   `HttpClient.Timeout` is set to `Timeout.InfiniteTimeSpan` on both typed clients so the
   resilience pipeline's own timeout is the only budget in effect, instead of competing with
   `HttpClient`'s implicit 100s default. `CvrApiHealthCheck` reads `HealthCheckTimeoutSeconds`
   from `ResilienceOptions` instead of a hardcoded 3s (Cvr-only; `StatbankApiHealthCheck` keeps
   its hardcoded value, out of scope here).
2. **F9a**: added a `Serilog` section to `appsettings.json` overriding
   `System.Net.Http.HttpClient` and `Polly` to `Warning`; `appsettings.Development.json` restores
   `System.Net.Http.HttpClient` to `Information` so outgoing calls stay visible locally.
3. **F9b**: `CachingCompanyDirectory` decorates `ICompanyDirectory`, mirroring
   `CachingIndustryStatisticsProvider`'s shape but with a 10-minute TTL (not 24h — master data
   changes, annual statistics don't) and caching only `GetByCvrAsync` (not `SearchByNameAsync`,
   which has high cardinality, low reuse, and would put PII in the cache key). `AddCvrClient` now
   registers the concrete `CvrApiClient` and wires the decorator via a factory, matching
   `AddStatbankClient`'s existing pattern — it can no longer register the typed client directly
   as `ICompanyDirectory`, since that can't be decorated afterward.
4. **F9c**: `TimeProvider` is injected into `EnrichedCompanyMapping.ToResponse` (as a parameter,
   not by making the mapper instance-based) and `MetadataController`. `StatisticsYear.TryCreate`
   gained an optional `currentYear` parameter (`internal currentYear ?? DateTime.UtcNow.Year`)
   rather than pulling DI into Domain, since it's a `readonly record struct` with no DI story.

## A regression found and fixed along the way

While building F9c, introducing `builder.Services.AddSingleton(TimeProvider.System)` and having
`ApiFactory` override it with a frozen `FakeTimeProvider` caused every retried request in
`CompaniesEndpointTests` to hang for ~100 seconds (traced to the test client's own default
`HttpClient.Timeout`, not any budget this app's pipeline sets). Root cause:
`Microsoft.Extensions.Http.Resilience`'s pipelines resolve an *unkeyed* `TimeProvider` from the
same DI container to drive Polly's own retry and timeout delays. A frozen `FakeTimeProvider`
registered as that same unkeyed service starves every scheduled retry — Polly logs the retry
decision, then waits on a clock that never advances, until an unrelated outer timeout eventually
gives up.

Fix: the app's own `TimeProvider` is registered as a **keyed** singleton (`AppTimeProvider.
ServiceKey = "app"`), consumed via `[FromKeyedServices(AppTimeProvider.ServiceKey)]` in
`CompaniesController`/`MetadataController`. `ApiFactory` overrides only that keyed registration;
the unkeyed `TimeProvider` Polly resolves is left alone (defaulting to the real
`TimeProvider.System` behaviour it already had). See [gotchas.md](../gotchas.md) for the full
writeup — this is the kind of thing worth knowing before touching either `TimeProvider` or the
resilience pipelines again.

## Consequences

- Resilience budgets can now be tuned per environment via configuration without a rebuild;
  defaults are unchanged, so behaviour with no configuration is identical to before this fase.
- CVR lookups are cached for 10 minutes, cutting repeated-lookup load on apicvr.dk; search
  remains uncached by design.
- Outgoing HTTP URLs (which can carry a search string) no longer log at `Information` in
  production.
- `AddCvrClient`'s public registration shape changed (`ICompanyDirectory` no longer resolves to
  the concrete `CvrApiClient` directly) — an internal detail, but any test asserting on the
  concrete type needed updating (`FirmaData.Cvr.Tests/ServiceCollectionExtensionsTests`).
- Any *future* code that needs "now" must go through the keyed `AppTimeProvider.ServiceKey`
  registration, not inject a plain `TimeProvider` — otherwise it either gets Polly's real-time
  `TimeProvider.System` unexpectedly in that one spot, or (worse) some future change registers a
  fake unkeyed `TimeProvider` again and silently reintroduces the hang.
