# v6.1.0 — Spartacus

**Release date:** TBD (tagged once the nightly Inferno run is clean)
**Previous release:** [v6.0.0](./RELEASE_NOTES_v6.0.0.md) (October 28, 2026)

This release covers the pull requests merged since v6.0.0 (#1583 to #1608).
It is a minor version: nothing in it requires a configuration change to
upgrade from 6.0, and everything new that changes behaviour is off until a
deployment turns it on.

It closes most of what the v6.0.0 notes listed as
[not yet done for CMS-0057-F](./RELEASE_NOTES_v6.0.0.md#not-yet-done-for-cms-0057-f):
SMART Backend Services authentication, the Plan-Net 1.2.0 Provider Directory
and the rest of Bulk Data `$export`. Work planned for 6.2 (mid-December)
landed here too. What is still open is under
[Not yet done for CMS-0057-F](#not-yet-done-for-cms-0057-f).

"I'm Spartacus": a backend client now proves who it is with its own signed
assertion, and CHO can do the same to other payers.

---

## Highlights

### SMART Backend Services, in both directions (G-42)

- **Inbound** (#1583): a tenant administrator registers a backend client with
  its public keys (`jwks` or `jwksUri`) instead of a secret. The client gets
  `client_credentials` tokens with a `private_key_jwt` assertion (RS384 or
  ES384, single-use `jti`, `exp` at most five minutes out).
  `PUT /api/admin/smart/clients/{clientId}/keys` rotates the keys.
- **For a provider** (#1585): a backend client registered with `providerId`
  gets tokens carrying `provider_id`, and fhir-service treats it as that
  provider for Provider Access, including bulk `Group/{providerId}/$export`.
- **Outbound** (#1587): for Payer-to-Payer as the new payer, CHO signs its own
  assertions to the other payer's token endpoint, caches the token, and
  publishes its public keys at `/fhir/r4/.well-known/smart-backend-jwks.json`.
- **Evidence:** the hosted Inferno Bulk Data kit's SMART Backend Services group
  passed against the GKE demo on 2026-10-29 (#1598, #1606), and the nightly
  run now includes that group (#1607).

### The public Provider Directory is Plan-Net 1.2.0 (G-36)

- `/fhir/r4/directory/{directory}/` serves all nine Plan-Net profiles,
  including the plan's own payer Organization, with every SHALL search, chain,
  `_has`, `_include` and `_revinclude` of the Plan-Net CapabilityStatement
  (#1584).
- Each directory is an in-memory snapshot, rebuilt in the background every
  `PublicProviderDirectory:RefreshMinutes` (60).
- The GKE deploy can now publish directories from one setting,
  `PUBLIC_PROVIDER_DIRECTORY` (#1608). Before, only the interop profile could.
- The Inferno Plan-Net kit runs nightly with no token
  (`INFERNO-PLANNET-SERVER-001`). The kit is generated from Plan-Net 1.1.0, so
  check its known mismatches before treating a finding as a CHO defect.

### Bulk Data `$export` is complete (G-34, G-35), still off by default

- Claims-history attribution feeds a provider's Group export (#1586), so a plan
  that relies on it alone no longer exports nobody.
- `Patient/$export` (#1588), and GET kick-offs on every export endpoint.
- Payer-to-Payer over bulk: `PayerToPayer/$export` for consented members
  (#1589), PDex `Group/$bulk-member-match` and
  `Group/{id}/$davinci-data-export` (#1590), and outbound bulk with CHO as the
  new payer (`PayerToPayer/$bulk-initiate`, #1591).
- `_typeFilter` and `patient` (#1594).
- Fixes from the first Inferno Bulk Data run (#1592), and bulk jobs now read
  CHO's own services in the export's tenant (#1589).

### Prior authorization reporting

- Appeal outcomes the PA reporting ledger refused can be listed and re-queued
  by an `appeals:admin` operator, from the API or the portal case screen
  (#1596).

### Conformance testing

- The nightly interop job now runs the Bulk Data kit (#1593) and the Plan-Net
  kit, and the kits actually boot: database migration (#1597), seed fixes
  (#1597, #1604) and harness fixes (#1605). From 2026-10-24 to 2026-10-28 the
  job had failed silently and produced no verdicts.
- The GKE demo can be put in front of hosted Inferno on public tunnel
  hostnames, opt-in (#1598, #1606). Runbook:
  `docs/deploy/GCP-CONFORMANCE-TESTING.md`.

The full list, itemized, is in the [changelog](../../CHANGELOG.md).

---

## Upgrading from 6.0

No steps are required. Things to know:

- **Public directory resources changed shape.** Practitioner, PractitionerRole
  and Organization now declare only the Plan-Net profiles, and PractitionerRole
  carries Plan-Net codes instead of the CHO tier and secondary specialties.
  The authenticated `/fhir/r4/` routes are unchanged. This matters only to a
  plan that published a directory on 6.0.
- **A cancelled bulk export's status URL answers 404** (it was 410), as Bulk
  Data 2.0 requires. Only deployments with `BulkExport:Enabled` are affected.
- **`.well-known/smart-configuration`** now advertises `private_key_jwt`
  algorithms, `client-confidential-asymmetric` and the `refresh_token` grant.
- **To publish a directory**, each plan still needs its
  `PublicProviderDirectory:Payers:<id>` entry with an address; Plan-Net fails
  without it. See `docs/architecture/public-provider-directory.md`.

---

## Where this release has been validated

- **Hosted Inferno, against the GKE demo:** Bulk Data v2.0.0 SMART Backend
  Services group, **pass** (2026-10-29). The export groups have not been run
  on hosted Inferno.
- **Nightly interop job (Inferno kits against the seeded `interop-cho`
  profile):** _to be filled in from the first clean run (US Core 3.1.1,
  CARIN BB 2.0.0, Plan-Net, Bulk Data). This release is not tagged until it
  is clean._
- **Google Cloud (GKE):** the demo is deployed on every merge to `main`.
- **Azure:** automatic deploys are still paused (`AZURE_DEPLOYMENTS_ENABLED`)
  behind the #1217 deployment checklist. Neither 6.0 nor 6.1 has been deployed
  to Azure.

---

## Off by default

New in 6.1, or changed, and doing nothing until a deployment turns them on:

| Feature | Switch |
|---|---|
| Bulk `$export`: Group, Patient, Payer-to-Payer, bulk member match | `BulkExport:Enabled`; GCP `enable_bulk_export` with `BULK_EXPORT_BUCKET` and `FHIR_SERVICE_GOOGLE_ACCOUNT` |
| Outbound bulk Payer-to-Payer (#1591) | `Cms0057:PayerToPayerOutbound:Bulk:Enabled` |
| Outbound SMART Backend Services credentials (#1587) | `SmartBackendClients:*` and a `CredentialKey` on the payer's directory entry |
| Public Provider Directory | A plan listed in `PublicProviderDirectory:Directories`; on GKE, `PUBLIC_PROVIDER_DIRECTORY` (#1608) |
| Hosted-Inferno hostnames on the GKE demo (#1598) | `CONFORMANCE_FHIR_HOST`, `CONFORMANCE_AUTH_HOST` |

Everything in the [v6.0.0 list](./RELEASE_NOTES_v6.0.0.md#off-by-default) is
still off by default too.

---

## Not yet done for CMS-0057-F

The Patient Access, Provider Access and Payer-to-Payer API requirements take
effect on January 1, 2027. Still open after 6.1:

- **External verdicts.** Until the nightly run is clean, the US Core, CARIN BB,
  Plan-Net and Bulk Data export results are not externally verified (G-60).
  The Bulk Data export groups have not been run on hosted Inferno.
- **System-level `$export`** still answers 501; no caller has been approved
  for it.
- **No X12 278 bridge.** CHO relies on HHS enforcement discretion for
  FHIR-based prior authorization without a 278.
- **Per-issuer scope limits** (G-39), so only named issuers can mint
  `system/PayerToPayer.read`, are not built.

A plan must also supply some things itself:

- the `PA-DECISION-EXTENSION` letter template;
- its `PublicProviderDirectory` entry, with the plan's address;
- its counsel's reading of "more than once" for the Patient Access usage
  figures. The usage ledger counts only from the day 6.0 or later is
  deployed, so days before that need another source.

`docs/epa-gap-matrix.md` gives the status of every requirement.

---

**Full changelog:** [CHANGELOG.md](../../CHANGELOG.md) ·
[v6.0.0...v6.1.0](https://github.com/aurelianware/cloudhealthoffice/compare/v6.0.0...v6.1.0)
