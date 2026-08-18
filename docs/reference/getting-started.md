# Getting started

## Prerequisites

| For | You need |
| --- | --- |
| The Docker stack | Docker Desktop (or any Docker engine with Compose v2) |
| Running from source | .NET SDK — the version pinned in [`global.json`](../../global.json) |
| The circuit-breaker harness | Python 3, plus the Docker stack |

## With Docker (recommended)

From the repository root:

```bash
docker compose up --build
```

| Service | URL |
| --- | --- |
| UI (search a company) | http://localhost:8090/ |
| Swagger UI | http://localhost:8080/swagger |
| Prometheus | http://localhost:9090/ |
| Grafana dashboard | http://localhost:3000/ |

Compose runs both containers with `ASPNETCORE_ENVIRONMENT=Development` so Swagger UI is reachable
without extra steps, and points the frontend at the API by its service name
(`Api__BaseUrl=http://firmadata-api:8080/`). Grafana is provisioned from [`ops/grafana/`](../../ops/grafana/)
with anonymous viewer access, so the dashboard opens without a login. The web container waits for
the API's `/health/ready` check before starting.

Stop it again with `docker compose down`.

### Windows launchers

* [`web/run.bat`](../../web/run.bat) starts the same stack, waits for the API to report healthy, and
opens all four URLs in the browser.
* [`web/shutdown.bat`](../../web/shutdown.bat) runs
`docker compose down --remove-orphans`.

Both are thin wrappers over the scripts in
[`web/PowerShell/`](../../web/PowerShell/) and can be double-clicked from Explorer.

## From source, without Docker

```bash
# API — listens on http://localhost:5188, Swagger UI opens automatically at /swagger
cd solution/src/Backend/FirmaData.Api
dotnet run
```

```bash
# Web — listens on http://localhost:5074, points at the API above via
# appsettings.Development.json (Api:BaseUrl = http://localhost:5188/)
cd solution/src/Frontend/FirmaData.Web
dotnet run
```

Prometheus and Grafana are Docker-only; running from source still exposes raw metrics at
`http://localhost:5188/metrics`.

## Sample requests

CVR `16500836` is LB Forsikring A/S itself — used throughout the test suite as a real fixture.

```bash
# Company lookup by CVR number, enriched with 2022 industry statistics
curl -s "http://localhost:8080/api/v1/companies/16500836?year=2022"

# Search by name
curl -s "http://localhost:8080/api/v1/companies?name=LB%20Forsikring"

# Years with available industry statistics (used for the frontend's year dropdown)
curl -s "http://localhost:8080/api/v1/metadata/years"

# Health checks
curl http://localhost:8080/health/live
curl http://localhost:8080/health/ready
```

Responses come back as one unformatted line. Three ways to read them comfortably:

* **Swagger UI** at http://localhost:8080/swagger formats every response for you, and needs no
  tooling at all — the quickest option.
* **PowerShell** parses and re-indents JSON on its own:

  ```powershell
  Invoke-RestMethod "http://localhost:8080/api/v1/companies/16500836?year=2022" | ConvertTo-Json -Depth 10
  ```

  Note that in Windows PowerShell, `curl` is an alias for `Invoke-WebRequest`, not curl — use
  `curl.exe` if you want the real thing.
* **`| jq`** on the commands above, if you have [jq](https://jqlang.github.io/jq/) installed.

Full endpoint and error semantics: [API reference](api.md).

## Tests

```bash
dotnet test solution/FirmaData.sln --filter "Category!=Live"
```

See [testing](testing.md) for the layers, the live smoke tests, and the circuit-breaker harness.
