# v6.1.1 — Spartacus (patch)

**Release date:** October 31, 2026
**Previous release:** [v6.1.0](./RELEASE_NOTES_v6.1.0.md) (October 31, 2026)

A patch release with no configuration changes. Upgrade from 6.1.0 by
deploying this version; nothing else is needed.

The v6.1.0 tag was placed on #1654, one commit after the commit the Inferno
gate tested (#1653). #1654 gave CARIN BB's skipped tests data, but it also
broke one search: once the Coverage payor referenced the plan's Organization,
`Coverage?_include=Coverage:payor` was refused with a 400 and the CARIN BB kit
failed one test. This release adds #1655, which fixes that, and the website
and documentation changes merged since.

---

## Fixed

- **`Coverage?_include=Coverage:payor` answers again (#1655).** A payor that
  references the plan's own Organization (`Fhir:PlanOrganization`) is
  included in the searchset with `search.mode` include. Any other referenced
  payor is still refused, as before.

## Included from the v6.1.0 tag

These shipped in the v6.1.0 tag but are not described in the v6.1.0 notes,
because they merged after the commit those notes cover.

- **CARIN BB Organization and must-support data (#1654).** The plan's own
  Organization is served as a C4BB-Organization at
  `Organization/{Fhir:PlanOrganization:Id}`, and the EOB insurer and Coverage
  payor reference it. The EOB's provider is typed from the 837 billing
  provider's entity type (NM102). Coverage carries the member id as its MB
  identifier and, when recorded, group and plan names (new optional
  `groupName` and `planName` on coverage create). Patient carries CARIN BB's
  unique member id and `deceasedBoolean` false for a living member, and member
  create accepts `race` and `ethnicity`.

## Website and documentation

- The Inferno results page, badges and article show the excused known kit
  issues and match the all-green run (#1657).
- The website architecture page is refreshed for v6.0 (#1656).
- New answers on pended versus pending prior authorizations (#1652).

---

## Where this release has been validated

- **Inferno kits against the seeded `interop-cho` profile:** US Core 3.1.1,
  CARIN BB 2.0.0, Da Vinci Plan-Net and Bulk Data 2.0.0 all **pass** on
  `main` after #1655 (DaVinci Interop run
  [41207410008](https://github.com/aurelianware/cloudhealthoffice/actions/runs/41207410008),
  commit 5d5e87d, 2026-10-31), apart from the same known kit issues listed in
  the [v6.1.0 notes](./RELEASE_NOTES_v6.1.0.md#where-this-release-has-been-validated).
  The commits after it change only the website and documentation.
- **Google Cloud (GKE):** the demo is deployed on every merge to `main`.
- **Azure:** automatic deploys are still paused behind the #1217 deployment
  checklist.

---

**Full changelog:** [CHANGELOG.md](../../CHANGELOG.md) ·
[v6.1.0...v6.1.1](https://github.com/aurelianware/cloudhealthoffice/compare/v6.1.0...v6.1.1)
