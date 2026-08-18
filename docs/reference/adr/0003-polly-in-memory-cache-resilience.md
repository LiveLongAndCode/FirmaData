# ADR-0003: Polly + in-memory cache for resilience

## Context

The task requires robust handling of downstream failures. Three options for what sits in front
of the CVR and Statbank HTTP clients:

1. **Polly only, no cache** — retry, timeout, and circuit breaker per client, but every request
   hits the external APIs. Simplest, but loses an easy performance story that ties into the
   metrics requirement (industry statistics rarely change within a year).
2. **Polly + Redis** — the same resilience policies plus a distributed cache container. More
   production-like, one more moving part to run and explain in a 30-minute conversation.
3. **Polly + in-memory cache** — the same resilience policies, plus `IMemoryCache` for Statbank
   industry data, which is exactly the kind of highly-cacheable, slowly-changing data an
   in-process cache is good at.

## Decision

Polly + in-memory cache, via `Microsoft.Extensions.Http.Resilience`: retry with exponential
backoff and jitter, a per-attempt and total timeout, and a circuit breaker, per external client.
`CachingIndustryStatisticsProvider` decorates `IIndustryStatisticsProvider` with `IMemoryCache`
without changing the interface or its consumer (Open/Closed).

## Consequences

- No extra infrastructure to run or explain — `IMemoryCache` needs nothing beyond the process
  itself.
- Positive results cache for 24 hours (industry statistics are year-scoped, effectively static
  within a day); definitive negative results (`NotAvailableForYear`) cache for 5 minutes;
  transient `Unavailable` results are never cached, so Polly's retry pipeline still gets to do its
  job on the next call instead of being masked by a cached failure.
- A `ConcurrentDictionary<string, SemaphoreSlim>` gate coalesces concurrent requests for the same
  cache key into one upstream call, avoiding a cache-stampede on a cold key.
- What this gives up, and is the documented next step: the cache does not survive a restart and
  is not shared across replicas. A multi-instance deployment would need Redis so a restart isn't a
  cold cache and all replicas see the same warm data.
