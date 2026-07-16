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

| Evidence | Result |
| --- | --- |
| 50K breadth validation | Zero scoreable workflow mismatches, pended-claim observation, unsupported scenarios separated |
| 100K local Kubernetes run | 100,000 processed, zero platform failures, zero scoreable workflow mismatches, zero unexpected pends, 2,000/2,000 comparable payments within one cent |
| Operator console | Run summaries, claim drilldown, lifecycle timing, fixture preparation evidence, payment evidence filters |

Start with:

- [Episode 008 article](../million-claim-challenge/podcast/episode-008/article.txt)
- [Episode 008 benchmark results](../million-claim-challenge/podcast/episode-008/benchmark-results.txt)
- [Episode 008 PR summary](../million-claim-challenge/podcast/episode-008/pr-summary.txt)
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
