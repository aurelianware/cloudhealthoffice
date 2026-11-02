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
| 1 | Corpus generator and answer key, in-process preflight test | #1778 |
| 2 | `run`: import history, submit live cases through PAS `$submit`, act as reviewer, score the reports | #1780 |
| 3 | Container image and GKE runner script (`scripts/demo/gcp/run-pa-timeliness.sh`) | This PR |
| 4 | Evidence page with the first real GKE result | After the first run |

## Generate a corpus

```bash
dotnet run --project src/tools/pa-timeliness-challenge -- generate \
  --out ./ptc-corpus --count 10000 --seed 57 --as-of 2026-11-02T00:00:00Z
```

Options: `--period-start`, `--period-end` (history receipt window; default the
whole calendar year before as-of, the year a plan reports on; it must end at
least 60 days before as-of), `--live-fraction`
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

## Run it against a stack

```bash
export CHO_BEARER_TOKEN="$(TENANT_ID=pa-timeliness python3 scripts/demo/gcp/cho_demo.py token)"
dotnet run --project src/tools/pa-timeliness-challenge -- run --out ./ptc-run \
  --auth-url http://localhost:5006 --fhir-url http://localhost:5023 --tenant pa-timeliness \
  --count 10000 --seed 57
```

On the GKE demo, run it inside the cluster instead (next section), or run
`scripts/demo/gcp/port-forward.sh` first and point the URLs at localhost. The token's tenant
must match `--tenant`. A dedicated tenant (the default, `pa-timeliness`) keeps the
score clean: the metrics endpoints count every request in the tenant, so the run
checks the history period before it loads anything and says so if the tenant
already holds other requests there. A rerun with the same seed is idempotent
(the import reports them unchanged, the facts are keyed by event id).

## Run it on the GKE demo

The deploy workflow builds the `pa-timeliness-challenge` image beside the
service images. `run-pa-timeliness.sh` mints a token for the run's tenant into
a short-lived Secret, runs the tool as a Kubernetes Job against the in-cluster
services, and copies the job log, `run-summary.json` and `run-summary.md` to
`./pa-timeliness-results/<timestamp>/`:

```bash
GAR_REPOSITORY=<vars.GAR_REPOSITORY> IMAGE_TAG=<deployed tag> ./scripts/demo/gcp/run-pa-timeliness.sh
```

Optional: `TENANT` (default `pa-timeliness`), `COUNT` (10000), `SEED` (57),
`LIVE_FRACTION` (0.05), `PARALLELISM` (8), `MODE` (`all`, `history-only`,
`live-only`), `JOB_TIMEOUT` (45m). It exits 0 when every check passed, 1 when
one failed, and 124 on timeout.

What `run` does, in order:

1. **History.** Imports `authorizations.csv`, records the extension and appeal facts,
   then scores `GET api/authorizations/cms-0057/metrics` (all lines of business and
   each one), every request's on-time and extension flags in the UM report
   `um-decision-timeliness` (in 7-day windows, under its 1,000-row cap), and
   `cms-0057/annual-draft` per line of business and receipt year.
2. **Live.** Submits each live case through PAS `Claim/$submit` (no service state,
   so state-specific rules pend it), reads it back by its `preAuthRef`, takes the
   scripted extension (`decision-extension`: 200 on Medicare Advantage and Medicaid,
   409 on Commercial), records the reviewer's decision with line decisions
   (`/response`), and reads it back. Each case scores line of business, urgency,
   outcome, per-line denial reasons and the extension behavior. A case a rule
   decided on submit is reported as not reviewable rather than scored as the
   reviewer's outcome. Then the live window's metrics are checked against what
   CHO recorded for those requests (drug requests out, arithmetic right).

It writes `run-summary.json`, `run-summary.md` and the corpus under `--out`,
and exits 0 when every check passes, 1 when any fails, 2 when it could not run.
`--history-only` and `--live-only` run one phase; `--parallelism` (default 8)
bounds concurrent requests.

PAS has no claim filing code for an Exchange (QHP) plan, so live Exchange
requests are sent as `CI` and CHO records them as Commercial. The run expects
that and says so in its notes.

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

## Tests

- **Preflight** (below): the answer key against CHO's reporting code, in process.
- **Contract**: every live request passes fhir-service's real PAS compliance check
  and `PasAuthorizationMapper` (member, NPI, urgency, lines, line of business).
- **End to end over HTTP**: the history phase and the live phase run against the
  real authorization-service pipeline (authentication, permissions, import, facts,
  extension, 278 response, metrics, UM report, annual draft) with an in-memory
  store. Building them found two contract errors in the runner before any deployed
  run: authorization-service reads enums by name only, and the UM report id is
  `um-decision-timeliness`.

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
