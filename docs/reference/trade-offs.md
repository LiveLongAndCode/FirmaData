# Trade-offs and omissions

Left out deliberately, so they read as decisions rather than gaps:

| Left out | Why | What production would need |
| --- | --- | --- |
| AuthN/AuthZ | No requirement; would add setup noise | API keys or OAuth2 at a gateway, per-client rate limits |
| Persistence | Both sources are authoritative; nothing to own | Postgres for audit/history if lookups must be replayable |
| Distributed cache | Single replica; `IMemoryCache` is enough | Redis, so replicas share a cache and a restart isn't a cold start |
| Microservices | Complexity without benefit at this size | Split only if CVR and Statbank need independent scaling; boundaries are already drawn |
| Full tracing backend | Metrics answer the stated user story | OTLP → Tempo/Jaeger for cross-service traces |
| Kubernetes manifests | Compose is enough to demo | Helm chart, HPA, liveness/readiness wired to the resilience settings |
| Full localisation | Danish UI, English contract — see [ADR-0006](adr/0006-danish-ui-english-codebase.md) | `IStringLocalizer` + resource files if an English UI is ever needed |
| Statbank bulk/batch | One industry per lookup is the actual access pattern | Batch by industry code if usage turns bulk |

## Further production hardening

Beyond that table:

* **Rate limiting** — per-client throttling to protect the upstream dependencies.
* **Alerting** — Alertmanager rules on circuit-breaker state and p95 latency, paged via
  PagerDuty/Opsgenie.
* **Secrets management** — Azure Key Vault / AWS Secrets Manager / Vault instead of plain
  environment variables on the hosting platform.
* **Contract testing** — Pact-based consumer-driven contracts against the CVR and Statbank
  adapters, so breaking upstream changes are caught before they reach the live smoke tests.

The microservices half of this list is written out step by step in
[`monolith-to-microservices/`](../monolith-to-microservices/) — a documentation-only guide, nothing
there is implemented.
