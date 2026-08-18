# ADR-0001: Modular monolith over microservices

## Context

The task asks for "a project for each service (CVR, Statbank)". Two topologies satisfy that
literally:

1. **True microservices** — the CVR adapter, the Statbank adapter, an aggregator API, and the
   MVC site each become their own deployable ASP.NET Core Web API with its own Dockerfile: four
   containers, network hops between them, and independent failure modes for each hop.
2. **Modular monolith** — one deployable API (`FirmaData.Api`) that orchestrates, with
   `FirmaData.Cvr` and `FirmaData.Statbank` as separate class library projects (their own
   `.csproj`, their own test projects, no shared internals) referenced by the API process.

## Decision

Modular monolith. `FirmaData.Cvr` and `FirmaData.Statbank` are separate projects with their own
tests, satisfying "a project for each service" in the project structure, but they run inside one
deployable unit alongside `FirmaData.Application` and `FirmaData.Api`.

## Consequences

- The demo runs as two application containers (`firmadata-api`, `firmadata-web`) plus Prometheus
  and Grafana — fits comfortably in a one-day scope and a 30-minute conversation.
- The service boundary is enforced mechanically instead of physically: `FirmaData.Application`
  depends only on `ICompanyDirectory` / `IIndustryStatisticsProvider`, never on
  `FirmaData.Cvr`/`FirmaData.Statbank` concretely, and `ArchitectureTests` (NetArchTest) fails the
  build if that boundary is crossed.
- Splitting `FirmaData.Cvr` or `FirmaData.Statbank` into its own deployable service later is a
  matter of adding a Dockerfile and an HTTP-facing host around the existing adapter — the
  contracts at the port boundary do not change.
- What this gives up: no independent scaling or independent deployment of the two adapters, and
  no network-partition failure mode between "API" and "CVR service" to demonstrate — the modular
  monolith cannot fail exactly the way a real microservice split would.
