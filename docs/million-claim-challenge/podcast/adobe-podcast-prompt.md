# Reusable Adobe Podcast / Acrobat Generate Podcast prompt

Create a 10-12 minute conversational podcast episode from the attached episode packet.

Use two hosts:

- Alex: systems architect focused on Kubernetes, performance, infrastructure, and repeatable benchmarking.
- Jordan: healthcare platform engineer focused on claims adjudication, benefit rules, prior authorization, CMS-0057-F, and workflow correctness.

The episode should feel like a natural engineering discussion between two senior people who are excited by evidence and careful about claims.

## Requirements

- Do not read the article verbatim.
- Do not turn this into marketing hype.
- Do not invent benchmark numbers, PRs, dates, customers, or production claims.
- Explain technical terms clearly enough for a healthcare technology audience.
- Emphasize measurable correctness, not only speed.
- Clearly distinguish valid business denials from platform failures.
- Treat local Kubernetes benchmarks as local validation, not production-scale performance claims.
- Include a recurring "Next Week's Challenge" ending.

## Suggested structure

1. Open with the core technical problem.
2. Explain what changed in the platform or benchmark workflow.
3. Discuss the evidence: pull requests, benchmark numbers, screenshots, and observed behavior.
4. Explain why the evidence matters for claims adjudication and payer platform modernization.
5. Highlight risks, caveats, and what the numbers do not prove yet.
6. End with "Next Week's Challenge."

## Tone

Use plain engineering language. Keep the pace conversational, thoughtful, and grounded. It is fine for the hosts to disagree briefly or ask clarifying questions, but the discussion should remain professional and evidence-driven.
