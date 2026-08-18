# Testing

```bash
dotnet test solution/FirmaData.sln --filter "Category!=Live"
```

## The layers

Tests are split across five layers, one project per production project:

| Project | What's tested |
| --- | --- |
| `FirmaData.Domain.Tests` | Domain objects and `Result<T>` |
| `FirmaData.Application.Tests` | `CompanyEnrichmentService` (both ports mocked), plus architecture rules via NetArchTest |
| `FirmaData.Cvr.Tests` / `FirmaData.Statbank.Tests` | Adapter logic and mapping against stubbed HTTP responses |
| `FirmaData.Api.IntegrationTests` | End-to-end via `WebApplicationFactory` — endpoints, resilience, metrics, health checks |
| `FirmaData.Web.Tests` | Controller behaviour, view model mapping, Danish validation messages |

`ArchitectureTests` mechanically enforces that `FirmaData.Domain` depends on nothing else, and
that `FirmaData.Application` never references the adapter implementations directly. Break the
hexagonal boundary and the build fails.

## The live smoke tests

`Category=Live` selects a smoke-test class that calls the real CVR and Statbank APIs instead of
stubs. It is excluded from the default run and from CI's PR gate — see
[ADR-0005](adr/0005-hermetic-tests-with-opt-in-live-smoke.md) for why the suite is
otherwise fully hermetic.

Run it deliberately when you want to know whether the upstream contracts still hold:

```bash
dotnet test solution/FirmaData.sln --filter "Category=Live"
```

In CI it runs from the [`live-smoke.yml`](../../.github/workflows/live-smoke.yml) workflow, on manual
dispatch plus a nightly cron — never on a pull request. See [CI/CD](ci-cd.md).

## Circuit-breaker harness

[`tools/run_test-circuit-breaker.bat`](../../tools/run_test-circuit-breaker.bat) exercises the
circuit breaker against the running Docker stack. It:

1. Restarts `firmadata-api` with [`tools/tests/docker-compose.circuit-test.yml`](../../tools/tests/docker-compose.circuit-test.yml)
   layered on, pointing the CVR dependency at a local failing stub instead of `apicvr.dk`.
2. Runs [`tools/tests/test-circuit-breaker.py`](../../tools/tests/test-circuit-breaker.py), which
   fires enough traffic to trip the breaker and asserts the transitions.
3. Always restores `firmadata-api` to the real APIs afterwards, including after a failed run.

The Python script refuses to send any traffic unless it detects that routing is in place, so
running it by hand against a normally configured stack cannot hit the real upstream APIs. Results
are visible in Grafana's "Circuit breaker state" panel at http://localhost:3000.
