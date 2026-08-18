# ADR-0005: Fully hermetic test suite, with an opt-in live smoke test

## Context

Whether tests (and CI) should ever call the real CVR / Statbank APIs:

1. **Hermetic only, no live tests** — mocks and WireMock.Net stubs only. Simplest, fastest CI, but
   nothing ever verifies the real upstream contracts still match what the adapters assume.
2. **Include live calls in CI** — CI hits the real public APIs on every run. Genuinely verifies
   the integration, but makes builds flaky and dependent on third-party uptime and rate limits —
   a failing PR check because `apicvr.dk` is briefly down is a false signal.
3. **Fully hermetic, plus an opt-in live smoke test** — unit tests mock `HttpMessageHandler`
   directly; integration tests use WireMock.Net stubs with `WebApplicationFactory`; a separate
   test class tagged `[Trait("Category","Live")]` calls the real APIs but is excluded from the
   default filter and only runs on demand.

## Decision

Fully hermetic CI (`dotnet test --filter "Category!=Live"`), plus a manually-triggered
`workflow_dispatch` live smoke workflow (`live-smoke.yml`) that runs the `Category=Live` tests
against the real APIs. A failure there means the upstream contract drifted, not that the code
regressed, which is exactly why it must never gate a PR.

## Consequences

- CI is deterministic and works offline — no dependency on `apicvr.dk` or `api.statbank.dk` being
  up for a merge to go green.
- This is how the three live-API discrepancies documented in the README were actually found:
  probing the real endpoints before writing the adapters, not guessing from the task PDF alone.
- The trade-off is coverage: nothing in the default CI run proves the real APIs still match the
  adapters' assumptions today. That's an accepted gap — `live-smoke.yml` exists precisely to close
  it on demand (or nightly, if the cron trigger is left enabled) without coupling it to the merge
  gate.
