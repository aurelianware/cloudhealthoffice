# Episode 005: From Faster to Repeatably Faster

## Episode summary

Episode 005 covers the move from isolated fast local runs to repeatable local Kubernetes benchmarking for the Million Claim Challenge.

The core story is that CloudHealthOffice added a local Kubernetes sweep harness, compared parallelism settings, discovered that the best setting changed as workload size increased, found a hidden 10,000-claim cap, made the cap explicit with an intentional override, and then completed a true 50,000-claim validation run.

## Episode metadata

- Episode title: From Faster to Repeatably Faster
- Source article: Running a Healthcare Claims Platform Locally in Kubernetes, Part 5: From Faster to Repeatably Faster
- Target length: 10-12 minutes
- Preferred format: two-host engineering conversation
- Primary hosts: Alex and Jordan

## Core message

Performance tuning only matters if it is repeatable, explainable, and paired with workflow correctness.

The episode should make clear that throughput alone is not the story. The real benchmark is throughput plus latency, platform stability, and correct healthcare workflow outcomes.

## Packet files

- `article.txt` - article source adapted for the podcast.
- `pr-summary.txt` - implementation and PR context.
- `benchmark-results.txt` - exact benchmark numbers and interpretation.
- `podcast-prompt.txt` - episode-specific prompt for Adobe Podcast / Acrobat Generate Podcast.
- `screenshots/.gitkeep` - placeholder for optional uploaded screenshots.

## Production notes

Upload this packet with the reusable files in the parent folder:

- `../host-personas.md`
- `../adobe-podcast-prompt.md`
- `../intro-script.md`
- `../outro-script.md`

The AI should not simply read the article. The preferred output is a natural conversation between two senior engineers.

The hosts must clearly explain that valid business denials are not platform failures.
