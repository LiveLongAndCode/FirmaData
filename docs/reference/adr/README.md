# Architectural Decision Records (ADR)

Records significant, open-ended decisions left to the implementer. The first six were each posed
as a clarifying question before any code was written; later entries record equally significant
decisions made after the initial build. Smaller, implementation-level decisions are listed in
[`design-decisions.md`](../design-decisions.md).

| ADR | Decision |
| --- | --- |
| [0001](0001-modular-monolith-topology.md) | Modular monolith over microservices |
| [0002](0002-otel-prometheus-grafana-observability.md) | OpenTelemetry + Prometheus + Grafana for observability |
| [0003](0003-polly-in-memory-cache-resilience.md) | Polly + in-memory cache for resilience |
| [0004](0004-frontend-calls-api-over-http.md) | Frontend calls the API over HTTP, not by direct reference |
| [0005](0005-hermetic-tests-with-opt-in-live-smoke.md) | Fully hermetic test suite, with an opt-in live smoke test |
| [0006](0006-danish-ui-english-codebase.md) | Danish UI language, English codebase and API contract |
| [0007](0007-net10-upgrade.md) | Upgrade from .NET 8 to .NET 10, OpenAPI generation moved off Swashbuckle |
| [0008](0008-statbank-parsing-hardening.md) | Strict Statbank CSV validation, `Unexpected` mapped to 502 |
| [0009](0009-name-search-normalization-and-reranking.md) | Normalize the name-search query, and re-rank results locally |
| [0010](0010-search-fanout-and-input-limits.md) | Cap search fan-out before enrichment, and validate search input |
| [0011](0011-statbank-400-classification.md) | Classify Statbank 400s instead of treating them all alike |
| [0012](0012-config-driven-resilience-and-cleanup.md) | Config-driven resilience budgets, CVR caching, PII-safe logging, and TimeProvider |
| [0013](0013-contract-field-renaming.md) | Rename ambiguous contract fields in place, no `/api/v2` |
