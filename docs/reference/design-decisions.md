# Design decisions

## Architectural Decision Records

The six most significant, open-ended decisions the task left to the implementer are recorded as
ADRs. Each one carries the full context and the alternatives considered — see
[`adr/`](adr/).

| ADR | Decision |
| --- | --- |
| [0001](adr/0001-modular-monolith-topology.md) | Modular monolith over microservices |
| [0002](adr/0002-otel-prometheus-grafana-observability.md) | OpenTelemetry + Prometheus + Grafana for observability |
| [0003](adr/0003-polly-in-memory-cache-resilience.md) | Polly + in-memory cache for resilience |
| [0004](adr/0004-frontend-calls-api-over-http.md) | Frontend calls the API over HTTP, not by direct reference |
| [0005](adr/0005-hermetic-tests-with-opt-in-live-smoke.md) | Fully hermetic test suite, with an opt-in live smoke test |
| [0006](adr/0006-danish-ui-english-codebase.md) | Danish UI language, English codebase and API contract |

## Implementation-level decisions

Smaller decisions that didn't warrant a standalone ADR, but that explain why the code looks the
way it does:

* **Anti-corruption layers.** `CvrCompanyResponse` and Statbank's request/response types are
  internal to each adapter and never leak out; only domain types cross the boundary, so a change
  in an upstream format is isolated to one project.
* **`Result<T>` over exceptions for expected failure.** CVR-not-found, statistics unavailable for
  a given year, and similar are expected states, not exceptions. `GlobalExceptionHandler` handles
  whatever slips through anyway.
* **Caching as a decorator (Open/Closed).** `CachingIndustryStatisticsProvider` wraps
  `StatbankClient` without modifying either.
* **Cache-stampede protection.** A `ConcurrentDictionary<string, SemaphoreSlim>` ensures
  concurrent calls for the same cache key coalesce into one upstream call instead of firing several
  identical ones in parallel.
* **Statbank lookups are deduplicated on name search.** Many search results share an industry
  code; `CompanyEnrichmentService` deduplicates codes and fires the Statbank calls in parallel —
  not one per search result.
* **Year selection with a fallback.** Available statistics years are discovered live from
  Statbank's `tableinfo` endpoint and cached; if that lookup fails, a configured `FallbackYear` is
  used instead — the caller never sees the failure.
* **Degraded responses signal via a custom header, not `Warning`.** `FirmaData-Degraded-Source`
  is set instead of the standard HTTP `Warning` header, which RFC 9111 removed from the spec; the
  JSON body's `StatisticsStatus` remains the authoritative signal, the header is a secondary,
  parse-free one for HTTP-only clients.
* **CVR's 200-with-`NOT_FOUND`-body.** `apicvr.dk` returns HTTP 200 with `{"error":"NOT_FOUND"}`
  for unknown CVR numbers instead of 404. Handled explicitly in the adapter rather than by reading
  the status code — see [gotchas](gotchas.md).
* **The dependency-metrics handler sits outside Polly's pipeline.** `DependencyMetricsHandler` is
  added *before* the resilience pipeline, so it observes the overall outcome including
  `circuit_open` and `timeout` — not only what makes it through to the HTTP layer.
