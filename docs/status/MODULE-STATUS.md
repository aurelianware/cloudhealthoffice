# Cloud Health Office — Module Status

**Last updated:** 2026-05 (Claims Phase 1 close)
**Maintainer:** Module owners per row
**Format owner:** Established with Claims Phase 1 closer (5.13);
mirrorable for future service-level closures.

This document tracks per-service / per-domain phase posture across
Cloud Health Office. It is a high-level register; per-domain detail
lives in domain-specific closer docs (e.g., the Claims Phase 1
closer at
[`docs/architecture/claims-phase-1-closer.md`](../architecture/claims-phase-1-closer.md)).

For broader product positioning see
[`docs/POSITIONING.md`](../POSITIONING.md). For commercial-readiness
sequencing across Cloud Health Office, see
[`docs/roadmap/CHO-Roadmap-Readme.md`](../roadmap/CHO-Roadmap-Readme.md)
— note that the roadmap's "Phase 1" / "Phase 2" axis tracks
**commercial-readiness milestones** and is distinct from
domain-internal phasing tracked here.

---

## Domain phase posture

| Domain | Phase 1 | Phase 2 | Closer doc | Notes |
|--------|---------|---------|-----------|-------|
| **Claims** (claims-service, claims-examiner-service, payment-service customer surface) | ✅ Complete (May 2026) | 🚧 Backlog cataloged | [claims-phase-1-closer.md](../architecture/claims-phase-1-closer.md) | 14 capabilities (5.1a-5.12b). Phase 2 backlog: [claims-phase-2-backlog.md](../roadmap/claims-phase-2-backlog.md) |
| **Provider** (provider-service, provider-verification-service) | ✅ Complete (April 2026) | 🚧 Backlog implicit in per-capability docs | — (per-capability docs only; closer pattern not retro-applied) | Pattern parity with Claims closer is a future option |
| **Benefit Plan** (benefit-plan-service) | ✅ Complete (April 2026) | 🚧 Backlog implicit in per-capability docs | — (per-capability docs only) | Pattern parity with Claims closer is a future option |
| **Coverage** (coverage-service) | 🚧 Active | ⏳ Planned | — | Phase 2 cross-service dependencies named in Claims Phase 2 backlog (CobEntry contract fixes; FHIR Coverage projection) |
| **Member** (member-service) | 🚧 Active | ⏳ Planned | — | Patient Access API contributions tracked separately |
| **AR / Appeals / Authorization / Capitation / Eligibility / Encounter / FFS / FHIR / Premium-billing / Risk-adjustment / Trading-partner / etc.** | Various | Various | — | Out of scope for this MODULE-STATUS revision; future revisions extend |

> **Out-of-scope for this revision.** This MODULE-STATUS register is
> initialized at Claims Phase 1 close. Other domains' rows are
> placeholder; full status posture lands as those domains close
> phases or as a focused MODULE-STATUS update PR fills them in. The
> goal of this initial revision is to establish the format, not
> claim full-portfolio posture.

---

## Claims Phase 1 — closure record

**Capabilities shipped:** 5.1a, 5.1b, 5.2, 5.3, 5.4, 5.5, 5.6, 5.7,
5.8, 5.9, 5.10, 5.11, 5.12a, 5.12b. 14 of 14.

**PR sequence:** #725, #728, #729, #731 (#732 follow-up), #733, #734,
#736, #737, #738, #739, #740, #741, #742, #743.

**Closer PR:** Claims 5.13 (this entry's source).

**Documentation surfaces shipped at close:**

- Closer narrative — [`docs/architecture/claims-phase-1-closer.md`](../architecture/claims-phase-1-closer.md)
- Phase 2 backlog (48 items across 10 categories) — [`docs/roadmap/claims-phase-2-backlog.md`](../roadmap/claims-phase-2-backlog.md)
- CMS-0057-F readiness — [`docs/compliance/claims-cms-0057-f-readiness.md`](../compliance/claims-cms-0057-f-readiness.md)
- V1 API surface (8 controllers / 47 verbs) — [`docs/api/claims-v1-surface.md`](../api/claims-v1-surface.md)
- 12 per-capability architecture docs unchanged — [`docs/architecture/claim-*.md`](../architecture/)
- 5.1b operator runbook unchanged — [`docs/migrations/claims-cosmos-partition-migration.md`](../migrations/claims-cosmos-partition-migration.md)

**Outstanding follow-ups (post-close):**

- Old `Claims` Cosmos container final deletion (~30 days post 5.1b
  cutover; Bicep PR) — [Phase 2 backlog 10.1](../roadmap/claims-phase-2-backlog.md#101--old-claims-cosmos-container-final-deletion)
- Phase 2 sequencing per
  [Phase 2 backlog](../roadmap/claims-phase-2-backlog.md). Primary
  near-term driver: CMS-0057-F unauthenticated patient access
  (January 2027 mandate).

---

## Format conventions (for future closures)

When a service or domain closes a phase, add or update its row using
this shape:

- **Domain** — service or domain name (services aggregated where
  natural, e.g., Claims spans claims-service + claims-examiner-service
  + relevant payment-service surfaces).
- **Phase 1 / Phase 2 columns** — status emoji + close date for
  Complete; "Active" for in-flight; "Planned" for not started;
  "Backlog cataloged" when the phase boundary is documented even if
  no work has started.
- **Closer doc** — link to the closer narrative if one exists, or
  "—" if per-capability docs are the only surface.
- **Notes** — sparse free-text for cross-domain dependencies, future
  considerations, or known caveats.

For full closer-pattern guidance, see the "Future closures" section
of [`docs/architecture/claims-phase-1-closer.md`](../architecture/claims-phase-1-closer.md#future-closures).

---

## Audit / review cadence

- **Per-PR:** Closer PRs update their own row.
- **Quarterly:** Format owner reviews for staleness; nudges out-of-
  date rows toward update PRs.
- **Pre-diligence engagements:** Full review before any external
  diligence consumer (investor, acquirer, regulator, prospective
  customer) is shown this register, to ensure rows reflect
  current state honestly.

---

## See also

- [`docs/POSITIONING.md`](../POSITIONING.md) — product positioning
- [`docs/roadmap/CHO-Roadmap-Readme.md`](../roadmap/CHO-Roadmap-Readme.md)
  — commercial-readiness phase sequencing
- [`docs/status/POSITIONING-AUDIT.md`](./POSITIONING-AUDIT.md) —
  documentation-and-marketing audit (positioning alignment)
- [`CHANGELOG.md`](../../CHANGELOG.md) — semver release log
