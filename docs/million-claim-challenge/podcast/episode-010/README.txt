# Episode 010: The Migration Cost That Wasn't

## Episode summary

Episode 010 closes the open question Part 9 left behind: was the throughput drop in Part 9's confirmation run (26.27 claims/sec vs. a 58.55 baseline) really a one-time provider-healing migration cost? The answer is no. A fresh 250,000-claim baseline run — 2.5x Part 9's scale, no migration required — still ran at 41.43 claims/sec, and profiling found the real causes: a cold Redis accumulator cache forced a cross-service HTTP call on every claim's first read, all three adjudication-path services shared an artificially tight CPU limit that throttled claims-service 81.3% of the time, and NCCI's lookup cache carried a 10-minute TTL far shorter than a real run's duration.

Four fixes shipped from that profiling pass (PR #961 parallel seeding, #967 accumulator cache warming, #968 corrected CPU limits, #969 extended NCCI TTL), taking a 5,000-claim smoke test from 92.73 to 203.80 claims/sec before the 250K confirmation run even started.

The confirmation run itself took three tries — full story in `benchmark-results.txt` "Fix history" and the article's "The confirmation run took three tries." Round 1 hit 147.12 claims/sec (3.55x baseline) but found 5 scattered mismatches, all tracing to the same cause: provider verification timestamps aging past a 7-day staleness threshold, triggering a live NPPES-registry check that always fails for MCC's synthetic NPIs (fixed by PR #970). Round 2 found a different cause behind 80 concentrated mismatches: provider-service, still a single replica, dropping connections under real p56 concurrency, forcing the same live-fallback path through a different trigger — and surfacing a genuine, separate bug in how that fallback interprets a "Failed" verification result (fixed the trigger via PR #971, disclosed the mapping bug rather than patching it reflexively). Round 3 is clean: **30,534/32,500 matched, 0 mismatched**, 1,966 unsupported, every acceptance-bar criterion met at 2.5x Part 9's scale, at 145.04 claims/sec — a 3.5x improvement over this episode's own baseline.

Series direction: the local Kubernetes series continues until the limiting local resource is reproducible and explained or the full one-million-claim corpus completes cleanly on local hardware. Then a new series begins: scaling Cloud Health Office in the cloud (AKS/EKS/GKE) with the same corpus and gates.

Next rung: 500,000 claims — the first scale where the still-untouched `Submit` chain (five sequential I/O hops per claim) and the disclosed provider-integrity status-mapping bug both get a harder test than 250K gave them.

## Episode metadata

- Episode title: The Migration Cost That Wasn't
- Source article: Running a Healthcare Claims Platform Locally in Kubernetes, Part 10: The Migration Cost That Wasn't
- Published article: live at /insights/million-claim-challenge/part-10-the-migration-cost-that-wasnt
- Target length: 10-12 minutes
- Preferred format: two-host engineering conversation
- Primary hosts: Alex and Jordan
- Status: evidence recorded, clean (zero mismatches) - podcast production pending

## Core message

A guessed explanation for a performance regression is a liability until it's tested, even when the guess sounds reasonable and even when the episode that made the guess was otherwise careful and honest about it. Part 9 said "probably a one-time migration cost" and said plainly that it was unconfirmed. Part 10 tested it, found it wrong, and found the real causes only by profiling instead of re-guessing — a cold cache, a mis-sized resource limit, and an overly short TTL, none of which had anything to do with provider migrations.

The episode must distinguish:

- a hypothesis honestly labeled unconfirmed from a result actually measured
- profiling evidence (cgroup throttling stats, per-stage timing) from assumption
- fixes validated live at small scale before being trusted at 250K
- the performance-tuning story (clean, resolved) from the confirmation-run story (two real defects, both disclosed)
- a fix for a bug's trigger from a fix for the bug itself — PR #971 removed the condition that exposed the status-mapping defect without touching the defect
- local benchmark evidence from production capacity claims

## Packet files

- `article.txt` - Part 10 article source, including the profiling narrative and the three-round confirmation-run section.
- `benchmark-results.txt` - run identity, command, and recorded results for the final, clean 250K p56 confirmation run, plus the full fix history across the performance-tuning pass and both confirmation-run defects.
- `pr-summary.txt` - implementation work covered by this episode (#961, #962, #966, #967, #968, #969, #970, #971) and the disclosed, unfixed status-mapping bug.
- `podcast-prompt.txt` - episode-specific generation prompt.
- `raw-validator-output-250k-p56.txt` - raw validator console output from the final, clean confirmation run.
- `run-summary-250k-p56.json` - completed dashboard summary returned by claims-service.
- `screenshots/.gitkeep` - placeholder for console evidence screenshots.

## Acceptance checklist

- [x] Part 9's open question (one-time migration cost vs. standing tax) is answered directly, and the answer is stated honestly even though it isn't the guessed one.
- [x] The profiling methodology (cgroup cpu.stat, per-stage timing) is recorded, not just its conclusions.
- [x] Each of the four performance fixes is validated with a live smoke-test result before being trusted at 250K scale.
- [x] The confirmation run's three-round history is recorded honestly, including both defects found and the one deliberately left unfixed.
- [x] The disclosed, unfixed `HttpProviderIntegrityGate` status-mapping bug is named explicitly, with its file location and failure mechanism, not folded into "future work."
- [x] benchmark-results.txt results are final: 30,534/32,500 matched, 0 mismatched, 1,966 unsupported — every acceptance-bar criterion met exactly at 2.5x Part 9's scale.
- [x] Performance result is reported as resolved, not open — distinguished explicitly from Part 9's unresolved throughput question.
- [ ] Console screenshots captured (profiling evidence, confirmation-run mismatch drilldown, clean final result).
- [x] Published article URL is recorded (/insights/million-claim-challenge/part-10-the-migration-cost-that-wasnt).
