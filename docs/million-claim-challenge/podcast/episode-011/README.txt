# Episode 011: The Check That Only Ran in the Benchmark

## Episode summary

Part 10 disclosed, but explicitly did not fix, a defect in how `HttpProviderIntegrityGate` handles a live verification `"Failed"` status. Episode 011 opened that investigation and found the disclosed theory didn't survive a closer reading: the specific mapping Part 10 named wasn't producing a wrong-payment bug through the caller Part 10 was looking at. What the investigation found instead was worse in a different way — a real fail-open path in the same gate (total verification unavailability defaulted to a silent pass), and, underneath that, the discovery that the entire federal provider-exclusion check had never been reachable from a real submitted claim at all. Claims-service's actual production pipeline, `ClaimAdjudicationOrchestrator`, never called the gate; only the standalone synchronous endpoint this series' own MCC validator calls directly did. Every `ExcludedProviderDenied` result this series has ever reported was scored against that benchmark-only call, not against what the real orchestrator decided.

PR #974 fixed both: the gate can no longer fail open (a new `RequiresManualReview` flag distinguishes "held for review" from a confirmed exclusion), and a new `ProviderIntegrityStage` (`Order=150`) now runs the same check inside claims-service's real pipeline, reached through a new side-effect-free endpoint rather than folding it into the shared `calculate-benefits` call. Code review caught a third bug before merge — the standalone endpoint was still classifying "held for review" results as confirmed exclusions — fixed with an explicit `IsExcluded` check and four new tests.

This is not a scale episode. The evidence is a 500-claim smoke test through the real orchestrated pipeline, read directly from claims-service's own logs and persisted claim state rather than from the validator's benchmark-endpoint scoring: `ProviderIntegrityStage` fired exactly 10 `Deny` outcomes, one-for-one against the run's 10 seeded excluded-provider fixtures, zero false positives, zero false negatives, zero unintended pends anywhere else in the run.

## Episode metadata

- Episode title: The Check That Only Ran in the Benchmark
- Source article: Running a Healthcare Claims Platform Locally in Kubernetes, Part 11: The Check That Only Ran in the Benchmark
- Published article: pending — will be live at /insights/million-claim-challenge/part-11-the-check-that-only-ran-in-the-benchmark
- Target length: 10-12 minutes
- Preferred format: two-host engineering conversation
- Primary hosts: Alex and Jordan
- Status: evidence recorded, clean — podcast production pending

## Core message

A disclosed-but-unconfirmed bug is not the same as a confirmed one, and fixing it honestly means checking the theory again before touching code — not defending a guess just because a prior episode wrote it down. Part 10 named a specific mapping defect and was honest that it hadn't confirmed it. Part 11 checked, found the named defect wasn't the live problem through the caller it named, and kept looking instead of declaring victory on a technicality. That persistence is what found the real issue: a fail-open default in the same gate, and a structural gap where the entire check had never been wired into production traffic at all.

The episode must distinguish:

- a disclosed theory that didn't hold up from a theory that was simply wrong — the mapping code Part 10 named is real, but wasn't reachable the way Part 10 assumed
- a benchmark-only code path from claims-service's real, production-triggering orchestrator — this series' own prior `ExcludedProviderDenied` results were correct in outcome, but scored against the wrong system
- a confirmed exclusion (`IsExcluded`) from "could not be confidently verified" (`RequiresManualReview`) — conflating them, which code review caught before merge, is itself a safety bug
- a scale/throughput result (this is not one) from a correctness and pipeline-wiring verification
- local smoke-test evidence, including the disclosed in-memory-message-bus limitation of this environment, from a production capacity or messaging-behavior claim

## Packet files

- `article.txt` - Part 11 article source: the re-investigation, the two real bugs found, the fix, and the live verification.
- `benchmark-results.txt` - fix history and the 500-claim smoke-test evidence, both the synchronous benchmark side (unchanged bar) and the real orchestrated pipeline (new evidence this episode adds).
- `pr-summary.txt` - implementation work covered by this episode (PR #974) and the code-review-caught third bug.
- `podcast-prompt.txt` - episode-specific generation prompt.
- `raw-validator-output-500-smoke.txt` - raw validator console output from the smoke-test run.
- `claims-service-provider-integrity-log-excerpt.txt` - claims-service pod log excerpt showing `ProviderIntegrityStage`'s actual outcomes, independent of the validator's own scoring.
- `sample-denied-claim.json` - persisted claim record for one of the ten excluded-provider denials, confirming the async pipeline's result matches the synchronous benchmark's.
- `screenshots/.gitkeep` - placeholder for console evidence screenshots.

## Acceptance checklist

- [x] Part 10's disclosed theory is re-examined honestly, and the finding that it didn't fully hold up is stated directly rather than quietly patched over.
- [x] The larger discovery (the check was never wired into the real pipeline) is stated as the episode's actual thesis, not buried under the originally disclosed bug.
- [x] The fix is distinguished from the discovery: gate hardening (`RequiresManualReview`), new pipeline stage + endpoint, and the code-review-caught controller bug are each described separately.
- [x] This episode is explicitly labeled as not a scale run, distinguished from Part 8/9/10's throughput evidence.
- [x] The 500-claim smoke test's evidence is sourced from claims-service's own logs and persisted claim state, not only from the validator's benchmark-endpoint scoring.
- [x] The local in-memory-message-bus environment limitation is disclosed rather than left implicit.
- [x] Zero unintended side effects (false-pend sweep, error/exception grep) are reported, not just the intended outcomes.
- [ ] Console screenshots captured (log excerpt, claim record, validator summary).
- [ ] Published article URL recorded once live.
