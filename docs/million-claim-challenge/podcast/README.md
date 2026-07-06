# Million Claim Challenge podcast packets

This folder contains source packets for preparing Million Claim Challenge podcast episodes from CloudHealthOffice project material.

The goal is not to generate audio in this repository. The goal is to keep a repeatable, repo-native workflow for preparing organized source material that can be uploaded to Adobe Podcast, Acrobat Generate Podcast, or another AI podcast workflow.

## What is an episode packet?

An episode packet is a curated set of text-first source files for one podcast episode. It packages the story, evidence, benchmark numbers, pull request context, screenshots, and host instructions in a format that an AI podcast tool can use.

Each packet should make it possible to generate a natural engineering conversation without forcing the model to infer the story from scattered repo history.

## Standard packet contents

Each episode folder should include:

| File | Purpose |
| --- | --- |
| `README.txt` | Episode overview, source list, and production notes. |
| `article.txt` | Medium article draft or source article adapted for podcast context. |
| `pr-summary.txt` | Pull requests, commits, and repo changes that matter for the episode. |
| `benchmark-results.txt` | Measurements, tables, environment details, and interpretation. |
| `podcast-prompt.txt` | Episode-specific prompt for Adobe Podcast / Acrobat Generate Podcast. |
| `screenshots/.gitkeep` | Placeholder for optional screenshots. Do not commit large media. |

Use screenshots only as source references for the podcast tool. Keep the repository lightweight: do not add generated audio, large screenshots, WAV/MP3 files, or rendered video.

## Reusable files

- `episode-template.md` - base structure for future episode packets.
- `adobe-podcast-prompt.md` - reusable prompt for a 10-12 minute conversational episode.
- `host-personas.md` - recurring host personas for technical continuity.
- `intro-script.md` - reusable show intro.
- `outro-script.md` - reusable show outro with "Next Week's Challenge."

## How to use with Adobe Podcast / Acrobat Generate Podcast

1. Open the episode folder, for example `episode-005/`.
2. Review `README.md` for the intended story and source list.
3. Upload or paste the following files into Adobe Podcast / Acrobat Generate Podcast:
   - `podcast-prompt.txt`
   - `article.txt`
   - `pr-summary.txt`
   - `benchmark-results.txt`
   - relevant reusable files such as `host-personas.md`, `intro-script.md`, and `outro-script.md`
4. Add any local screenshots manually if they help the tool understand a dashboard, pull request, or benchmark slide.
5. Ask the tool to generate a conversational podcast, not an audio reading of the article.

## Editorial guidance

The AI should not simply read the article. The preferred output is a natural conversation between two senior engineers who understand both distributed systems and healthcare claims workflows.

Keep the tone technically credible:

- No marketing hype.
- Explain terms clearly.
- Treat benchmark claims as evidence, not slogans.
- Emphasize measurable correctness, not only speed.
- Distinguish valid business denials from platform failures.
- Preserve the difference between local Kubernetes validation and production-scale cloud benchmarking.

## Claim outcomes guidance

Correct denials are not platform failures.

In the Million Claim Challenge, some claims should deny because the business rules are working. Examples include uncovered services, excluded providers, duplicate logic, prior authorization requirements, coding edits, and benefit plan rules.

Podcast hosts should explain this distinction clearly. A platform failure means unexpected system behavior, infrastructure failure, unhandled error, or workflow breakdown. A valid business denial means the platform applied the rules correctly.
