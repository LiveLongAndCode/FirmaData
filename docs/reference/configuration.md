# Configuration

Configuration comes from `appsettings.json` and environment variables only — no cloud vendor
configuration services. Nested keys use `__` as the separator when set as environment variables
(`Api__BaseUrl`), as `docker-compose.yml` does.

## Settings

| Key | Default | Purpose |
| --- | --- | --- |
| `Cvr:BaseUrl` | `https://apicvr.dk/` | CVR API base address |
| `Statbank:BaseUrl` | `https://api.statbank.dk/` | Danmarks Statistik base address |
| `Statbank:FallbackYear` | `2022` | Used only if the live year-discovery call (Statbank's `tableinfo` endpoint) fails |
| `Api:BaseUrl` (`FirmaData.Web`) | `http://localhost:8080/` | Where the frontend calls the API; overridden to `http://firmadata-api:8080/` inside `docker-compose.yml`, and to `http://localhost:5188/` for local dev |

There are no credentials to configure: both upstream APIs are public and unauthenticated.

## Fixed resilience and cache values

Not currently config-bound — hardcoded per adapter, and identical for CVR and Statbank:

| Setting | Value |
| --- | --- |
| Total request timeout | 15 s |
| Per-attempt timeout | 5 s |
| Retry | 3 attempts, exponential backoff + jitter |
| Circuit breaker | opens at ≥10 requests with ≥50% failures in a 30 s window, breaks for 30 s |
| Statbank result cache — positive | 24 h |
| Statbank result cache — negative (`NotAvailableForYear`) | 5 min |

The reasoning behind these mechanisms is in
[ADR-0003](adr/0003-polly-in-memory-cache-resilience.md); the circuit breaker can be
exercised end to end with the harness described in [testing](testing.md).
