# Benchmarks

CloudHealthOffice benchmark documentation is centered on the Million Claim
Challenge (MCC). MCC is a proof ladder for claims-processing correctness,
observability, and local Kubernetes performance.

The benchmark is not only a throughput test. It separates:

- Paid claims.
- Business denials.
- Expected pends.
- Unexpected pends.
- Platform failures.
- Scoreable workflow matches and mismatches.
- Unsupported scenarios.
- Payment comparisons and deltas.
- Lifecycle timing and fixture preparation cost.

## Current Evidence

All results below are **local Docker Desktop Kubernetes** runs. They are engineering
benchmarks, not production-cloud capacity claims. Throughput figures are hardware-dependent
and will not reproduce exactly elsewhere; the correctness gates are seed-reproducible.

**Canonical current result — correctness baseline (episode 15, run 2):**

| Measure | Result |
| --- | --- |
| Corpus | 1,000,000 claims |
| Processed | 1,000,000 |
| Platform failures | **0** |
| Workflow checks | 129,981/130,000 matched (19 mismatched, 0 unsupported, 0 observation timeouts) |
| Payment gate | 20,000/20,000 exact within $0.01 |
| Throughput | 123.81 claims/sec |

**Highest sustained throughput (episode 16), same corpus:**

| Measure | Result |
| --- | --- |
| Throughput | 155.89 claims/sec (P95 910 ms, P99 1,205 ms) |
| Workflow checks | 129,980/130,000 matched |
| Payment gate | 19,982/19,982 exact within $0.01 |
| Observation timeouts | 122 — claims that became terminal *after* the validator's 180-second window |

The 122 are an **observation deadline, not lost claims**: post-run verification found all
1,000,000 claims terminal, 2,000,000 lifecycle events, zero dead letters, zero pod
restarts. They still produced a nonzero validator exit and left 20 workflow checks and 18
payment scenarios unreconciled *inside the run artifact*, which is why episode 15 — not
16 — is cited as the strict baseline. Episode 017 adds automatic post-window
reconciliation so a delayed terminal outcome is re-scored without manual MongoDB
inspection.

**Earlier rungs of the ladder:**

| Evidence | Result |
| --- | --- |
| 50K breadth validation | Zero scoreable workflow mismatches, pended-claim observation, unsupported scenarios separated |
| 100K local Kubernetes run | 100,000 processed, zero platform failures, zero scoreable workflow mismatches, zero unexpected pends, 2,000/2,000 comparable payments within one cent |
| Operator console | Run summaries, claim drilldown, lifecycle timing, fixture preparation evidence, payment evidence filters |

> The separate 100,000-claim **raw X12 837P** result proves parser-to-persistence plumbing
> and throughput. Because it deliberately repeats one COB-secondary fixture, it is **not**
> diverse adjudication-correctness evidence — do not cite it as such.

Start with:

- [Episode 015 benchmark results](../million-claim-challenge/podcast/episode-015/benchmark-results.txt) — canonical 1M correctness baseline
- [Episode 015 raw evidence](../million-claim-challenge/podcast/episode-015/raw-evidence-1m-runs.txt)
- [Episode 016 benchmark results](../million-claim-challenge/podcast/episode-016/benchmark-results.txt) — throughput high-water
- [Episode 008 article](../million-claim-challenge/podcast/episode-008/article.txt) — 100K run narrative
- [Pended-claim validation](../million-claim-challenge/pend-validation.md)

## Reproducibility

Each benchmark packet should include:

- Commit SHA.
- Commands.
- Environment description.
- Claim count and parallelism.
- Seed and corpus profile.
- Timed phase results.
- Total job lifecycle timing.
- Validation outcome counts.
- Unsupported breakdown.
- Raw output or enough structured evidence to audit the summary.

## Hardware And Environment

Published local results have used Docker Desktop Kubernetes on a developer
workstation. These are local engineering results, not production cloud capacity
claims. When publishing a new result, include CPU, memory, Kubernetes context,
claim count, parallelism, and whether the tenant was fresh or long-lived.

## Metrics

| Metric | Meaning |
| --- | --- |
| Claims/sec | Timed claim-processing throughput, not always total job throughput |
| P95/P99 latency | Tail latency for processed claims in the timed phase |
| Platform failures | System failures, not valid business denials |
| Business denials | Correct or incorrect claim dispositions that deny for a business reason |
| Workflow checks | Answer-key checks for scoreable scenarios |
| Unsupported | Scenarios not honestly scoreable through the current validation path |
| Payment delta | Difference between actual and expected plan payment for comparable paid claims |
| Lifecycle timings | Preparation, processing, observation, and diagnostics phase durations |

## Limitations To Preserve

Do not collapse limitations into green numbers:

- Local Docker Desktop results are not production capacity claims.
- Unsupported scenarios are roadmap items, not successes.
- Payment accuracy is a separate gate from disposition correctness.
- Expected-pend observation does not replace a false-pend sweep across non-pend
  claims.
- Fixture preparation time and timed processing throughput answer different
  questions.

## Adding A New Benchmark Packet

Create a new folder under `docs/million-claim-challenge/podcast/episode-NNN/`
with:

- `article.txt`
- `benchmark-results.txt`
- `podcast-prompt.txt`
- `pr-summary.txt`, when relevant
- `screenshots/`
- raw output files, when safe and useful

Keep all data synthetic.
