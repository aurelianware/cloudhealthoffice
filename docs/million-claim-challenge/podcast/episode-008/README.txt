# Episode 008: The Clean 100,000-Claim Run

## Episode summary

Episode 008 covers the first clean 100,000-claim local Kubernetes validation after payment accuracy, false-pend detection, accumulator handling, and fixture isolation became scored gates.

The result held: 100,000 processed, zero platform failures, zero scoreable workflow mismatches, zero unexpected pends, and 2,000 of 2,000 comparable payments within one cent. The episode also explains why the 29:40 timed processing phase and 128-minute Kubernetes job lifecycle are different, and why fixture preparation is now the clearest local scaling target.

The pend counts are intentionally reported with scope: 923 persisted pended outcomes overall, 920 expected-pend claims observed as pended, and zero unexpected pends across 10,614 scoreable expected-pay and expected-deny claims.

Post-#928 note: after the original 100K run, provider-fixture hardening moved scoreable validation providers into wider, role-separated synthetic NPI namespaces. The follow-up 50K verification completed cleanly with zero workflow mismatches, 460/460 expected pends observed, zero unexpected pends, and 1,000/1,000 payment comparisons within tolerance.

## Episode metadata

- Episode title: The Clean 100,000-Claim Run
- Source article: Running a Healthcare Claims Platform Locally in Kubernetes, Part 8: The Clean 100,000-Claim Run
- Published article: not yet published
- Target length: 10-12 minutes
- Preferred format: two-host engineering conversation
- Primary hosts: Alex and Jordan
- Status: draft

## Core message

The stronger correctness system reached 100K cleanly, and the run exposed the next honest bottleneck: local fixture preparation and rising tail latency.

The episode must distinguish:

- timed claim processing from total job lifecycle
- business denials from platform failures
- unsupported scenarios from mismatches
- local benchmark evidence from production capacity claims
- reference-data preparation cost from adjudication throughput

## Packet files

- `article.txt` - Part 8 article source.
- `benchmark-results.txt` - exact 100K run command, environment, gates, timing, and limitations.
- `pr-summary.txt` - implementation work that raised the correctness bar before 100K.
- `podcast-prompt.txt` - episode-specific generation prompt.
- `screenshots/episode-008-100k-dashboard.png` - completed 100K run list, run detail, timing, outcome mix, and claim-result evidence.
- `screenshots/episode-008-100k-outcome-breakdown.png` - business-denial distribution and explicit zero-platform-failure evidence.
- `screenshots/episode-008-100k-paid-results.png` - retained paid-claim evidence with payment amounts and per-claim stage timing.
- `screenshots/episode-008-100k-validation-summary.png` - completed 100K correctness gates, workflow scoring, performance, and run configuration.
- `screenshots/episode-008-100k-unsupported-results.png` - retained unsupported claims with human-readable MCC IDs, scenario names, observed outcomes, and latency.
- `screenshots/episode-008-claim-payment-breakdown.png` - persisted claim-level allowed amount, copay, member responsibility, and payer amount after the portal projection fix.
- `screenshots/episode-008-claim-detail-summary.png` - run-aware claim drilldown with human-readable MCC ID, disposition, corrected financial summary, and member/provider context.
- `screenshots/episode-008-benefit-breakdown.png` - fee-schedule projection, network tier, cost-share calculation, and plan/member amounts.
- `screenshots/episode-008-adjudication-pipeline.png` - persisted intake, benefit calculation, disposition, persistence, and NCCI/MUE evidence.
- `screenshots/episode-008-local-docker-resources.png` - Docker Desktop's 18-CPU and 22.88-GB local resource context.
- `screenshots/episode-008-scenario-scoring.png` - scenario-level matched, mismatched, unsupported, and timeout counts alongside denial and failure evidence.
- `screenshots/.gitkeep` - placeholder for console and environment screenshots.

## Acceptance checklist

- [x] Exact 100K results are recorded.
- [x] Payment and false-pend gates are explained.
- [x] Persisted-pend, expected-pend, and false-pend sweep scopes are separated.
- [x] Unsupported scenarios remain visible and separate.
- [x] Timed processing and total job duration are not conflated.
- [x] Local results are not presented as production capacity.
- [x] The next optimization target is evidence-driven.
- [x] Primary completed-run console screenshot is captured.
- [x] Unsupported claim sample and persisted payment drilldown are captured.
- [x] Post-#928 50K provider-fixture verification is recorded.
- [ ] Published article URL is recorded.
