# ADR-0004: Frontend calls the API over HTTP, not by direct reference

## Context

The task asks for an ASP.NET Core MVC frontend with an easy-to-use UI for looking up company
data. Two ways for `FirmaData.Web` to get that data:

1. **Direct project reference** — the MVC project references `FirmaData.Application` directly and
   calls `CompanyEnrichmentService` in-process. Fewer moving parts, no network hop.
2. **HTTP call to the API** — `FirmaData.Web` calls `FirmaData.Api`'s public REST surface through
   a typed `HttpClient`, exactly as any external consumer would.

## Decision

HTTP call to the API. `FirmaDataApiClient` in `FirmaData.Web` talks to `FirmaData.Api` over HTTP,
bound to `ApiOptions.BaseUrl` (`http://firmadata-api:8080/` inside `docker-compose`,
`http://localhost:5188/` for local dev without Docker).

## Consequences

- The frontend is treated as the first consumer of the API, not a privileged insider — the same
  contract, serialization, and error shapes (`ProblemDetails`) that any external caller would see
  are the ones the UI actually exercises.
- This is the thing that makes `docker compose up --build` a genuine two-container, over-the-network
  demo rather than a single process wearing two hats.
- Cost: an extra network hop and a second place to configure a base URL and timeout, and the UI's
  own availability now depends on the API being reachable — handled with a friendly Danish error
  page and a correlation-id echo rather than a raw exception (§15 of the build plan).
