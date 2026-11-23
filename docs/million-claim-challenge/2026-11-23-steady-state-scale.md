# Steady-state scale: why 1M runs looked slower, and how to prove it in the cloud

_November 23, 2026_

## Summary

- The Million Claim Challenge's 1M-claim runs (124 and 156 claims/sec in July
  2026) and the 20K burst (363 claims/sec) were mostly measuring the
  **validator**, not the platform. The validator re-read each submitted claim's
  status on a fixed 1,000 ms poll while holding one of at most 96 workers. A
  claim adjudicated in 200 ms still held its worker for a second, so measured
  throughput could not rise much above the worker count per second. The poll
  now starts at 50 ms and doubles up to the interval. On the same sandbox and
  load, measured throughput went from 13.7 to 43.4 claims/sec with no platform
  change.
- A real platform slowdown with data size was found and fixed: the
  accumulator outbox dispatcher's "oldest pending entry" query (added in
  October 2026, so not the cause of the July numbers) could use no index and
  scanned the whole Claims collection twice per 15-second poll, per
  claims-service replica. At 1M stored claims each scan took about 1.2 s on the
  sandbox (4.7 s on 1M realistic documents in a cold test). It now uses
  sparse indexes and examines 0 to 3 documents.
- With both fixes, a 30K-claim soak on a database already holding 1M claims
  runs at the same flat rate as on an empty one. The 500K and 1M rungs can now
  run unattended on GKE with one workflow and cost a few dollars.

These are engineering findings from a small sandbox. The cloud 500K and 1M runs
below are the evidence to cite once they have run.

## What was measured

All sandbox runs: 30,000 generated claims, 32 validator workers, the
validator's standard scenario mix, one claims-service replica, every service as
a host process on one 4 vCPU machine, MongoDB 7 with a 1 GB WiredTiger cache
and Redis in Docker, ASP.NET Development environment.

| Run | Claims already in MongoDB | Claims-service code | Overall rate | Steady windows (per minute) | P50 (median of windows) | P95 |
| --- | --- | --- | --- | --- | --- | --- |
| A | about 0 | fixed | 46.8 claims/sec | 43.1 first quarter, 49.4 last quarter | about 600 ms | 1,009 ms |
| B2 | 1,039,000 (4.9 GB) | fixed | 44.00 claims/sec | 41.1 first quarter, 45.4 last quarter | 588 ms | 1,033 ms |
| C | 1,039,000 (4.9 GB) | before the fix | 40.21 claims/sec | 39.2 first quarter, 39.0 last quarter | 911 ms | 1,102 ms |

- Every run: 0 platform failures and 0 observation timeouts. The 600 workflow
  mismatches in each run are the sandbox's provider-exclusion scenarios
  (`ExcludedProviderDenied`), which need the OIG and NPPES lookups the sandbox
  has no network access to. They are not a scale effect.
- The 1M filler claims are realistic 5.5 KB documents in another tenant, so
  the cross-tenant outbox polls see them, as production would.
- Run C's slower median comes from the full-collection scans competing with
  adjudication for the same CPU and cache. Production runs three
  claims-service replicas, so the old code would scan three times as often
  there.
- An earlier run B on the 1M database was disturbed by a second validator
  started by mistake for its first five minutes. It is left out; B2 is its
  clean rerun.

### What this does not show

- Absolute capacity. A 4 vCPU box with one replica of everything is far below
  the GKE demo node or a production deployment, and the rates above are bound
  by that box.
- Whether the July 1M runs also had a genuine data-size effect. They predate
  the current validator (Service Bus outcome observation, July 28), so their
  logs cannot separate the two. The GKE run below answers it with per-minute
  windows.

## Changes

