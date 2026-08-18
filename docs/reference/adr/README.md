# Architectural Decision Records (ADR)

Records the six significant, open-ended decisions the task left to the implementer — each was
posed as a clarifying question before any code was written. Smaller, implementation-level
decisions are listed in [`design-decisions.md`](../design-decisions.md).

| ADR | Decision |
| --- | --- |
| [0001](0001-modular-monolith-topology.md) | Modular monolith over microservices |
| [0002](0002-otel-prometheus-grafana-observability.md) | OpenTelemetry + Prometheus + Grafana for observability |
| [0003](0003-polly-in-memory-cache-resilience.md) | Polly + in-memory cache for resilience |
| [0004](0004-frontend-calls-api-over-http.md) | Frontend calls the API over HTTP, not by direct reference |
| [0005](0005-hermetic-tests-with-opt-in-live-smoke.md) | Fully hermetic test suite, with an opt-in live smoke test |
| [0006](0006-danish-ui-english-codebase.md) | Danish UI language, English codebase and API contract |
