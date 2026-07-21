# Episode 009: From Unsupported to Scored

## Episode summary

Episode 009 covers the first deliberate conversion of unsupported Million Claim Challenge scenarios into scored platform behavior: provider-aware prior-authorization validation with a runtime enforcement probe (`PriorAuthRequired_WrongProvider`), the behavioral-health carve-out denial path, the tenant-scoped behavioral-health service-category mapping, and the validation-plan behavioral-health benefit that returned carve-in/parity scenarios to deterministic paid outcomes.

It also records the fix for the Part 8 live-progress lesson: the validator now publishes pending pend-observation and pending terminal-status telemetry, and the console shows pending checks instead of temporary mismatches during active runs.

The episode's evidence gate was a post-#950 100,000-claim p12 confirmation run with the expanded answer key. It took four attempts and three additional fix PRs to actually clear it — full story in `benchmark-results.txt` "Fix history" and the article's "The Part 9 confirmation run took four tries". Round 1 (#950 alone) regressed to 7,178/13,000, fixed by #954 (plan-specific service-category overrides). Round 2 (#950+#954) recorded 12,182/13,000, 32 mismatched — initially filed as scale-dependent follow-up debt, actually PR #956's bug (provider network-participation writes silently no-opped on Active providers). Round 3 (#950+#954+#956) fixed COB completely but surfaced a new, unrelated regression in ExcludedProviderDenied (64 mismatched) — PR #958's bug (stale integrity fields on a provider-fixture NPI collision). Round 4 (all four PRs) is clean: **12,214/13,000 matched, 0 mismatched**, 786 unsupported, every acceptance-bar criterion met exactly — all recorded in `benchmark-results.txt`.

Series direction: the local Kubernetes series continues until the limiting local resource is reproducible and explained or the full one-million-claim corpus completes cleanly on local hardware. Then a new series begins: scaling Cloud Health Office in the cloud (AKS/EKS/GKE) with the same corpus and gates.

Next rung: the post-#950/#954/#956/#958 100K confirmation run is recorded, clean. The next jump is 250,000 claims (the Part 10 candidate), following the series pattern of scaling the run and working through whatever mismatches and service pressure the new size exposes — including confirming whether Round 4's throughput hit (26.27 claims/sec vs. the 58.55 baseline) was really the one-time provider-healing migration cost it's believed to be.

## Episode metadata

- Episode title: From Unsupported to Scored
- Source article: Running a Healthcare Claims Platform Locally in Kubernetes, Part 9: From Unsupported to Scored
- Published article: live at /insights/million-claim-challenge/part-9-from-unsupported-to-scored
- Target length: 10-12 minutes
- Preferred format: two-host engineering conversation
- Primary hosts: Alex and Jordan
- Status: evidence recorded, clean (zero mismatches) - podcast production pending

## Core message

Unsupported scenarios are debt with a name, and paying that debt means changing the platform, not the scorekeeping. Each conversion in this episode shipped as product behavior first (provider-aware prior-auth validation, behavioral-health category resolution and benefits), and became scoreable only when the platform could actually enforce the expected outcome - including a runtime probe that keeps wrong-provider scoring off unless the deployed authorization-service really rejects wrong-provider authorization use.

The episode must distinguish:

- scored scenarios from unsupported scenarios, before and after conversion
- platform capability shipped in code from capability enforced by the deployed services (the probe exists because these differ)
- the temporary, honest carve-in/parity regression to unsupported in #949 from a hidden failure
- projected scoring-surface targets from recorded run evidence
- local benchmark evidence from production capacity claims

## Packet files

- `article.txt` - Part 9 article source, including the four-round confirmation-run section and the full #954/#956/#958 fix history.
- `benchmark-results.txt` - run identity, command, scoring-surface change under test, and recorded results for the final, clean post-#950/#954/#956/#958 100K p12 confirmation run.
- `pr-summary.txt` - implementation work covered by this episode (#936, #941, #946, #947, #949, #950, #954, #956, #958).
- `podcast-prompt.txt` - episode-specific generation prompt.
- `raw-validator-output-post950-100k-p12.txt` - raw validator console output from the final, clean confirmation run.
- `run-summary-post950-100k-p12.json` - completed dashboard summary returned by claims-service.
- `screenshots/.gitkeep` - placeholder for console evidence screenshots (pending-check progress view, expanded scenario scoring, behavioral-health claim drilldown).

## Acceptance checklist

- [x] Scoring-surface conversions (#946, #947, #949, #950) are summarized with their honesty mechanisms.
- [x] Live-progress pending-check fix (#936, #941) is recorded.
- [x] Expected scoring-surface movement is stated as targets, not results.
- [x] Remaining unsupported families are named as visible product gaps.
- [x] Post-#950/#954/#956/#958 100K p12 confirmation run is recorded (raw validator output + dashboard summary), clean.
- [x] Prior-auth enforcement probe outcome is recorded explicitly (enforced).
- [x] Article confirmation-run section covers all four rounds and the #954/#956/#958 fix history honestly.
- [x] benchmark-results.txt results are final: 12,214/13,000 matched, 0 mismatched, 786 unsupported — every original acceptance-bar criterion met exactly.
- [x] Throughput regression in the final run (26.27 claims/sec vs. 58.55 baseline) is reported honestly, with cause and an open question for the next rung to confirm.
- [ ] Console screenshots captured (pending-check live view, scenario scoring, behavioral-health payment drilldown, ExcludedProviderDenied clean result).
- [x] Published article URL is recorded (/insights/million-claim-challenge/part-9-from-unsupported-to-scored).