| Change | Where |
| --- | --- |
| Poll backoff: first re-read at 50 ms, doubling to the interval (`--pend-observation-initial-interval-ms`) | `src/tools/mcc-platform-validator/MccObservationBackoff.cs` |
| Per-minute throughput and latency windows, a first-vs-last-quarter trend line, CSV (`--throughput-window-seconds`, `--throughput-windows-csv`) | `src/tools/mcc-platform-validator/MccThroughputWindows.cs` |
| Outbox age query uses `$exists` on sparse `accumulator_outbox_{commit,reversal}_created` indexes | `ClaimRepositoryMongo.OldestAccumulatorOutboxAsync`, `ClaimIndexInitializer` |
| Test that every outbox poll query is an index scan examining at most 3 documents | `tests/CloudHealthOffice.ClaimsService.Tests/HostedServices/OutboxPollQueryIndexTests.cs` |
| CMS 2023 synthetic claims as an optional corpus (`--cms-synthetic-dir`) | `src/tools/mcc-platform-validator/CmsSyntheticCorpus.cs` |
| One-click GKE run: node on, run, evidence artifact, node off | `.github/workflows/mcc-gke.yml` |

## The CMS synthetic corpus (volume only)

The workflow can run the validator's own generated scenarios (scored for
correctness, as in episodes 15 and 16) or CMS's public synthetic Medicare
claims.

- **Source:** Centers for Medicare & Medicaid Services, "Synthetic Medicare
  Enrollment, Fee-for-Service Claims, and Prescription Drug Event", 2023
  release, on data.cms.gov. 8,671 synthetic beneficiaries; about 1.12M carrier
  (professional), 575K outpatient and 58K inpatient claims, 2015 to early 2023.
- **Terms:** CMS publishes it for public use without restrictions. It is
  generated with Synthea and describes no real person. CMS asks that it not be
  used to draw conclusions about Medicare beneficiaries.
- **What it proves:** CHO ingests and adjudicates hundreds of thousands of
  claims with real-world shape (CMS's mix of claim types, lines per claim,
  HCPCS codes and modifiers, revenue codes, ICD-10 diagnoses, places of
  service, DRGs and charges) at a sustained rate. Anyone can reproduce it.
- **What it does not prove:** correctness or payment accuracy. The claims
  carry no expected outcome, so they score as Unspecified. CMS's user guide
  (section 4.4) says the payment amounts "do not closely model reality", so no
  historical fee schedules are loaded and no payment match rate is reported.
  It is Medicare fee-for-service, not a Medicaid plan's contracts.
- **How it is used:** the files are downloaded on the GitHub runner by
  `scripts/demo/gcp/fetch-cms-synthetic.sh`, which records each file's URL,
  size and SHA-256 in `cms-source.txt` in the run's evidence. The validator
  takes a seeded sample of each claim type in proportion. Each CMS
  beneficiary becomes one synthetic member with the file's birth date and sex,
  and each CMS provider number one synthetic provider with a valid NPI. Service
  dates are moved into recent weeks, as for generated claims.

## Running 500K and 1M on GKE

The workflow **Million Claim Challenge on GKE** (`.github/workflows/mcc-gke.yml`)
turns the demo node on, runs the challenge as a Kubernetes Job, uploads the
evidence and turns the node off again, also on failure or cancel.

1. Merge this change to `main` and let **Deploy demo to GKE** finish for that
   commit, so its validator and claims-service images exist.
2. Actions → **Million Claim Challenge on GKE** → Run workflow:
   - `claims` 500000, `corpus` cms-synthetic (or generated), `parallelism` 64.
3. When it has passed, run it again with `claims` 1000000.
4. The artifact `mcc-<corpus>-<claims>-<run id>` has `environment.txt`, the
   full log with a line per minute, the run summary and, for CMS runs,
   `cms-source.txt`.

**Cost:** the demo node is one e2-standard-8 at about $0.28 an hour. A 500K run
should take one to three hours of node time and a 1M run two to five (seeding
included), so under $2 each. GitHub Actions minutes are a hosted runner for the
same time. The job stops at 6 hours, and the node is turned off whatever
happens. The nightly 2 AM "demo off" waits for a running challenge.

## Results on GKE

_To be filled in from the workflow runs._

| Run | Corpus | Claims | Rate | Steady windows | P95 | P99 | Platform failures | Node hours |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| | | 500,000 | | | | | | |
| | | 1,000,000 | | | | | | |
