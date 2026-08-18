# ADR-0006: Danish UI language, English codebase and API contract

## Context

What language the MVC user interface should be in:

1. **English** — consistent with the codebase, but reads as a mismatch against Danish domain
   terms like *branchekode* and *lønsum* that have no natural English equivalent in this
   context.
2. **Danish** — UI labels in Danish (*Virksomhed*, *Branchekode*, *Antal ansatte*, *Lønsum*),
   matching both LB Forsikring's own product language and the Danish source data.

## Decision

Danish UI. Code, identifiers, comments, commit messages, this README, and the API contract
(JSON field names, route segments) stay in English; only what a Danish end user reads on screen —
labels, validation messages, the API-unavailable error page — is in Danish.

## Consequences

- The UI reads naturally to the audience it's actually built for, instead of forcing English
  labels onto Danish statistical concepts (*antal arbejdssteder*, *fuldtidsbeskæftigede*) that
  don't translate cleanly.
- The split is a hard line, not a judgment call per string: anything inside `FirmaData.Api`'s
  contract or any project's source stays English; anything rendered by `FirmaData.Web`'s Razor
  views is Danish. `DataAnnotations` validation messages ("Indtast et gyldigt CVR-nummer på 8
  cifre") follow the same rule.
- No `IStringLocalizer`/resource files — a second UI language was never a requirement, so full
  localisation infrastructure would be speculative. If an English UI is ever needed, that's the
  documented next step.
