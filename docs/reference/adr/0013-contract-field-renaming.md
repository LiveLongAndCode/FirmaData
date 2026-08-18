# ADR-0013: Rename ambiguous contract fields in place, no `/api/v2`

## Context

`IndustryStatisticsDto.Employees` is Statbank's ERHV1 `ANSATTE` — jobs across the whole industry,
end of November — while `CompanyDto.EmployeeCount` is the individual company's own headcount from
CVR. Both appear in the same `EnrichedCompanyResponse`, and nothing in the field names told a
consumer they aren't comparable. `IndustryStatisticsDto.Workplaces` had the same problem in
miniature: the DTO's own doc comment described it as "number of companies (workplaces)", but it's
`ARBSTED` — establishments in the industry, not a count of companies. Separately,
`EnrichedCompanyResponse.RetrievedAt` didn't say what it was a snapshot *of* — company master
data is current as of that instant, but `IndustryStatistics` reflects whichever `year` was
requested, which can be well in the past. The two follow different clocks entirely, and the name
didn't hint at that.

This is the plan's one intentionally breaking change (fase 8, F6), deliberately sequenced last so
it wouldn't block the other eight improvements. The plan left one question open for the user: is
this API public enough that the rename needs `/api/v2` alongside an unchanged `v1`, or can the
fields be renamed directly in `v1`?

## Decision

**Renamed directly in `v1` — no `/api/v2`.** The user's call (2026-08-18): the API isn't
public/documented as an external contract yet, and the only known consumer is this project's own
`FirmaData.Web`, which shares the `FirmaData.Contracts` project directly — the compiler catches
every call site, and it did (a clean rebuild after the rename surfaced zero missed references).

Renames:

- `IndustryStatisticsDto.Workplaces` → `WorkplacesEndNovember`
- `IndustryStatisticsDto.Employees` → `JobsEndNovember`
- `EnrichedCompanyResponse.RetrievedAt` → `RetrievedAtUtc`

`FullTimeEquivalents` and `WageSumMillionDkk` were already unambiguous and are unchanged. The
matching Domain type (`IndustryStatistics`), the API-layer mapping
(`EnrichedCompanyMapping.ToDto`), and the frontend's own view model
(`CompanyDetailViewModel.WorkplacesDisplay`/`EmployeesDisplay` →
`WorkplacesEndNovemberDisplay`/`JobsEndNovemberDisplay`) were renamed to match, so the "which
`Employees` is this" question doesn't resurface one layer down.

## Consequences

- Breaking change to the JSON wire contract: any consumer parsing `workplaces`, `employees`, or
  `retrievedAt` by name needs to update. There is exactly one such consumer today
  (`FirmaData.Web`), already updated in the same change.
- If the API is opened up to external consumers later and this decision needs revisiting, the
  renamed fields are now the stable names going forward — there's no further rename debt sitting
  behind this one.
- The rendered Danish UI text (`Views/Companies/Details.cshtml`) was left as-is: "Antal
  arbejdssteder" / "Antal ansatte i branchen" under the "Branchestatistik for {år}" heading, next
  to "Antal ansatte" under "Stamdata", already read as distinct to a user — the ambiguity this ADR
  fixes was in the wire contract's field names, not the rendered labels.
