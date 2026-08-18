# API reference

Base URL is `http://localhost:8080` under Docker, `http://localhost:5188` when run from source.
Swagger UI at `/swagger` is the authoritative, always-current contract; this page is the summary.

## Endpoints

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/v1/companies/{cvrNumber}` | Company lookup by CVR number, enriched with industry statistics. Optional `?year=` |
| `GET` | `/api/v1/companies?name=&limit=` | Search by company name, each result enriched. A name with no matches returns `200` with `[]`, not an error. Results are re-ranked locally (exact match, then prefix match, then the rest) and exclude bankrupt companies. `name` must be 2–100 characters (`400` otherwise); `limit` defaults to 10 and must be 1–25 (`400` otherwise) — see [configuration](configuration.md) for the underlying `Search:*` settings |
| `GET` | `/api/v1/metadata/years` | Years with available industry statistics — backs the frontend's year dropdown |
| `GET` | `/health/live` | Liveness — process is up |
| `GET` | `/health/ready` | Readiness — dependencies checked |
| `GET` | `/metrics` | Prometheus scrape endpoint |

### Example: successful lookup

`GET /api/v1/companies/16500836` — CVR data and Statbank enrichment both succeeded (`200 OK`):

```json
{
  "company": {
    "cvrNumber": "16500836",
    "name": "LB FORSIKRING A/S",
    "address": {
      "street": "Amerika Plads 15",
      "postalCode": "2100",
      "city": "København Ø"
    },
    "industryCode": "651200",
    "industryDescription": "Anden forsikring",
    "employeeCount": 1010
  },
  "industryStatistics": {
    "industryCode": "651200",
    "year": 2022,
    "workplaces": 166,
    "employees": 15206,
    "fullTimeEquivalents": 13458,
    "wageSumMillionDkk": 10380
  },
  "statisticsStatus": "Ok",
  "retrievedAt": "2026-08-18T09:12:00Z",
  "sources": {
    "company": "apicvr.dk",
    "statistics": "api.statbank.dk/ERHV1"
  }
}
```

## Error handling

Follows RFC 9457 (`ProblemDetails`). `Result<T>` propagates upward from the domain and is mapped
to HTTP status codes centrally:

| Error type | HTTP | Extra |
| --- | --- | --- |
| `Validation` | 400 | — |
| `NotFound` | 404 | — |
| `Unavailable` | 503 | `Retry-After: 30` |
| `Unexpected` | 502 | An upstream response could not be interpreted — a broken integration, not a genuine crash. Distinct from an unhandled exception, which is still a plain 500. |

Every error response carries the request's correlation id, matching the one on the log lines for
that request: it comes back on the `X-Correlation-Id` response header, and again as
`correlationId` in the JSON body.

### Example: error response

`GET /api/v1/companies/12345678` where the CVR API has no company for that number (`404 Not Found`):

```json
{
  "status": 404,
  "title": "NotFound",
  "detail": "No company found for CVR number 12345678.",
  "instance": "/api/v1/companies/12345678",
  "correlationId": "a1b2c3d4e5f64789a1b2c3d4e5f64789"
}
```

A malformed CVR number (`GET /api/v1/companies/123`) fails the same way but as `400 Validation`,
with `detail` set to one of `CVR number is required.`, `CVR number must be exactly 8 digits.`, or
`CVR number failed the modulus-11 checksum.`, depending on which check rejected the input.

## Partial success is not failure

A failure in the *enrichment* source (Danmarks Statistik) never brings the whole response down.
The API still returns HTTP 200; the response body is self-describing via `StatisticsStatus` and a
`null` `IndustryStatistics`, which is what every consumer in this repo actually reads. For a
client that only wants a cheap header-level signal without parsing the body, the same failure
also sets `FirmaData-Degraded-Source: statbank` — a plain custom header, not the HTTP `Warning`
header, which RFC 9111 removed from the spec.

CVR is the core source; statistics are an enrichment. A CVR failure is a real failure; a Statbank
failure is a degraded response.

`statisticsStatus` is one of:

| Value | Meaning |
| --- | --- |
| `Ok` | Statistics were retrieved for the requested (or resolved) year |
| `NotAvailableForYear` | The requested year has no data in Statbank's ERHV1 table |
| `IndustryCodeNotSupported` | The industry code is valid but ERHV1 (DB07) doesn't recognise it — e.g. a code from CVR's newer DB25 revision. See [gotchas](gotchas.md) |
| `SourceUnavailable` | Statbank itself couldn't be reached or returned an unparseable response |

### Example: degraded response

`GET /api/v1/companies/16500836` where Statbank is unreachable — still `200 OK`, with response
header `FirmaData-Degraded-Source: statbank`:

```json
{
  "company": {
    "cvrNumber": "16500836",
    "name": "LB FORSIKRING A/S",
    "address": {
      "street": "Amerika Plads 15",
      "postalCode": "2100",
      "city": "København Ø"
    },
    "industryCode": "651200",
    "industryDescription": "Anden forsikring",
    "employeeCount": 1010
  },
  "industryStatistics": null,
  "statisticsStatus": "SourceUnavailable",
  "retrievedAt": "2026-08-18T09:12:00Z",
  "sources": {
    "company": "apicvr.dk",
    "statistics": "api.statbank.dk/ERHV1"
  }
}
```
