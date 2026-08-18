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
| `Search:MinNameLength` | `2` | Shortest `name` accepted by `GET /api/v1/companies?name=`; shorter is `400` |
| `Search:MaxNameLength` | `100` | Longest `name` accepted; longer is `400` |
| `Search:DefaultLimit` | `10` | Results returned when the `limit` query parameter is omitted |
| `Search:MaxLimit` | `25` | Highest `limit` accepted; `0` or above this is `400` |
| `Search:MaxConcurrentStatisticsCalls` | `4` | Caps how many Statbank lookups a single search can run at once |
| `Cvr:Resilience:*` | see below | CVR client's resilience budget |
| `Statbank:Resilience:*` | see below | Statbank client's resilience budget |

There are no credentials to configure: both upstream APIs are public and unauthenticated.

## Resilience budgets

Config-bound per adapter (`Cvr:Resilience:*` / `Statbank:Resilience:*`), identical defaults for
both — adjusting a budget in production no longer requires a rebuild:

| Setting | Default |
| --- | --- |
| `TotalTimeoutSeconds` | `15` |
| `AttemptTimeoutSeconds` | `5` |
| `MaxRetryAttempts` | `3` |
| `CircuitFailureRatio` | `0.5` |
| `CircuitMinimumThroughput` | `10` |
| `CircuitSamplingDurationSeconds` | `30` |
| `CircuitBreakDurationSeconds` | `30` |
| `HealthCheckTimeoutSeconds` (`Cvr` only) | `3` |

The reasoning behind these mechanisms is in
[ADR-0003](adr/0003-polly-in-memory-cache-resilience.md); the circuit breaker can be
exercised end to end with the harness described in [testing](testing.md).

## Fixed cache values

Not config-bound:

| Setting | Value |
| --- | --- |
| CVR lookup cache (`GetByCvrAsync` only — not search) | 10 min |
| Statbank result cache — positive | 24 h |
| Statbank result cache — negative (`NotAvailableForYear`, `IndustryCodeNotSupported`) | 5 min |

## Logging

Serilog reads only the `Serilog` section (`ReadFrom.Configuration`); the `Logging` section above
configures the framework's own logging providers, which this app doesn't otherwise use. Outgoing
HTTP calls (`System.Net.Http.HttpClient`) and Polly are suppressed to `Warning` in production,
since their `Information`-level logs include the outgoing URL — for `GET
/api/v1/companies?name=`, that's the search string, potentially a person's name. Local development
(`appsettings.Development.json`) overrides `System.Net.Http.HttpClient` back to `Information` so
outgoing calls are visible while debugging. Search results themselves may contain personal data
(email/phone for a sole proprietorship) and must never be logged.
