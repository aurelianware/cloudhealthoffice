# Episode 012: The Database Nobody Profiled

## Episode summary

Episode 012 set out to answer one question: could this series run 500,000 claims in under an hour? Getting there took two detours neither one was looking for. Testing the validator at 30K and 50K scale — traffic it had never generated before — surfaced two real fixture-generation bugs: scenario injection functions that could silently overwrite an already-tagged edge-case claim without clearing its scoring label (PR #977), and a member date generator that draws coverage effective dates from a fixed window uncorrelated with any claim's service date, giving roughly 1% of claims a ~1% chance of landing "effective after their own service date" (PR #978). Neither was a production defect — both were the benchmark harness not holding up at scale it had never run at.

With the fixture generator trustworthy again, Submit-chain timing could finally be read honestly: average time had nearly doubled between 30K and 50K claims, the same "five sequential I/O hops" bottleneck Part 10 disclosed but never measured. Profiling every hop directly found no single dominant cost — instead, all four MongoDB-touching operations showed the unmistakable signature of resource contention (fast median, huge P95/P99 tail). Checking MongoDB's own `cgroup cpu.stat`, the same technique Part 10 used on claims-service, found it: MongoDB had been throttled for more cumulative time than it had actually run, on a 1-core limit shared by five services, never touched by Part 10's earlier app-tier CPU-limit fixes. Raising it to 4 cores produced a 2.7-3.4x throughput gain, verified live (PR #982).

The 500,000-claim confirmation that followed came back clean: 61,063/65,000 workflow checks matched, only 4 mismatched (all attributable to accumulated test-tenant state, not a new defect), zero platform failures, and 186.06 claims/sec — a higher rate than Part 10 achieved at half the scale. But Part 10's disclosed wall-clock gap reproduced, smaller: about 17 minutes unaccounted for, down from 45, despite this episode's own new instrumentation (PR #976) proving post-processing itself only takes 3 seconds. The gap is real, confirmed against claims-service's own log timestamps, and unresolved.

## Episode metadata

- Episode title: The Database Nobody Profiled
- Source article: Running a Healthcare Claims Platform Locally in Kubernetes, Part 12: The Database Nobody Profiled
- Published article: pending — will be live at /insights/million-claim-challenge/part-12-the-database-nobody-profiled
- Target length: 10-12 minutes
- Preferred format: two-host engineering conversation
- Primary hosts: Alex and Jordan
- Status: evidence recorded, clean at 500K with one disclosed open question — podcast production pending

## Core message

Chasing one number honestly can turn up bugs that have nothing to do with the number you're chasing — and finding them first, rather than working around them, is what makes the number you eventually get trustworthy. This episode's actual subject (the Submit chain, disclosed since Part 10) had to wait behind two fixture-generation bugs the investigation itself exposed by running at a scale nobody had tested before. Fixing those first, rather than treating the mismatches as noise to filter out, is what made the eventual profiling data believable. And when the real bottleneck turned up, it wasn't where three episodes of narrative had been pointing — it was one layer underneath, in a shared dependency nobody had individually profiled.

The episode must distinguish:

- a bug found while investigating something else from a distraction to route around
- fixture-generation bugs (found via the validator's own corpus generator) from production adjudication defects
- a hypothesis that sounds right (the "five hops" architecture) from what profiling actually measured (a shared database's CPU limit)
- a 2.7-3.4x throughput gain, verified with before/after numbers, from a plausible-sounding fix
- correctness evidence (clean 500K scoring) from performance evidence (throughput, the still-open gap) — the two tell different stories in this episode and neither should borrow the other's confidence
- a wall-clock gap that shrank from one that's resolved

## Packet files

- `article.txt` - Part 12 article source: the two fixture bugs, the Submit-chain profiling, the MongoDB discovery, and the 500K confirmation with its own open question.
- `benchmark-results.txt` - fix history for all four PRs (#976, #977, #978, #982), before/after Submit-chain profiling evidence, and the full 500K confirmation run results.
- `pr-summary.txt` - implementation work covered by this episode.
- `podcast-prompt.txt` - episode-specific generation prompt.
- `raw-validator-output-500k.txt` - raw validator console output from the 500K confirmation run.
- `mongodb-cgroup-before-after.txt` - MongoDB's cgroup cpu.stat and resource spec, before and after the CPU limit fix, plus the follow-up audit of other shared dependencies.
- `submit-profile-before-after.txt` - aggregated per-hop Submit-chain timing statistics, before and after the MongoDB fix.
- `screenshots/.gitkeep` - placeholder for console evidence screenshots.

## Acceptance checklist

- [x] Both fixture-generation bugs are described as bugs in the benchmark harness, not the production adjudication path — stated explicitly, not left ambiguous.
- [x] The Submit-chain profiling methodology (per-hop instrumentation, cgroup cpu.stat) is recorded, not just its conclusion.
- [x] The MongoDB fix is verified with a direct before/after comparison on an identical job, not just a plausibility argument.
- [x] The 500K confirmation's correctness result (clean, 4 attributable mismatches) is reported separately from its performance result (strong throughput, unresolved wall-clock gap).
- [x] The reproduced-but-smaller wall-clock gap is disclosed as genuinely unresolved, including the specific contradiction (server log timestamp vs. client-side timer) that makes it stranger than Part 10's original version, not smoothed into "mostly fixed."
- [x] "Run 500K in under an hour" is answered precisely: not at total wall time (1:23:40), yes at timed-adjudication time alone (44:47).
- [ ] Console screenshots captured (Submit-chain profiling output, MongoDB cgroup stats, 500K validation summary).
- [ ] Published article URL recorded once live.
