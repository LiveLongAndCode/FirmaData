# ADR-0002: OpenTelemetry + Prometheus + Grafana for observability

## Context

The task requires metrics for both the service itself and the two external data sources: 95th
percentile response time and error rate. Two ways to satisfy that:

1. **`/metrics` endpoint only** — OpenTelemetry instrumentation with the Prometheus exporter,
   exposing histograms and counters at `/metrics`. The acceptance criterion is technically met,
   but the reader has to imagine what the numbers would look like on a dashboard.
2. **OTel + Prometheus + Grafana** — the same instrumentation, plus Prometheus and a
   pre-provisioned Grafana dashboard wired up in `docker-compose.yml`.

## Decision

OTel + Prometheus + Grafana, provisioned entirely as code (`ops/prometheus/`,
`ops/grafana/provisioning/`) so `docker compose up --build` produces a working dashboard with no
manual clicking — request/dependency latency, circuit breaker state, and cache hit/miss are all
visible on screen without extra setup.

## Consequences

- Two extra containers in `docker-compose.yml` (`prometheus`, `grafana`), justified by turning an
  abstract acceptance criterion into something that can be pointed at during the interview.
- `prometheus/prometheus:v2.53.0` is pinned rather than `:latest` — the newer major line renames
  scraped metrics to use dots instead of underscores, which silently breaks every PromQL query in
  the provisioned dashboard. Discovered during Phase 7 of the build.
- Grafana runs with anonymous viewer access enabled (`GF_AUTH_ANONYMOUS_ENABLED`) so the dashboard
  opens directly, appropriate for a local demo and never intended for anything Internet-facing.
- What production would need beyond this: an OTLP exporter into a real tracing backend
  (Tempo/Jaeger) for cross-service traces, and Alertmanager rules on circuit-breaker state and p95
  latency wired into paging — neither is in scope here.
