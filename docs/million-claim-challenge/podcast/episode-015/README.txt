# Episode 015: One Million Claims, and the Bug Only That Scale Could Find

## Episode summary

Every episode since Part 6 has closed with the same recurring section: "the road to one million." This is the episode where that number finally ran.

With Part 14 closing the last known scoring gap (zero unsupported scenarios) and fixing the last known performance ceiling (parallelism 56), nothing left disclosed as open pointed at a reason not to attempt the real target. Every touched service was rebuilt fresh from the merged code and redeployed; a deliberately fresh seed was chosen to avoid a member-seeding pollution mechanism the previous episode had just diagnosed.

The first 1,000,000-claim run came back with a real, honest mixed result: zero unsupported scenarios, 129,989/130,000 workflow checks matched, an exact payment gate, and the Part 13 wall-clock-gap fix holding at fifty times the scale it had ever been tested at — alongside 23 platform failures, the first this series has seen since it started chasing every single one down. All 23 appeared in one tight window between the 950,000 and 960,000-claim mark, nowhere else. That specific shape — fine, then abruptly wrong, late in a long run — pointed the investigation in the right direction immediately: not concurrency (Redis had shown zero CPU throttling in every prior test), but memory. `redis-dataprotection`'s `used_memory` was pinned exactly at a 768-megabyte ceiling, and `evicted_keys` had passed six million — more evictions than the store even currently held. A full million-claim run touches roughly 962,000 distinct member accumulators, a working set none of the prior 20K/150K/500K verification runs had ever approached, and sustained LRU eviction-scan overhead under real concurrent load is exactly what turns a normal sub-millisecond Redis read into a 22-second wait.

The sharpest detail: that 768-megabyte number existed nowhere in the committed Kubernetes manifest. It was a hand-applied live patch from somewhere in the cluster's three-week history, never synced back — the same undocumented-drift pattern Part 14 found on two other services days earlier, now on a third. Fixed by raising the ceiling to 3 gigabytes (roughly 4x the measured working set) and, for the first time, writing the real configuration into the file. Verified on an identical re-run of the full million claims, not a smaller stand-in: zero evictions, platform failures 23 → 0, payment gate still exact, wall-clock gap down to 7 milliseconds unaccounted out of two and a half hours.

What the fix didn't solve, stated plainly rather than smoothed over: throughput at sustained scale (107.91-123.81 claims/sec across both runs) remained well below the 363 claims/sec verified at 20,000-claim scale in Part 14 — confirmed not Redis on both runs, and left open as a genuinely unexplained gap between short-burst and steady-state behavior.

## Episode metadata

- Episode title: One Million Claims, and the Bug Only That Scale Could Find
- Source article: Running a Healthcare Claims Platform Locally in Kubernetes, Part 15: One Million Claims, and the Bug Only That Scale Could Find
- Published article: pending — will be live at /insights/million-claim-challenge/part-15-one-million-claims
- Target length: 10-12 minutes
- Preferred format: two-host engineering conversation
- Primary hosts: Alex and Jordan
- Status: evidence recorded, both runs verified live, fix merged — podcast production pending

## Core message

Reaching the number this series named as its target from the very first "road to one million" line didn't happen despite finding a new bug — it found a new bug specifically because it was the first time anything had actually run at that scale. The pattern isn't new: every scale increase this series has attempted has surfaced something the previous scale couldn't see (Part 8's next bottleneck at 100K, Part 10's real MongoDB cost at 250K, Part 12's actual database ceiling at 500K). This episode is proof the pattern holds at the scale that was always the point, not an exception to the series' discipline.

The episode must distinguish:
- a milestone reached from a milestone reached cleanly — this one is honest, not clean, and says so directly
- the failure's specific shape (a tight cluster late in the run, not scattered throughout) as a real diagnostic clue, not incidental detail
- the false start correctly ruled out (concurrency/CPU) from the actual cause (memory ceiling + eviction churn) — Redis's own zero-CPU-throttling history across this entire series is what redirected the investigation
- a configuration value that was live-patched and never committed (the real root cause of the drift) from a simple "we forgot to set maxmemory" framing
- the Redis fix (verified, complete, closes the platform-failure and eviction story) from the remaining throughput gap (confirmed NOT Redis, genuinely unexplained, explicitly not chased in this episode)

## Packet files

- `article.txt` - Part 15 article source: why now, the first attempt's mixed result, the platform-failure shape as a clue, the Redis investigation, the fix and its honest limits, and what one million claims actually proves about the series' own methodology.
- `benchmark-results.txt` - full numeric evidence for both runs side by side, the Redis diagnostic data (used_memory, evicted_keys, slowlog), the fix details, and the post-fix verification.
- `pr-summary.txt` - implementation work covered by this episode (PR #992).
- `podcast-prompt.txt` - episode-specific generation prompt.
- `raw-evidence-1m-runs.txt` - raw kubectl/redis-cli output from both runs: progress-line transitions, the platform-failure stack traces, the live deployment config revealing the drift, the fix commands, and the post-fix Redis state.
- `screenshots/.gitkeep` - placeholder for console evidence screenshots.

## Acceptance checklist

- [x] The first run's result is reported as a genuine mixed outcome (real wins + a real new failure), not softened toward either "clean success" or "the run failed."
- [x] The platform failures' clustered shape (950K-960K window, zero before and after) is used as a diagnostic clue driving the investigation narrative, not just reported as a count.
- [x] The ruled-out hypothesis (concurrency/CPU contention) is stated explicitly, with the specific evidence that ruled it out (Redis's clean CPU-throttling history across the whole series).
- [x] The root cause of the 768MB ceiling itself (undocumented live-cluster drift, not a deliberate design choice) is stated directly and connected explicitly to Part 14's identical finding on two other services.
- [x] The fix is verified against a full identical re-run of the actual target scale, not a smaller stand-in.
- [x] The remaining throughput gap is disclosed as genuinely open, with the specific evidence that rules out Redis a second time, and the decision not to chase it further in this episode is justified directly (doesn't affect correctness, requires another multi-hour run to reproduce).
- [x] The closing section connects this episode's finding to the series' established pattern (every scale increase finds something the previous one couldn't) rather than treating it as a one-off surprise.
- [ ] Console screenshots captured (both runs' final summaries, the Redis before/after INFO output, the platform-failure stack trace).
- [ ] Published article URL recorded once live.
