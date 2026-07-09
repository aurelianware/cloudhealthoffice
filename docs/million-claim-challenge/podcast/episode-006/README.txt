# Episode 006: From Fast Runs to Honest Edge-Case Scoring

## Episode summary

Episode 006 covers the step after repeatable local Kubernetes speed tests: making the Million Claim Challenge more honest about healthcare workflow correctness.

The core story is that Cloud Health Office moved from a narrow set of deterministic workflow checks to broader edge-case validation across expected paid, denied, and pended outcomes. The work made pended claims observable through persisted claim status, preserved scenario-specific fixture meaning after date normalization, fixed newborn, COB, and retro-eligibility scoring gaps, separated unsupported scenarios from mismatches, and produced 5K, 10K, and 50K breadth runs with zero mismatches.

Part 6 now has a clean 50,000-claim breadth run with zero platform failures, zero pend observation timeouts, and zero workflow mismatches.

100K is intentionally deferred. After the 50K breadth result is triaged and publishable, 100K should become a later focused milestone article instead of expanding this episode's scope.

## Episode metadata

- Episode title: From Fast Runs to Honest Edge-Case Scoring
- Source article: Running a Healthcare Claims Platform Locally in Kubernetes, Part 6: From Fast Runs to Honest Edge-Case Scoring
- Published article: https://medium.com/@markus_31/running-a-healthcare-claims-platform-locally-in-kubernetes-part-6-from-fast-runs-to-honest-4b58288da8da
- Target length: 10-12 minutes
- Preferred format: two-host engineering conversation
- Primary hosts: Alex and Jordan
- Status: published

## Core message

Correctness credibility improves when the benchmark refuses to overclaim.

The episode should make clear that:

- a paid claim, a denied claim, and a pended claim can all be correct outcomes
- unsupported scenarios should be labeled honestly instead of counted as wins or failures
- benchmark fixture bugs can create false failures
- the next proof step is 100K as its own focused milestone, not a late addition to Part 6

## Packet files

- `article.txt` - synced source snapshot of the published Medium article.
- `pr-summary.txt` - implementation and PR context for PRs #853-#856 and #858.
- `benchmark-results.txt` - exact 5K, 10K, and 50K breadth validation results.
- `raw-validator-output-50k.txt` - raw final 50K validator console output.
- `podcast-prompt.txt` - episode-specific prompt for Adobe Podcast / Acrobat Generate Podcast.
- `screenshots/episode-006-hero.png` - generated article hero image.
- `screenshots/.gitkeep` - placeholder for optional uploaded screenshots.

## Production notes

Upload this packet with the reusable files in the parent folder:

- `../host-personas.md`
- `../adobe-podcast-prompt.md`
- `../intro-script.md`
- `../outro-script.md`

The AI should not simply read the article. The preferred output is a natural conversation between two senior engineers.

The hosts must clearly explain that unsupported scenarios are not proof of correctness. They are honest gaps. That distinction is central to the episode.

## Acceptance checklist

- [x] Episode has a clear thesis.
- [x] 5K benchmark numbers are exact and sourced from a local run.
- [x] PRs #853-#856 and #858 are summarized.
- [x] Valid business denials are not described as failures.
- [x] Unsupported scenarios are explicitly separated from mismatches.
- [x] False-pend limitation is disclosed in the article, benchmark results, and podcast prompt.
- [x] Payment delta is defined as a diagnostic, not included in the zero-mismatch workflow score.
- [x] 10K validation run completed and recorded.
- [x] 50K validation run completed and recorded.
- [x] Raw final 50K validator output is included.
- [x] Article hero image is included.
- [x] 50K `EdgeCase:CobSecondaryPayer` mismatch triaged before publishing.
- [x] Article updated with final 10K and 50K numbers before publishing.
- [x] Published Medium article URL is recorded.
- [x] 100K deferred to a future milestone article.
- [x] No generated audio or large media is committed.
