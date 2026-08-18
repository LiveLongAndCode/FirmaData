# ADR-0007: Upgrade from .NET 8 to .NET 10

## Context

.NET 8 is in maintenance and reaches end-of-support on 10 November 2026. The solution needed to
move to a currently-supported LTS release before then. Two options were considered:

1. **.NET 9** — STS, itself already out of support before .NET 8's own end-of-support date, so it
   would only defer the problem.
2. **.NET 10** — the next LTS, supported until November 2028.

Separately, Microsoft has stopped including Swashbuckle in its own project templates from .NET 9
onward in favour of `Microsoft.AspNetCore.OpenApi`, the framework's own OpenAPI document
generator, with Swashbuckle repositioned as a UI-only consumer of that document.

## Decision

Upgrade to .NET 10. The upgrade was verified with a full trial migration (build + all 126 tests)
before being applied for real. Alongside the framework bump, OpenAPI document generation moved
from Swashbuckle's own generator to `Microsoft.AspNetCore.OpenApi` (`AddOpenApi`/`MapOpenApi`),
keeping only `Swashbuckle.AspNetCore.SwaggerUI` for the browsable UI.

## Consequences

- No changes were required in Domain, Application, or either adapter (CVR/Statbank) — the upgrade
  was entirely build configuration and package versions, plus three narrow build breaks (NuGet
  package pruning, interface-member accessibility modifiers, and a transitive vulnerability in a
  test dependency) all triggered by this solution's `TreatWarningsAsErrors` policy rather than by
  the framework itself.
- The generated OpenAPI document is now 3.1.1 (previously Swashbuckle produced 3.0.x) — a contract
  change for any consumer generating a client from `/openapi/v1.json`. It can be pinned back to
  3.0 via `AddOpenApi(options => options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_0)` if that
  becomes necessary.
- Controller actions now carry explicit `[ProducesResponseType]` attributes so the generated
  document describes the actual error contract (400/404/503 with `ProblemDetails`), not just the
  200 happy path — previously true under Swashbuckle too, but only fixed as part of this move.
