# Episode 014: Zero Unsupported, Then the Parallelism Nobody Profiled

## Episode summary

Two genuinely separate stories, published together because they happened back to back in the same session.

The first closes a scoring gap this series has carried since Part 9: five synthetic scenario families that were generated and adjudicated but never scoreable, because nobody had built the logic to check whether the result was right. A short investigation first, to find which was most tractable, then three PRs: `RetroEligibilityCoverageChange` reused infrastructure Part 9 already built; all three `Subrogation` variants converted together once a shared mechanism (one new claim field, one pipeline check) became clear, rather than treating them as three separate efforts; `MedicaidSpendDown` needed something genuinely new, modeled deliberately as a member-level eligibility fact rather than folded into the production deductible engine. Workflow checks landed at 1,300/1,300 matched — the first zero-unsupported run in series history. Along the way, CI caught two tests that local verification missed, because the local run rotation during that work hadn't included the test project those two tests lived in — a real process gap, named directly rather than treated as CI simply doing its job.

The second starts from an informal observation — parallelism-20 runs looked faster than parallelism-56 runs on the mass-adjudication dashboard — verified against the dashboard's own API rather than trusted on sight, and diagnosed with the same live-cgroup-cpu.stat technique this series used on claims-service in Part 10 and MongoDB in Part 12. The answer wasn't the leading suspect (Redis): it was two single-replica services, member-service and coverage-service, each on half a CPU core and each throttled 20-50% of the time under P56 load, never profiled at that concurrency before because neither had ever been the bottleneck before. Fixing them immediately exposed a second bottleneck — claims-service getting OOM-killed once it could finally sustain the load the CPU fix unlocked. Fixed both, plus a related `imagePullPolicy` issue that broke local development the moment the real manifests got applied. Final result: parallelism 56 went from the worst-performing concurrency level tested this series to the best, on the same job, same seed, verified three times.

## Episode metadata

- Episode title: Zero Unsupported, Then the Parallelism Nobody Profiled
- Source article: Running a Healthcare Claims Platform Locally in Kubernetes, Part 14: Zero Unsupported, Then the Parallelism Nobody Profiled
- Published article: pending — will be live at /insights/million-claim-challenge/part-14-zero-unsupported-parallelism
- Target length: 10-12 minutes
- Preferred format: two-host engineering conversation, two clearly separated acts
- Primary hosts: Alex and Jordan
- Status: evidence recorded, both threads verified live and merged — podcast production pending

## Core message

Two different kinds of rigor, both on display: closing a scoring gap means building real detection logic and proving it live, not declaring victory on green tests — the CI catch on two stale tests is proof the discipline held even when local verification had a real gap. And chasing a performance number means starting from what the dashboard actually says, not what a run "felt like," then following the evidence wherever it leads even when it isn't the suspect you expected (Redis, exonerated) and even when fixing one bottleneck immediately reveals the next one (member-service/coverage-service CPU, then claims-service memory).

The episode must distinguish:
- a scoring-surface gap (five families, closed) from a performance-surface gap (parallelism 56, closed) — different kinds of "unsupported," addressed with different techniques
- three scenarios converted together because one mechanism covered all three, from scope creep — the distinction matters and should be stated directly
- a CI failure caused by the code being wrong from one caused by a test's premise going stale — this episode is the second kind, and should say so plainly
- the OOM kill's real consequence (77 mismatches in that specific run) from its apparent cause — the mismatches were a different, older, already-disclosed gap, confirmed by checking the actual mismatched claims rather than assumed
- live cluster configuration drift (a real, disclosed finding about manifest hygiene) from a code defect — it was neither script nor pipeline, it was hand-patched state nobody had re-validated against the committed file in three weeks

## Packet files

- `article.txt` - Part 14 article source: the five-scenario sweep (three PRs), the CI catch, the P56 investigation, the two-stage fix, and the two things disclosed rather than smoothed over.
- `benchmark-results.txt` - full evidence for all four PRs: per-scenario live verification numbers, the dashboard parallelism table, the cgroup cpu.stat before/after deltas at each fix stage, and the mismatch root-cause trace.
- `pr-summary.txt` - implementation work covered by this episode (PRs #987-#990).
- `podcast-prompt.txt` - episode-specific generation prompt, structured as two acts.
- `dashboard-parallelism-data.txt` - raw dashboard API pulls: the 25-run parallelism table, cgroup cpu.stat deltas at each fix stage, and the OOM-kill pod evidence.
- `raw-validator-output-zero-unsupported.txt` - raw scenario breakdown from the first zero-unsupported run, plus the per-claim mismatch evidence proving the 77 mismatches were a single consistent pattern unrelated to the OOM kill.
- `screenshots/.gitkeep` - placeholder for console evidence screenshots.

## Acceptance checklist

- [x] All five scenario conversions are described with their real-world business meaning, not just "converted from unsupported to scored."
- [x] The decision to convert all three Subrogation scenarios together is justified directly (shared mechanism), not left to look like unplanned scope expansion.
- [x] The CI-caught test failures are attributed to a genuine local-verification gap (missing test project in the run rotation), not glossed over as "CI worked as intended."
- [x] The P56 investigation states clearly that Redis was the leading suspect and was exonerated by the data, not omitted to make the diagnosis look cleaner than it was.
- [x] The claims-service OOM kill and the 77 mismatches are correctly separated: the OOM kill is real and disclosed; the mismatches are proven (via per-claim dashboard data) to be a different, older, still-open issue.
- [x] The live configuration drift (ASPNETCORE_ENVIRONMENT, MongoDB secret) is disclosed as a real finding with a concrete cause, not treated as a minor aside.
- [x] The unexplained P20 cold-start throughput gap (699 vs 349-498 claims/sec) is disclosed as flagged-not-investigated, consistent with this series' standing discipline.
- [x] "Zero unsupported" is stated as a genuine first for the series, with the exact 1,300/1,300 figure, not rounded or softened.
- [ ] Console screenshots captured (zero-unsupported summary, dashboard parallelism table, cgroup cpu.stat output, OOM-kill pod description).
- [ ] Published article URL recorded once live.
