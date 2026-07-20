# Episode 008: The Clean 100,000-Claim Run

## Episode summary

Episode 008 covers the clean post-#934 100,000-claim local Kubernetes validation after payment accuracy, false-pend detection, accumulator handling, member fixture isolation, provider identity hardening, prior-auth scoring, and provider-exclusion label normalization became part of the scored path.

The result held: 100,000 processed, zero platform failures, zero scoreable workflow mismatches, zero unexpected pends, and 2,000 of 2,000 comparable payments within one cent. The episode also explains why the 30:05 timed processing phase and 38:05 tracked lifecycle are different, and why the next local scaling target is controlled parallelism and internal service/writeback pressure rather than raw Docker CPU or memory.

The controlled follow-up sweep is also recorded: p12 preserved the same correctness gates and improved throughput to 58.55 claims/second, but raised tail latency to 416 ms P95 and 580 ms P99. The p16 follow-up also stayed correct, but fell to 55.11 claims/second with 518 ms P95 and 683 ms P99. That makes p12 the current local sweet spot and argues against a 250K attempt before service/writeback pressure is understood.

The pend counts are intentionally reported with scope: 924 persisted pended outcomes overall, 920 expected-pend claims observed as pended, and zero unexpected pends across 10,934 scoreable expected-pay and expected-deny claims.

Post-#934 note: provider-fixture hardening moved scoreable validation providers into wider, role-separated synthetic NPI namespaces, prior-auth evidence became scoreable, and provider-exclusion denial labels were normalized. The follow-up 50K verification completed cleanly with zero workflow mismatches, 460/460 expected pends observed, zero unexpected pends, and 1,000/1,000 payment comparisons within tolerance. The final 100K run used that hardened fixture and scoring model.

## Episode metadata

- Episode title: The Clean 100,000-Claim Run
- Source article: Running a Healthcare Claims Platform Locally in Kubernetes, Part 8: The Clean 100,000-Claim Run
- Published article: not yet published
- Target length: 10-12 minutes
- Preferred format: two-host engineering conversation
- Primary hosts: Alex and Jordan
- Status: draft

## Core message

The stronger correctness system reached 100K cleanly, and the run exposed the next honest scaling question: local throughput and tail latency with plenty of Docker CPU and memory still available.
The final 100K run also exposed a live-progress presentation issue: expected-pend checks briefly appeared as 920 workflow mismatches during processing and resolved to zero after expected-pend observation completed.

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
- `raw-validator-output-post934-100k.txt` - raw validator console output from the completed post-#934 100K run.
- `run-summary-post934-100k.json` - completed dashboard summary returned by claims-service for the post-#934 100K run.
- `raw-validator-output-post937-100k-p12.txt` - raw validator console output from the completed 100K p12 confirmation run.
- `run-summary-post937-100k-p12.json` - completed dashboard summary returned by claims-service for the 100K p12 confirmation run.
- `raw-validator-output-post937-100k-p16.txt` - raw validator console output from the completed 100K p16 pressure run.
- `run-summary-post937-100k-p16.json` - completed dashboard summary returned by claims-service for the 100K p16 pressure run.
- `raw-validator-output-post928-100k.txt` - prior post-#928 100K validator output retained as provenance.
- `run-summary-post928-100k.json` - prior post-#928 dashboard summary retained as provenance.

The screenshots below are retained visual console evidence from the Part 8 console capture set. The post-#934 raw validator output and dashboard summary JSON are authoritative for the final 100K numbers.

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
- [x] Post-#934 50K scoring verification is recorded.
- [x] Final post-#934 100K raw validator output is recorded.
- [x] Post-#937 100K p12 confirmation run is recorded.
- [x] Post-#937 100K p16 pressure run is recorded.
- [x] Live expected-pend progress caveat is recorded.
- [ ] Published article URL is recorded.
