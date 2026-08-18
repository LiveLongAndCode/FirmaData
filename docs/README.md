# Documentation

Everything that used to sit in one long root README, split by what you came looking for.
Start at the [root README](../README.md) if you just want the project running.

| Document | Read it for |
| --- | --- |
| [Getting started](reference/getting-started.md) | Running with and without Docker, prerequisites, helper scripts |
| [Architecture](reference/architecture.md) | Hexagonal layout, project responsibilities, what enforces it |
| [API reference](reference/api.md) | Endpoints, error handling, degraded responses |
| [Configuration](reference/configuration.md) | Settings keys, defaults, resilience and cache values |
| [Testing](reference/testing.md) | Test layers, live smoke tests, circuit-breaker harness |
| [Design decisions](reference/design-decisions.md) | The ADRs plus the smaller decisions behind the code |
| [Trade-offs and omissions](reference/trade-offs.md) | What was deliberately left out, and what production would need |
| [Gotchas](reference/gotchas.md) | Upstream API surprises and codebase quirks worth knowing first |
| [CI/CD](reference/ci-cd.md) | Pipelines and platform-agnosticism |

## Related

* [`reference/adr/`](reference/adr/) — the six Architectural Decision Records, with full context and
  the alternatives considered for each.
* [`monolith-to-microservices/`](monolith-to-microservices/) — a documentation-only guide to
  splitting this monolith into five services, and its production-hardening sequel. Nothing in
  either is implemented.
