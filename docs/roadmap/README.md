# Roadmap

This roadmap is public-facing and evidence-oriented. It separates implemented
behavior from active work and future goals.

## Current Strengths

- Kubernetes-first local development and benchmark path.
- Claims adjudication evidence through the Million Claim Challenge.
- Mass Adjudication console with run summaries and claim-level drilldown.
- Pended-claim observability and false-pend validation in benchmark scoring.
- Payment-comparable scoring for clean professional paid claims.
- FHIR, X12, authorization, eligibility, benefits, claims, and terminology
  surfaces in the repository.

## Active Work

- Improve benchmark evidence visibility in the portal.
- Tighten payment accuracy gates and evidence filters.
- Reduce fixture-preparation cost for large MCC runs.
- Continue replacing stale marketing or release-number claims with dated
  evidence.
- Improve developer onboarding and docs navigation.

## Next Milestones

1. Rerun 100K after fixture-preparation optimization.
2. Add richer live run telemetry for preparation and adjudication phases.
3. Expand scoreable edge-case coverage.
4. Convert remaining benchmark gaps into explicit backlog items.
5. Formalize more architecture decisions as ADRs.

## Future Work

- 250K and 500K local benchmark milestones, if local resource bottlenecks are
  reproducible and explained.
- Cloud scaling comparisons across managed Kubernetes environments.
- More complete payment accuracy distribution reporting.
- More complete FHIR and X12 conformance evidence.
- Broader operational work queues and job consoles beyond mass adjudication.

## Stretch Goals

- Full one-million-claim benchmark with strict correctness gates.
- Production cloud reference architecture and cost model.
- Public demo environment with synthetic data only.
- Contributor-friendly scenario authoring for the benchmark corpus.

## Related Roadmap Documents

- [Claims phase 2 backlog](claims-phase-2-backlog.md)
- [Enhancement checklist](CHO-ENHANCEMENT-CHECKLIST.md)
- [Enhancement status](CHO-ENHANCEMENT-STATUS.md)
