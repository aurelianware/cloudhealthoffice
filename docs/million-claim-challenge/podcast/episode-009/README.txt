# Episode 009: From Unsupported to Scored

## Episode summary

Episode 009 covers the first deliberate conversion of unsupported Million Claim Challenge scenarios into scored platform behavior: provider-aware prior-authorization validation with a runtime enforcement probe (`PriorAuthRequired_WrongProvider`), the behavioral-health carve-out denial path, the tenant-scoped behavioral-health service-category mapping, and the validation-plan behavioral-health benefit that returned carve-in/parity scenarios to deterministic paid outcomes.

It also records the fix for the Part 8 live-progress lesson: the validator now publishes pending pend-observation and pending terminal-status telemetry, and the console shows pending checks instead of temporary mismatches during active runs.

The episode's evidence gate is a post-#950 100,000-claim p12 confirmation run with the expanded answer key. Expected movement if conversions hold: workflow matched 11,854/13,000 -> 12,214/13,000, unsupported 1,146 -> 786, false-pend sweep scope 10,934 -> 11,294 scoreable non-pend claims. Those are targets until the run is recorded.

Series direction: the local Kubernetes series continues until the limiting local resource is reproducible and explained or the full one-million-claim corpus completes cleanly on local hardware. Then a new series begins: scaling Cloud Health Office in the cloud (AKS/EKS/GKE) with the same corpus and gates.

## Episode metadata

- Episode title: From Unsupported to Scored
- Source article: Running a Healthcare Claims Platform Locally in Kubernetes, Part 9: From Unsupported to Scored
- Published article: not yet published
- Target length: 10-12 minutes
- Preferred format: two-host engineering conversation
- Primary hosts: Alex and Jordan
- Status: draft - awaiting post-#950 100K confirmation evidence

## Core message

Unsupported scenarios are debt with a name, and paying that debt means changing the platform, not the scorekeeping. Each conversion in this episode shipped as product behavior first (provider-aware prior-auth validation, behavioral-health category resolution and benefits), and became scoreable only when the platform could actually enforce the expected outcome - including a runtime probe that keeps wrong-provider scoring off unless the deployed authorization-service really rejects wrong-provider authorization use.

The episode must distinguish:

- scored scenarios from unsupported scenarios, before and after conversion
- platform capability shipped in code from capability enforced by the deployed services (the probe exists because these differ)
- the temporary, honest carve-in/parity regression to unsupported in #949 from a hidden failure
- projected scoring-surface targets from recorded run evidence
- local benchmark evidence from production capacity claims

## Packet files

- `article.txt` - Part 9 article source (contains an EVIDENCE PENDING marker in the confirmation-run section).
- `benchmark-results.txt` - planned run identity, command, scoring-surface change under test, and [pending] evidence slots for the post-#950 100K p12 confirmation run.
- `pr-summary.txt` - implementation work covered by this episode (#936, #941, #946, #947, #949, #950).
- `podcast-prompt.txt` - episode-specific generation prompt.
- `raw-validator-output-post950-100k-p12.txt` - [to be added] raw validator console output from the completed confirmation run.
- `run-summary-post950-100k-p12.json` - [to be added] completed dashboard summary returned by claims-service.
- `screenshots/.gitkeep` - placeholder for console evidence screenshots (pending-check progress view, expanded scenario scoring, behavioral-health claim drilldown).

## Acceptance checklist

- [x] Scoring-surface conversions (#946, #947, #949, #950) are summarized with their honesty mechanisms.
- [x] Live-progress pending-check fix (#936, #941) is recorded.
- [x] Expected scoring-surface movement is stated as targets, not results.
- [x] Remaining unsupported families are named as visible product gaps.
- [ ] Post-#950 100K p12 confirmation run is recorded (raw validator output + dashboard summary).
- [ ] Prior-auth enforcement probe outcome is recorded explicitly.
- [ ] Article EVIDENCE PENDING marker is replaced with real numbers.
- [ ] benchmark-results.txt [pending] slots are filled and projections reconciled.
- [ ] Console screenshots captured (pending-check live view, scenario scoring, behavioral-health payment drilldown).
- [ ] Published article URL is recorded.
