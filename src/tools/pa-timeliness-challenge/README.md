# PA Timeliness Challenge

A CMS-0057-F counterpart to the Million Claim Challenge. The MCC proves claims
adjudication against a corpus whose right answers are known in advance. This
does the same for prior-authorization decisions and the CMS-0057-F metrics a
plan publishes: how many requests were approved and denied, how long decisions
took, and how many were decided inside the 72-hour or 7-day timeframe.

All data is synthetic. Nothing here is a CMS attestation.

## Status

| Part | What | State |
|---|---|---|
| 1 | Corpus generator and answer key (this tool), in-process preflight test | This PR |
| 2 | Validator: import history, submit live cases through PAS `$submit`, act as reviewer, score the reports | Next |
| 3 | GCP runner script and evidence page | After |

## Generate a corpus

```bash
dotnet run --project src/tools/pa-timeliness-challenge -- generate \
  --out ./ptc-corpus --count 10000 --seed 57 --as-of 2026-11-02T00:00:00Z
```

Options: `--period-start`, `--period-end` (history receipt window; default
1 January of the as-of year to 60 days before as-of), `--live-fraction`
(default 0.05), `--expedited-fraction` (default 0.25). The same options give
the same files, byte for byte. Without `--as-of` the corpus is pinned to the
current time, so pass it for a reproducible run.

Output:

| File | Use |
|---|---|
| `history/authorizations.csv` | The history cohort in CHO's authorization import format: `POST api/v1/authorizations/import/csv`. One row per service line. |
| `history/reporting-facts.jsonl` | Sourced extension and appeal facts to record after the import: `POST api/authorizations/cms-0057/{id}/facts`. Keyed by authorization number. |
| `live/requests.jsonl` | The live cohort: one case per line for the validator to submit through PAS and decide. |
| `answer-key.json` | Every case's expectation, and the expected metrics for the history cohort (all lines of business, then each). |

## Two cohorts

**History** (95% by default) is loaded through the import with the source's
receipt and decision times. That is the only way to score a full reporting
year, and decisions that land hours or days late, without waiting on a real
clock: authorization-service reads the system clock directly, so a live run
cannot be fast-forwarded.

**Live** (5%) goes through PAS `$submit`. The validator acts as the reviewer.
Timing is observed, not scripted, so live cases score outcomes, denial reasons,
drug exclusion and the extension rules, and their measured decision times are
fed into the metrics afterwards.

## Scenarios

| Scenario | Cohort | Expected |
|---|---|---|
| Approved, Denied, PartiallyApproved | both | Outcome, with a specific reason on every denied line |
| LateDecision | history | Decided 2 to 72 hours after the deadline: late |
| DecidedAtDeadline | history | Decided at exactly 72 or 168 hours: on time (the deadline is inclusive) |
| DecidedOneMinuteLate | history | 72 or 168 hours plus one minute: late |
| ExtendedDecidedInExtension | both | Medicare Advantage or Medicaid, extended 14 days before the deadline, decided inside it: on time and extended |
| ExtendedDecidedLate | history | Extended, then decided after the extended deadline: late |
| ExtensionRefusedForLineOfBusiness | live | Commercial or Exchange: CHO must refuse the extension |
| DrugExcluded | both | NDC or HCPCS J-code: left out of every metric |
| Undecided | both | Counted as a request, not as a decision |
| DeniedThenApprovedOnAppeal | history | Denied on time, approved on appeal: counts as denied initially and as approved after appeal |

## How the answer key is computed

The key never calls CHO code. The tool has no reference to authorization-service,
so a defect in CHO's reporting cannot also hide in the key. The timeframes come
from the rule (`PaTimeframes.cs`): 72 hours expedited, 168 hours standard, one
extension of up to 14 days on Medicare Advantage and Medicaid only. The metric
definitions are in `PaExpectedMetrics.cs`: drugs out of scope, undecided requests
in the denominator, approved-with-changes counted as approved, decision time from
receipt to the initial determination, and "within timeframe" judged against the
request's own deadline.

Percentages and hours are rounded to one decimal place, half away from zero. CHO
rounds half to even, so a value at an exact midpoint can differ by 0.1. The
preflight test allows that one step, and counts must match exactly.

## Preflight

`tests/CloudHealthOffice.PaTimelinessChallenge.Tests` loads the history cohort
through CHO's real `AuthorizationImportService`, records the facts through
`PaReportingEvidence.Prepare` (the endpoint's validator), computes
`Cms0057Reporting.ComputeMetrics` and compares every metric with the answer key,
for all lines of business and for each one. It runs on every PR in Test .NET.

Its first run found a real defect: an imported Medicare Advantage or Medicaid
request extended in the source system was counted as extended but judged against
its original deadline, so a decision inside the extension counted as late. About
7 points of on-time rate were missing for the default mix. Fixed in the same
change (`Cms0057Reporting.ReportingDeadline`).
