# ADR-0008: Strict Statbank CSV validation, and Unexpected mapped to 502

## Context

`StatbankClient.ParseCsvAsync` split each response row on `;` and wrote positionally
(`values[columns[1]] = columns[3]`) without checking that a row actually answered the request
that was sent: `BRANCHE07`/`TID` weren't validated against the requested industry code and year,
duplicate rows for the same measure were accepted silently, and a missing measure row read as
`null` via `GetValueOrDefault` — indistinguishable from Statbank's own `".."` suppressed-value
marker.

Separately, `ParseNullableLong`/`ParseNullableDecimal` were called *outside* the `try`/`catch
(FormatException)` that exists specifically to catch them. Two failure modes followed: an
unexpected format in the three integer measures threw an unhandled `FormatException`, mapped by
`GlobalExceptionHandler` to a generic 500; and `LØNSUM`'s `NumberStyles.Number` (which includes
`AllowThousands`) read a decimal comma such as `1234,5` as a thousands separator under
`InvariantCulture`, silently producing `12345` — a factor-10 error presented as fact, worse than
an outright failure.

Both bugs shared one root cause: nothing at the API boundary distinguished "the upstream response
couldn't be interpreted" from "the upstream response is malformed C# code" (`Unexpected` mapped
to the same 500 as `GlobalExceptionHandler`'s own unhandled exceptions), so there was no
controlled error to fall back into even once the parsing bugs were fixed.

## Decision

1. Validate every CSV row against the request: reject a row whose `BRANCHE07` or `TID` doesn't
   match what was asked for, reject a duplicate row for the same `TAL` measure, and require all
   four requested measures to be present. Read columns by header name, not hardcoded index, so a
   column reordering upstream is detected rather than silently misread.
2. Move all four measure-parsing calls inside the existing `try`/`catch (FormatException)`.
3. Tighten `LØNSUM`'s `NumberStyles` to `AllowDecimalPoint | AllowLeadingSign` (dropping
   `AllowThousands`), so a decimal comma is rejected as a format error instead of being silently
   misread. No `,` → `.` normalisation is applied — guessing the comma's meaning would just
   reintroduce the same ambiguity with the sign reversed.
4. Only Statbank's explicit `".."` marker maps to `null`; an empty cell is now a parse failure,
   not a suppressed value.
5. Extend `ResultErrorMapping`: `Unexpected` → 502 Bad Gateway (previously fell into the same
   `_ => 500` branch as `GlobalExceptionHandler`'s genuinely unhandled exceptions). Every
   `Unexpected` result originates from an upstream response that couldn't be interpreted, which is
   a broken integration, not this service's own fault — 502 is the correct RFC 9110 semantics for
   that. Controllers' `[ProducesResponseType]` sets were updated to document the new status.

## Consequences

- Outward-facing change: responses that previously came back as a plain `500` (unhandled parse
  exception) or silently degraded data (`LØNSUM` misread) now come back as a controlled `502`.
  Any consumer that branches on status code must handle `502` alongside `503`; both mean "try
  again later, this isn't a client error", but only `503` carries `Retry-After`.
- Statbank's actual `LØNSUM` decimal separator was not re-confirmed live as part of this change
  (see the live smoke workflow, plan exit criterion 6). If it turns out to be a comma rather than
  a period, the fix is an explicit `da-DK` parse, not re-adding `AllowThousands`.
- No change to the CSV parsing library: `StreamReader` is kept rather than adding `CsvHelper` (as
  the lb2 comparison project does) — the format is unquoted and semicolon-separated, and a new
  dependency in an otherwise dependency-free adapter isn't worth it for that.
