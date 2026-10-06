# v5.0.0 — The Native Backend

**Release date:** October 2026
**Previous GitHub release:** [v4.0.0](https://github.com/aurelianware/cloudhealthoffice/releases/tag/v4.0.0) (February 11, 2026)

This is the first tagged release since v4.0.0. It rolls up the work recorded in
CHANGELOG as 4.1.0 through 4.4.0 (never tagged) plus everything merged since —
roughly 360 pull requests. The theme is that Cloud Health Office now *is* the
system of record behind its CMS-0057-F APIs, its claims lifecycle and its X12
trading-partner surface, rather than a layer in front of one. It is a major
version because several defaults and security behaviors changed; read
**Breaking and behavior changes** before upgrading.

As with recent site and docs work, these notes describe what the code does and
what has evidence behind it. Where something is validated only locally or
against reference implementations, it says so.

---

## Highlights

### CMS-0057-F: CHO as the native authorization and data backend

- **CHO as the native prior-authorization backend** — a backend seam selected
  by operating mode makes CHO the system of record (Replace mode, the default)
  for the CMS-0057-F prior-auth slice, with external-core integration (Augment
  mode) as an explicit, never-silent alternative (#1144).
- **Da Vinci PAS `Claim/$inquire`** — prior-authorization status through the
  FHIR surface (#1154).
- **CDex additional-information round trip** for pended prior authorizations
  (#1156), and **durable prior-authorization data retention** (#1155).
- **Drug exclusions enforced** in the prior-auth workflow (#1147).
- **Payer-to-Payer, implemented natively** — member match (#1149), member data
  export (#1148), outbound exchange (#1150), durable inbound ingestion into the
  member record (#1151), and a dedicated P2P consent lifecycle (#1152).
- **Provider Access enforced through the shared consent registry** (#1153).
- **USCDI clinical resources served** through Patient and Provider Access —
  twelve resource types governed from a single inventory that drives routes,
  SMART scopes, Provider Access filtering, P2P import and the
  CapabilityStatement (#1157).
- **Acceptance evidence** — a CMS-0057-F acceptance scenario suite (#1143),
  hardened and published evidence (#1146), and versioned evidence in CI.

### External interoperability with HL7 Da Vinci reference implementations

- A separate **Da Vinci interoperability harness** runs real HL7 reference
  implementations in digest-pinned containers and exchanges FHIR with them
  across a process boundary — nothing mocked or replayed (#1159).
- Scenarios: PAS submit against `br-payer`, **CRD** (#1160), and **DTR
  `$questionnaire-package`** chained from the payer's CRD determination (#1161).
  Evidence is republished as the harness runs.

### Security

- **Production SMART on FHIR / OAuth trust (SEC-01)** — explicit `Demo` vs
  `ExternalIssuer` identity mode, a multi-issuer trust registry keyed on exact
  `iss`, validated OIDC discovery and JWKS with an SSRF boundary, rate-limited
  key rotation, asymmetric-only algorithm policy, and a single per-request
  caller identity (#1158).
- **CHO token auth and tenant isolation across all services**, payment
  controls, NACHA transmission controls, and SMART production sign-in (#1217).
- **Secret scanning now actually scans.** The gitleaks configuration had no
  detectors and passed unconditionally; it is fixed, and a committed SFTP
  credential it had been hiding was removed from HEAD. The SFTP endpoint it
  belonged to has been decommissioned (#1184, #1196, #1198).
- **HashiCorp Vault secret provider** (Kubernetes auth preferred, no new package
  dependency), so off-Azure deployments have a working secret store (#1182).
- Log-forging fix in the SMART authorization controller (#792).

### Claims: full Phase 1 lifecycle (released as 4.4.0 in CHANGELOG)

- Submit → adjudicate → pay → adjust → reverse, end to end: claim identity and
  versioning, a seven-stage adjudication pipeline (scrubbing, network and
  credentialing, benefit calculation, NCCI/MUE, coordination of benefits, AI
  examination, persistence), operator-initiated batched 835 remittance, FHIR
  ExplanationOfBenefit projection, adjustment chains with re-adjudication, and
  batched 835 reversals (#725–#744).
- **Prospective adjudication (payment estimate) API** — read-only estimates that
  reuse the production engines without writing accumulators (#1091).
- **Pended-claim examiner workflow**, claim audit event timeline, and evaluator
  journeys (#1069, #1082–#1085).
- **Inbound X12 834 and 837 parsers** (professional and institutional) wired to
  enrollment and claim submission, with an end-to-end 834→837 smoke test and an
  EDI Transactions console in the portal (#1002–#1018).
- **Capitation (PMPM) payments** — contracts, monthly runs, statements, NACHA /
  Stripe Connect / check disbursement, and capitation 835 ERAs (4.3.0).

### Benefit plans and reference data

- Version-safe authoring for benefit rules, exclusions and plan networks, a plan
  validation workflow, and provider network verification (#1076–#1081).
- **Canonical reference data service** with hardened schema migrations
  (#1094, #1095, #1097); ICD-10 display catalog and terminology-backed diagnosis
  metadata (#914–#920).

### X12 trading-partner surface through a vendor-neutral gateway

- A healthcare transaction gateway abstraction (#1106) with a Stedi
  implementation covering real **270/271 eligibility** including dependents,
  **837 submission**, the **277CA acknowledgment** lifecycle with durable replay,
  **276/277 claim status**, **275 attachments**, and **835 ERA ingestion**
  (#1107–#1118).
- Payer-side receivers: **inbound 270/271 eligibility responder** (#1110) and
  **inbound 275 attachment receiver** (#1116).
- **835 payment posting** onto claim financials and member accumulators (#1122)
  and a **claim intelligence read model** composing 837/277CA/276/275/835
  without letting one transaction overwrite another (#1119).
- Canonical **payer reference directory** synchronized from Stedi's payer list
  (#1108).
- Provider eligibility API for Cloud Dental Office (#1204).

### Scale validation: the Million Claim Challenge

- A local and Kubernetes validation harness that adjudicates synthetic claims
  through the real pipeline and scores outcomes against expected results
  (payment deltas, pends, prior auth, COB, retro eligibility, subrogation,
  exclusions, behavioral-health carve-outs).
- Runs progressed from 100K to one million claims, with every previously
  unsupported scenario converted to scored (#987–#989). Fixes found only at that
  scale — Redis accumulator eviction, MongoDB cache sizing, service CPU
  contention — are in this release (#992, #1000, #990).
- Raw 837 adjudication scales out over Azure Service Bus across claims-service
  replicas (#1040).
- Method, results and errata are published in the Million Claim Challenge
  episode series; results are local-validation scope, not production traffic.

### Platform and operations

- **MongoDB is the default data provider**, and all services route through
  shared infrastructure (#1177). Several defects that kept services from
  starting together on a clean cluster were fixed (#1174, #1178, #1188).
- Full local Kubernetes (kind) deployment and quickstart (#790, #791, #797).
- Runbooks: production readiness, disaster recovery, observability incidents,
  837 adjudication operations (#1072–#1075).
- Public trust and security center, and an Evidence Hub (#1067, #1163).
- Marketing site moved off Azure Static Web Apps; container deploys of the site
  retired (#770, #772, #1169, #1212).

---

## Breaking and behavior changes

- **Identity mode is explicit.** `SmartAuth:Mode` must be `Demo` or
  `ExternalIssuer`. A `Demo` deployment on a non-development host **fails at
  startup**. Configure `ExternalIssuer` and its trust registry before upgrading
  production.
- **Tenant headers can no longer override a token.** `X-Tenant-ID` may fill in
  only when the token carries no tenant, and a mismatch returns **403**.
  `X-Dev-Tenant-ID` is honored on development hosts only.
- **Backend services require CHO tokens and are default-deny** (#1217).
  Startup fails if no `ChoAuth` issuers are configured; the tenant comes from
  the token only (a disagreeing header is 403, a token without a tenant is 401);
  symmetric keys are allowed only in Development/Testing. See
  `docs/security/service-auth-rollout-playbook.md` for the rollout order.
- **MongoDB is now the default provider** (#1177). Deployments relying on the
  previous default must set their provider explicitly.
- **Payer IDs are resolved through the payer reference directory.** Arbitrary
  payer IDs are no longer passed through to the clearinghouse; `PayerMap` /
  `TenantPayerMap` are deprecated fallbacks.
- **`claims-scrubbing-service` is decommissioned**; scrubbing is a stage of the
  adjudication pipeline (#734).
- **Argo claims-adjudication workflow, event source and SLA watchdog templates
  were removed** (#1217); adjudication runs in claims-service.
- **Cosmos claims partition key moved to `/tenantId`** (#743). The legacy
  container is retained for a deletion window — see the 4.4.0 follow-ups.

## Upgrade notes

1. Read `docs/architecture/smart-oauth-trust.md` and
   `docs/architecture/idp-integration-contract.md`; configure your issuer(s).
2. Follow `docs/security/service-auth-rollout-playbook.md` to provision service
   tokens before rolling services.
3. Set the data provider explicitly if you are not on MongoDB.
4. If you previously cloned the repository, rotate any credential you may have
   copied from older manifests; the SFTP credential removed in #1184 remains in
   git history and its endpoint has been decommissioned.

---

**Full changelog:** [CHANGELOG.md](../../CHANGELOG.md) ·
[v4.0.0...v5.0.0](https://github.com/aurelianware/cloudhealthoffice/compare/v4.0.0...v5.0.0)
