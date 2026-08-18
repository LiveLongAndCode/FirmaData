# Architecture

C# on .NET 10, Ports & Adapters (hexagonal). The [root README](../../README.md) has the request-flow
diagram; this document is the reasoning and the layout behind it.

## Dependency direction

The arrows in that diagram are *request* flow, not project references. `FirmaData.Application`
calls the adapters only through two port interfaces — `ICompanyDirectory` and
`IIndustryStatisticsProvider` — that it owns. The compile-time dependency runs the *opposite* way:
the adapters reference `FirmaData.Application` to implement its ports, never the reverse.

```
FirmaData.Domain → FirmaData.Application ← FirmaData.Cvr / FirmaData.Statbank → FirmaData.Api
```

That inversion is what makes the topology hexagonal, and `ArchitectureTests` (NetArchTest) fails
the build if it is ever broken — see [testing](testing.md).

## Projects

| Project | Responsibility |
| --- | --- |
| `FirmaData.Domain` | Domain objects and `Result<T>`. Depends on nothing. |
| `FirmaData.Application` | `CompanyEnrichmentService` and the two port interfaces |
| `FirmaData.Contracts` | The DTOs the API exposes over the wire |
| `FirmaData.Cvr` | Adapter for `apicvr.dk`, implementing `ICompanyDirectory` |
| `FirmaData.Statbank` | Adapter for `api.statbank.dk`, plus the caching decorator |
| `FirmaData.Api` | ASP.NET Core host and composition root — DI, resilience, metrics, Swagger |
| `FirmaData.Web` | MVC frontend, an ordinary HTTP client of the API |

## Cross-cutting concerns

* **API surface** — ASP.NET Core Web API with Swagger/OpenAPI.
* **Logging** — structured JSON via Serilog, with a correlation id on every log line and every
  error response.
* **Observability** — OpenTelemetry metrics on `/metrics`, scraped by Prometheus: request and
  dependency latency, circuit breaker state, cache hit/miss. Grafana renders them.
* **Resilience** — Polly for both upstream dependencies: circuit breaker, retry with exponential
  backoff and jitter, and per-attempt plus total timeouts. Values in [configuration](configuration.md).
* **Caching** — `IMemoryCache` with a [`SemaphoreSlim`](https://learn.microsoft.com/en-us/dotnet/api/system.threading.semaphoreslim) (*external*) gate per cache key, so concurrent calls for
  the same key coalesce into one upstream call instead of stampeding.
* **Error propagation** — `Result<T>`. Exceptions are reserved for genuinely unexpected state; see
  [API reference](api.md) for how results map to status codes.

## Why these shapes

The six significant architectural choices are recorded as ADRs in [`adr/readme.md`](adr/readme.md), and
the smaller implementation-level ones are listed in [design decisions](design-decisions.md).
