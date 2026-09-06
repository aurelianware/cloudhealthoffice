# CMS-0057-F Acceptance Scenario Suite — Codebase Inventory & Traceability

Phase-0 recon for the CMS-0057-F Acceptance Scenario Suite. This is an honest
map of what exists in `aurelianware/cloudhealthoffice` today, what each scenario
binds to, and whether it is **PASSABLE**, **PARTIAL**, or **GAP** against the
current code. It is the source of truth the executable harness
(`tests/Cms0057Acceptance.Tests/`) is built against.

> Marketing claims of "complete CMS-0057-F compliance" were treated as
> untrusted until the controllers and adapters proved them. Where the code does
> not support a scenario, it is marked GAP with the exact missing file/symbol.

---

## Product / services boundary

Cloud Health Office (CHO) is used as the short form for the product throughout
this engineering doc.

- **CHO ships**: the FHIR surface (CRD/DTR/PAS, Patient/Provider Access
  projections), IG handling, consent/identity plumbing, the **CHO-native
  authorization backend** (Replace mode — Cloud Health Office owns the record),
  and this acceptance harness.
- **Per-engagement (external-core adapter)**: binding a customer's system of
  record (QNXT / Facets / HealthEdge) in **Augment mode**. **These external
  adapters are stubs today that throw `NotImplementedException`.** The suite
  asserts those stubs rather than papering over them.

Nothing here claims "production ready against QNXT". The external-core adapters
are stubs; see the GAP rows.

## Two dimensions: product capability vs integration capability

CMS-0057-F readiness has **two independent axes**. This inventory scores them
separately — a QNXT gap is not a Cloud Health Office product gap.

- **Product capability** — can Cloud Health Office itself perform the workflow?
  Proven in **Replace mode** (CHO is the authoritative backend).
- **Integration capability** — can Cloud Health Office perform the workflow
  against a specific external core *right now*? Proven in **Augment mode** with a
  live adapter. The QNXT/Facets/HealthEdge adapters are engagement work.

So, for example: **PAS-03 → CHO Replace: PASSABLE; QNXT Augment: GAP.**

## Operating mode: Demo vs Replace vs Augment

Three distinct concepts, deliberately not conflated:

| Mode | Who is authoritative | Data | Where configured |
| --- | --- | --- | --- |
| **Demo** | n/a — demonstration | synthetic / demo | `FhirAdapters:Mode = "Demo"` (fhir-service FHIR projections) |
| **Replace** | **Cloud Health Office** | authoritative (CHO-owned) | `Cms0057:Authorization:OperatingMode = "Replace"` (default) |
| **Augment** | external core (QNXT / Facets / HealthEdge) | authoritative (external) | `Cms0057:Authorization:OperatingMode = "Augment"` + `AugmentBackend` |

"Demo" is **not** a synonym for "Cloud Health Office backend": Demo is synthetic
demonstration data, Replace is CHO operating as the production system of record.
The authorization vertical slice reuses the OperatingMode engine's
`EngineOperatingMode { Augment, Replace }` (`docs/architecture/OPERATING-MODE.md`),
which defaults engines to **Replace** (CHO authoritative). A deployment
configured for Augment never silently falls back to the CHO backend — selection
fails loudly if the configured external backend is not registered
(`AuthorizationBackendSelector`). The FHIR projection layer defaults to Demo +
synthetic so a deployment can never silently look live (`FhirAdapterOptions`).

## Authorization vertical slice — Replace vs Augment

The prior-authorization slice is the first workflow made explicitly CHO-native:

```
FHIR / PAS $submit
   -> AuthorizationsController
   -> IAuthorizationBackendSelector (by operating mode)
        Replace -> ChoAuthorizationBackend -> IAuthorizationRepository (Cosmos / Mongo)   [CHO authoritative]
        Augment -> QnxtAuthorizationBackend (stub)                                        [external core]
```

- `Backends/IAuthorizationBackend.cs` — the domain seam (create / get-by-number
  / update-status).
- `Backends/ChoAuthorizationBackend.cs` — **Replace**, production; thin layer
  over the existing repository. Records append-only `StatusHistory`.
- `Backends/QnxtAuthorizationBackend.cs` — **Augment**, documented stub
  (throws; no fake SOAP). Replaces the PR #1143 `IAuthorizationAdapter`.
- `Backends/AuthorizationBackendSelector.cs` — routes by
  `Cms0057:Authorization:OperatingMode`; no silent fallback.
- `GET /api/authorizations/backend-status` reports the active mode/backend.

The acceptance suite exercises the **real** `ChoAuthorizationBackend` against an
in-memory repository *fixture* (test-only), so the production domain workflow —
not a parallel acceptance-only path — is what is proven.

## IG pins

Per the locked rule facts, the target IG family is **Da Vinci CRD / DTR / PAS
STU 2.2.x**, with PDex / CARIN BB / US Core as declared by
`MetadataController`. The code references canonical (unversioned) profile URLs:

| IG | Canonical referenced in code | File |
| --- | --- | --- |
| Da Vinci DTR | `…/davinci-dtr/StructureDefinition/dtr-std-questionnaire` | `DtrService.cs` |
| Da Vinci PAS | `…/davinci-pas/StructureDefinition/profile-claimresponse`, `…/extension-reviewAction` | `PasResponseBuilder.cs` |
| Da Vinci CRD | `…/davinci-crd/CodeSystem/temp` (card topics) | `CrdService.cs` |
| US Core / PDex | `us-core-patient`, PDex profiles named in compliance summaries | `Cms0057ComplianceChecker.cs` |
| CapabilityStatement | FHIR R4 (4.0.1), SMART-on-FHIR security | `MetadataController.cs` |

---

## Rule split — already-live operational rules vs 2027 API go-live

**Operational since 2026-01-01 (not QHP-on-FFE for the clocks):**
72-hour expedited / 7-calendar-day standard decisions; specific denial reason
(never a bare "not medically necessary"). PA metrics posted to a public website
by March 31 each year (first cycle CY2025 due 2026-03-31); metrics exclude
drugs.

**FHIR APIs due 2027-01-01:** Patient Access (incl. PA data except drugs),
Provider Access, Payer-to-Payer, Prior Authorization (CRD/DTR/PAS).

QHP issuers on FFEs are exempt from the 72h/7d clocks, but **not** from the
specific-reason, metrics, or API obligations.

---

## Surface inventory

Mode column: current operating mode of that surface in the default build.

### FHIR surface — `src/services/fhir-service`

| Path | Key types | Mode | Supports |
| --- | --- | --- | --- |
| `Program.cs`, `appsettings.json` | `FhirAdapters:Mode=Demo`; `Cms0057:{Crd,Dtr,PasAutoAdjudication}` | Demo | all FHIR scenarios |
| `Controllers/CrdController.cs` | `ExecuteHook`, `Discovery` (CDS Hooks order-select/order-sign) | Demo | PAS-01 |
| `Services/CrdService.cs` | `CrdService : ICrdService`, card builders, terminology translate | Demo | PAS-01 |
| `Services/CrdClassificationStore.cs` | `ICrdClassificationStore` (IMemoryCache) | Demo | PAS-01 |
| `Controllers/DtrController.cs` | Questionnaire/QuestionnaireResponse CRUD, `$questionnaire-package` | Demo | PAS-02 |
| `Services/DtrService.cs` | `DtrService : IDtrService`, 5 seeded Questionnaires (incl. Draft medication PA) | Demo (in-memory) | PAS-02, PAS-08 |
| `Controllers/PasController.cs` | `Claim/$submit` and `Claim/$inquire` | Demo | PAS-03/04/05/06 |
| `Services/PriorAuthorizationInquiry.cs`, `Services/PriorAuthorizationInquiryService.cs` | read-only projection of the authoritative authorization record; corroborating-key lookup; uniform refusal | Demo | PAS-04 inquiry / status | **PASSABLE** | **GAP** | Da Vinci PAS `Claim/$inquire` at `POST fhir/r4/Claim/$inquire`, projecting the SAME authorization record `$submit` writes — no inquiry-specific store, no second status field. Request is a PAS Bundle carrying a `Claim` (`use=preauthorization`); response is a Bundle carrying a `ClaimResponse` on the PAS profile, built by the same `PasResponseBuilder`. Status maps deterministically and totally (Submitted/InReview → queued `pending`; Pended/A4 → queued `pended-additional-information` + X12 306 reviewAction; Approved/A1 → complete `approved`; Modified/A2 → partial `modified`; Denied/A3 → complete `denied` + coded reason; Expired → complete `expired`; Cancelled → cancelled), so pending, pended-for-information, approved and denied are all distinguishable without claiming a CDex round-trip. Reads live committed state on every call, so a status changed since submission is the status returned. **Read-only by contract** — the store seam exposes no write method at all (asserted structurally), so repetition cannot create a record, move a status, restart a clock or trigger a payer submission. Tenant from the authenticated context and re-checked on the record; an authorization number alone never suffices — a corroborating member or provider key must match. Unknown / wrong-tenant / not-yours return one identical 404 `OperationOutcome`, category kept in a PHI-free audit line. CapabilityStatement advertises `submit` and `inquire` on `Claim`, pinned by test. Limitation: authorization records carry no concurrency token, so reads are last-write-wins committed state. QNXT status inquiry absent |
| `Services/PasAutoAdjudicator.cs` | `IPasAutoAdjudicator.TryDecideAsync` (rule engine + enrollment gate) | Demo | PAS-03 |
| `Services/PasResponseBuilder.cs` | approved/denied/pended ClaimResponse (PAS profile, X12 A4) | Demo | PAS-03/05/06/07 |
| `Services/Cms0057ComplianceChecker.cs` | `ICms0057ComplianceChecker`, `CheckPriorAuthTimeline` (72h/7d) | Demo | PAS-03/05/06, PAT-02/03 |
| `Mappers/PatientAccessMapper.cs` | member→US Core Patient, payment→CARIN EOB | Demo | PROV-01, PAT-01 |
| `Services/IPatientAccessDataProvider.cs` | `MockPatientAccessDataProvider` (synthetic pat-001..003) | Demo | PROV-01/02, PAT-01 |
| `Controllers/SmartConfigurationController.cs` | `.well-known/smart-configuration` | Demo (issuer configurable) | SEC-01 |
| `Controllers/MetadataController.cs` | R4 CapabilityStatement, SMART-on-FHIR security | Demo | SEC-01 (IG pins) |
| `Services/FhirAdapterStatusService.cs` | `/adapter-status`; PayerToPayer = **Demo** (inbound respond + `$member-match` + outbound initiation + durable ingestion) | Demo | P2P-01, P2P-02, P2P-04, mode evidence |
| `Services/PayerToPayer/PayerToPayerExchangeService.cs` | inbound respond: tenant-scoped resolution, opt-in gate, CHO-data export | Demo | P2P-01 |
| `Services/PayerToPayer/PayerToPayerMemberMatchService.cs` | `Patient/$member-match`: deterministic identity + coverage selection | Demo | P2P-04 |
| `Services/PayerToPayer/Outbound/PayerToPayerOutboundService.cs` | outbound initiation: member/coverage context → directory → opt-in gate → remote `$member-match` → export → validation → exchange state + audit | Demo | P2P-02 |
| `Services/PayerToPayer/Outbound/PayerToPayerEndpointResolver.cs` | `IPayerToPayerEndpointResolver` + config directory (`Cms0057:PayerToPayerOutbound`); tenant-scoped, HTTPS-only, fail-closed (SSRF boundary) | config | P2P-02 |
| `Services/PayerToPayer/Outbound/IPayerToPayerRemoteClient.cs` | transport seam + `IPayerToPayerCredentialProvider` (default: no credential) | seam | P2P-02 |
| `Services/PayerToPayer/Outbound/HttpPayerToPayerRemoteClient.cs` | named `HttpClient` (`AllowAutoRedirect=false`, response cap, no payload/credential logging) | Demo (no payer onboarded by default) | P2P-02 |
| `Services/PayerToPayer/Outbound/PayerToPayerResponseReader.cs` | peer-response parsing + member-consistency validation + source `Provenance` stamp | Demo | P2P-02 |
| `Services/PayerToPayer/Outbound/PayerToPayerOutboundExchangeStore.cs` | exchange state + idempotency key; **in-process store** (durable persistence is follow-up) | Demo (in-memory) | P2P-02 |
| `Services/PayerToPayer/Ingestion/PayerToPayerPackageIngestionService.cs` | durable ingestion of a validated package: classify → normalize references → stage → commit; exchange completes only after the commit | Demo | P2P-02 |
| `Services/PayerToPayer/Ingestion/PayerToPayerImportPolicy.cs` | supported-type inventory (member history / administrative reference / unsupported) + deterministic import key | Demo | P2P-02 |
| `Services/PayerToPayer/Ingestion/PayerToPayerReferenceNormalizer.cs` | intra-package reference rewriting (relative + absolute); unresolvable and contained references left verbatim | Demo | P2P-02 |
| `Services/PayerToPayer/Ingestion/PayerToPayerImportRepository.cs` | import store **separate from CHO-authoritative data**; staged-then-committed, tenant + member scoped | Demo (in-memory) | P2P-02 |
| `Services/PayerToPayer/Ingestion/MongoPayerToPayerImportRepository.cs` | durable MongoDB store; unique index on (tenantId, importKey), single-document ledger commit | live when `MongoDb:ConnectionString` set | P2P-02 |
| `Controllers/PayerToPayerOutboundController.cs` | `POST fhir/r4/PayerToPayer/$initiate` (payer id only — never a URL); thin routing | Demo | P2P-02 |
| `Controllers/CommunicationController.cs` | Appeal-note → FHIR Communication (**not** CDex additional-info) | Demo | PAS-07 (GAP note) |

### QNXT / operating-mode adapters (mostly stubs)

| Path | Type | Mode | Note |
| --- | --- | --- | --- |
| `src/services/claims-service/Adapters/QnxtClaimAdapter.cs` | `QnxtClaimAdapter : IClaimAdapter` | **stub** | throws `NotImplementedException` (PAT-01 GAP) |
| `src/services/provider-service/Adapters/QnxtProviderAdapter.cs` | `QnxtProviderAdapter : IProviderAdapter` | **stub** | throws `NotImplementedException` (PROV GAP) |
| `src/services/provider-service/Adapters/QnxtOrganizationAdapter.cs` | `QnxtOrganizationAdapter` | **stub** | throws `NotImplementedException` |
| `src/services/benefit-plan-service/Adapters/QnxtBenefitPlanAdapter.cs` | `QnxtBenefitPlanAdapter : IBenefitPlanAdapter` | **stub** | throws `NotImplementedException` (PAS-01 GAP) |
| `src/services/idcard-service/Adapters/QnxtIdCardAdapter.cs` | `QnxtIdCardAdapter : IIdCardAdapter` | Augment | best-effort mirror; not a 0057 scenario |
| `src/services/authorization-service/Backends/QnxtAuthorizationBackend.cs` | `QnxtAuthorizationBackend : IAuthorizationBackend` | **Augment stub** | throws `NotImplementedException`; selected only when configured for Augment. Integration GAP, not a product GAP. |

**Authorization backend (this PR).** The authorization slice now routes through
`IAuthorizationBackend` selected by operating mode (see the vertical-slice
section above), rather than the flat `IAuthorizationAdapter` from PR #1143
(removed). **Replace** binds `ChoAuthorizationBackend` (the CHO-native system of
record over `IAuthorizationRepository` → Cosmos/Mongo) and is the default;
**Augment** binds `QnxtAuthorizationBackend` (stub). So PAS-03 is **product
PASSABLE** on CHO Replace and **integration GAP** on QNXT Augment — two
dimensions, not one.

### Other surfaces

| Path | Key types | Mode | Note |
| --- | --- | --- | --- |
| `src/services/authorization-service` | `AuthorizationsController` (`POST`, `GET number/{n}`, `GET search`, `PUT {id}/status`, `POST {id}/response`, `GET summary`), `Authorization` (`Status`, `DenialReasonCode`, `SubmittedDate`, `ReviewedDate`), `AuthorizationsSummaryCalculator.CalculateTurnaroundDays` | live (CHO) | PAS-04 status, METRICS-01 |
| `src/services/consent-service` | `ConsentsController` (+ `GET consents/authorization-snapshots`), `Consent` (`MemberId`, `ConsentType`, **`PurposeOfUse`**, `Status`, `EffectiveAt`/`ExpiresAt`), `ConsentStateMachine` (Draft→Active→Revoked/Expired) | live (CHO) | PROV-03, P2P-03, CONSENT-01 |
| `src/services/shared/CloudHealthOffice.Consent.Contracts` | `ConsentPurposeOfUse`, `ConsentAuthorizationSnapshot`, `ConsentDecision`/`ConsentAuthorizationReason`, `ConsentAuthorizationPolicy.Evaluate` (pure) | live (CHO) | P2P-03, CONSENT-01 |
| `src/services/fhir-service/Services/Consent` | `IConsentSource`, `IConsentEvaluator`, `RegistryConsentEvaluator` (one fail-closed registry read + one policy, every purpose) | live (CHO) | P2P-03, CONSENT-01 |
| `src/services/fhir-service/Services/ProviderAccess` | `IProviderAttributionSource`/`ConfiguredProviderAttributionSource`, `IProviderAccessAuthorizationService`/`ProviderAccessAuthorizationService`, `ProviderAccessAuthorizationFilter` (global MVC filter) | live (CHO) | PROV-01/02/03, CONSENT-01 |
| `src/services/tenant-service` | adapter platform config (`cho`/`qnxt`) | n/a | mode routing |
| `api/openapi/prior-auth-api.yaml` | PA intake/status contract | n/a | reference |
| `src/fhir/*` (TypeScript CRD/DTR/PAS) | legacy prototype | **legacy** | `fhir-service` `Program.cs` is the system of record; TS is not in the C# runtime path — harness targets the C# services |

---

## Machine-readable source of truth & CI evidence

The status of every scenario is declared in one machine-readable manifest —
**`tests/Cms0057Acceptance.Tests/scenarios.json`** (`schemaVersion: 1`). It is the
single source of truth for the two-dimension status; the human table below is a
derived view of it. Statuses are constrained to `PASSABLE | PARTIAL | GAP | N/A`.

Three things stay in sync with the manifest, so a QNXT integration gap is never
presented as a Cloud Health Office product gap:

- **The acceptance suite** validates it — `ScenarioManifestTests` reconciles
  every `[Trait("Scenario"/"Backend"/"Kind")]` against the manifest (unknown or
  duplicate ids, invalid statuses, a scenario silently losing all its tests, or
  a scenario declared PASSABLE for a backend but backed only by GAP-assertion
  tests all fail the build).
- **The evidence generator** — `tools/Cms0057Evidence` — reads the manifest, the
  acceptance-suite TRX, and the suite's traits and emits versioned, deterministic
  evidence (`cms0057-evidence.json` / `.md` / `.html`) bound to the tested commit
  SHA. It keeps **declared capability status** separate from **test execution
  status**: a passing GAP-assertion test confirms the gap and never becomes
  PASSABLE.
- **CI** — the `CMS-0057-F Acceptance Evidence` workflow runs the suite, generates
  the evidence, and uploads it as the `cms0057-acceptance-evidence-<sha>` artifact.

`PASSABLE` means the repository's defined acceptance scenario is supported by the
tested implementation. It is not a CMS certification and does not by itself
establish production readiness for a specific payer deployment.

### When the evidence regenerates

The `CMS-0057-F Acceptance Evidence` workflow runs not only when the acceptance
suite, the manifest, this inventory, or the evidence tooling change, but also when
runtime/domain code that can change CMS-0057-F behavior changes — the FHIR,
authorization, member, provider, claims, benefit-plan, consent, and smart-auth
services, plus the operating-mode and prior-auth-rule engines. So a behavior change
that never touches the acceptance project still refreshes the evidence. The path
list lives in the workflow file; keep it aligned as the surface grows.

### Raw CI evidence vs. sanitized public evidence

There are two distinct artifacts, and they are not interchangeable:

- **Raw CI evidence** (`cms0057-evidence.json` / `.md` / `.html` + the TRX) is the
  full internal record — supporting test names, rationales, workflow run identity,
  execution status per backend. It stays a CI build artifact; it is **not** served
  from the website.
- **Sanitized public evidence** (`cms0057-public-evidence.json`) is a small
  allow-list projection built by `tools/Cms0057Evidence --public-output`. It is
  constructed field by field — the raw report is never serialized and stripped —
  so it can only ever contain: schema version, evidence status, commit SHA (+ short
  + a durable commit URL), generated timestamp, test-data classification, framework,
  FHIR version, scenario count, a test-execution summary (passed/failed/skipped),
  independent **Replace** (product) and per-backend **Augment** (integration)
  declared-status counts, a per-scenario declared-status matrix, and the
  disclaimers. It carries **no** PHI, member identifiers, test fixtures, tenant
  identifiers, secrets, connection strings, customer names, vendor credentials,
  stack traces, file paths, internal hostnames, or QNXT field mappings. A run with
  any failed acceptance test cannot produce it (the projector refuses).

### Latest published evidence

On `main`, after a fully passing, reconciled run, the workflow's `publish` job
commits the sanitized snapshot to
`src/site/insights/cms-0057-f/cms0057-public-evidence.json` (narrow `contents:
write`; that commit touches only `src/site/**`, so it deploys via Pages without
re-triggering evidence generation). The acceptance-scenarios page renders it under
**Latest published evidence**, showing the source revision and the generation date.
It is deliberately labelled *latest published* — not *current* — because the
published snapshot and the deployed site revision can diverge; the source SHA on the
page is the authority for what was actually tested. Pull-request runs validate and
upload evidence but never update the published snapshot.

**Follow-up (not in this change):** an immutable per-SHA evidence release (a
`cms0057-evidence/<sha>/…` tree or a tagged release) could make each snapshot
permanently addressable. It is intentionally deferred to avoid tag/commit spam
until there is a clear need; the sanitized snapshot already carries the tested SHA.

## Traceability table

Two dimensions per scenario: **CHO Replace** = Cloud Health Office as the
native/authoritative backend (product capability); **QNXT Augment** = the
external-core integration (integration capability). "n/a" = the scenario has no
external-core dependency (no vendor adapter involved).

| Scenario | CHO Replace (product) | QNXT Augment (integration) | Code path / notes |
| --- | --- | --- | --- |
| PAS-01 CRD | **PASSABLE** | **GAP** | `CrdController` + `CrdService` + CHO rule store; QNXT benefit adapter stub |
| PAS-02 DTR | **PASSABLE** | n/a | `DtrController` + `DtrService` (seeded, in-memory) |
| PAS-03 PA submit | **PASSABLE** | **GAP** | `ChoAuthorizationBackend` persists + retrieves via `IAuthorizationRepository`; `QnxtAuthorizationBackend` stub |
| PAS-04 inquiry/status | **PARTIAL** | **GAP** | `ChoAuthorizationBackend.GetByNumberAsync` + `AuthorizationsController` status persisted; FHIR PAS `$inquire` **GAP** |
| PAS-05 specific denial | **PASSABLE** | **GAP** | `PasResponseBuilder` coded error; `ChoAuthorizationBackend` persists coded denial reason |
| PAS-06 decision timeframe | **PASSABLE** | n/a | `Cms0057ComplianceChecker.CheckPriorAuthTimeline` (72h/7d); persisted status/decision history |
| PAS-07 CDex additional-info | **PARTIAL** | n/a | pended X12 A4 via `PasResponseBuilder`; CDex round-trip **GAP** |
| PAS-08 drug exclusion | **PASSABLE** | n/a | `ChoAuthorizationBackend.CreateAsync` enforces benefit exclusions (`BenefitExclusion` catalog + `DrugExclusionEvaluator`): a plan-excluded drug / pharmacy service type is persisted as a coded denial (A3) with audit history, not approvable |
| PROV-01 attributed pull | **PASSABLE** | **GAP** | `MockPatientAccessDataProvider` + `PatientAccessMapper`; QNXT provider adapter stub |
| PROV-02 attribution enforce | **PASSABLE** | n/a | data layer returns no data for non-attributed member; 403-class via middleware (SEC-01) |
| PROV-03 opt-out honored | **PASSABLE** | n/a | `ConsentStateMachine` Active→Revoked |
| P2P-01 inbound respond | **PASSABLE** | **GAP** | `PayerToPayerExchangeService` — tenant-scoped member resolution + opt-in gate + CHO-data FHIR export (Patient + Coverage + CARIN EOBs, 5-year lookback, audit); QNXT P2P integration absent |
| P2P-02 outbound initiate | **PASSABLE** | **GAP** | `PayerToPayerOutboundService` — CHO, as the member's new payer, resolves the member + prior-payer coverage context from CHO-owned data, resolves the target payer through a trusted tenant-scoped HTTPS-only directory (payer id in, never a caller URL), enforces the opt-in server-side **before anything leaves CHO**, calls the remote `$member-match` through a transport seam, requests the export only after a single member resolves, validates the returned Bundle for member consistency, and stamps source `Provenance`; structured outcomes + idempotent exchange record + audit without demographics/payloads/URLs. A validated package is **durably ingested** (`PayerToPayerPackageIngestionService`) into an import store kept separate from CHO-authoritative data: deterministic import keys (tenant + member + source payer + type + source id) make replay non-duplicating and never merge two payers' records, references are normalized, and a staged-then-committed ledger means a failed ingestion leaves the member record untouched. Ingestion covers the types CHO's FHIR surface serves (EOB, Claim, ClaimResponse, Encounter, DocumentReference; Patient/Coverage/Organization/Practitioner/PractitionerRole/Provenance as reference-only); **USCDI clinical types are archived, not ingested** (the PAT-02 gap), imported data is **not yet projected into the read APIs**, live payer onboarding (SMART Backend Services / UDAP / mTLS) is deployment integration, and exchange state is in-process. QNXT-backed outbound initiation absent |
| P2P-03 consent enforcement | **PASSABLE** | **GAP** | Payer-to-Payer authorization is a first-class purpose (`ConsentPurposeOfUse.PayerToPayerExchange`) on the one consent registry, orthogonal to `ConsentType` and aligned to FHIR `Consent.provision.purpose`. One pure `ConsentAuthorizationPolicy` decides for both directions: the snapshot must match tenant **and** member, carry the requested purpose, be `Active`, and be in force at the evaluation instant (the effective period is applied, not trusted from the stored status); ties resolve deterministically (latest-expiring, then highest version) and refusals carry a specific reason (`NoConsentForPurpose`, `Revoked`, `Expired`, `NotYetEffective`, `NotActivated`, `NoConsentOnRecord`). An Active consent is **not** sufficient by itself and a Provider Access consent does not authorize P2P — the separation is data, not a controller route. Enforced server-side in both directions against the same registry (`HttpConsentRegistryConsentSource` → `consent-service`, config-backed source only as the Demo fallback in the same shape): inbound before any member data is assembled; outbound before the remote `$member-match` (so an unauthorized member's identity never leaves CHO) **and again immediately before the export** (so a revocation in flight stops the data request). No request type carries a consent field — asserted by reflection — so no caller can self-attest. The exchange and both audit entries record `AuthorizingConsentId` + `ConsentDecisionReason`, and a retry clears the decision and re-asks. Fail-closed everywhere: blank ids, an unreadable registry, and `Unspecified` purpose all deny. Historical consent deserializes to `Unspecified` and is **not** reinterpreted. QNXT-backed P2P consent integration absent |
| P2P-04 member-match/concurrent | **PASSABLE** | **GAP** | `PayerToPayerMemberMatchService` — FHIR `Patient/$member-match`; deterministic strong-vs-supporting identity policy (member/subscriber id or SSN, or family + DOB; any contradiction fails closed), tenant-scoped, anti-enumeration sufficiency gate; concurrent/overlapping coverage selected by requested payer/subscriber + effective date (overlaps without a discriminator refuse); resolved context feeds the P2P-01 export; QNXT P2P integration absent |
| PAT-01 member claims / CARIN EOB | **PASSABLE** | **GAP** | `MockPatientAccessDataProvider` payments → `PatientAccessMapper` EOB; QNXT claim adapter stub |
| PAT-02 US Core clinical | **PARTIAL** | n/a | demographics validate; USCDI clinical is an external store |
| PAT-03 PA data except drugs | **PARTIAL** | n/a | `ClaimResponse` is a supported PA-data type; retention job absent |
| SEC-01 SMART/OAuth | **PARTIAL** | n/a | `SmartConfigurationController` (endpoints, scopes, S256); IdP per engagement |
| CONSENT-01 single registry | **PASSABLE** | n/a | One registry, one aggregate, one lifecycle, one purpose axis — and **both** purposes now enforced server-side through it. Payer-to-Payer (P2P-03) and Provider Access reach their answers via the same `IConsentEvaluator` over the same pure `ConsentAuthorizationPolicy`, differing only in the purpose asked for, so neither can drift more permissive and a consent for one satisfies nothing for the other. Provider Access authorization is composed by `ProviderAccessAuthorizationService` and enforced by a **global** MVC filter covering every member-scoped resource the SMART layer serves (a structural test pins the two inventories together, so a new controller cannot bypass it), placed after tenant resolution and before any action body so PHI is never assembled for an unauthorized request; FHIR operations are excluded because Payer-to-Payer authorizes those itself. Four independent, mandatory controls — authentication and SMART scope (upstream middleware, not re-implemented), provider/member attribution, active `ProviderAccess`-purpose consent — each able to refuse alone, composed fail-closed. Tenant and member must both match the consent; tenant comes from the authenticated context. Registry faults, an empty catalog, a missing member context and an unidentified caller all deny rather than degrading to SMART-plus-attribution. Denials are externally uniform (one 403 `OperationOutcome`, byte-identical bodies) so "not attributed" / "no consent" / "no such member" cannot be told apart and used to enumerate; the structured category stays in the PHI-free audit record. **Attribution is served from a configured panel catalog** — real, fail-closed enforcement, but no live roster feed from a payer source system is wired up (engagement integration behind `IProviderAttributionSource`) |
| METRICS-01 public metric set | **PASSABLE** | **GAP** | metrics derive from the persisted CHO authorization (`ChoAuthorizationBackend` + `AuthorizationsSummaryCalculator`); QNXT auth data stub. Full public-metrics *extract job* is still engagement work. |

**What changed in this PR:** PAS-03/PAS-04/PAS-05/PAS-06 and METRICS-01 are now
scored on the CHO-native authorization backend (Replace) rather than a flat GAP.
PAS-03 product capability moved GAP → **PASSABLE**; METRICS-01 product moved
PARTIAL → **PASSABLE** (derives from the persisted record, not a test-only
object). The QNXT column stays GAP: that integration is engagement work.

**What changed in the PAS-04 PR:** the Da Vinci PAS `Claim/$inquire` operation
is now served, projecting the existing authorization record onto a standards-
shaped `ClaimResponse`. PAS-04 moved PARTIAL → **PASSABLE**, so CHO Replace
declares **17 PASSABLE / 4 PARTIAL / 0 GAP** (generator-computed from the
manifest — nothing here is hard-coded). Remaining PARTIAL: PAS-07, PAT-02,
PAT-03, SEC-01.

**PAS-07 deliberately stays PARTIAL.** `$inquire` *reports* that a decision is
pended awaiting information — CHO already knows that from the A4 review decision
— but it neither requests documentation nor accepts it. That round-trip is what
CDex is, and it is not implemented. The PAS-07 GAP test was rewritten to stop
keying on the string `$inquire` (now a legitimate operation) and to assert what
actually remains missing: no additional-information intake action.

**Write-side fixes this required.** Status was not inquirable as shipped:
`preAuthRef` was set only on approvals, and a pended submission persisted an
authorization number that was never returned to the caller, so the outcome that
most needs following up had no tracking handle. Approved, denied and pended
responses now all carry the persisted number; the denial code and reason, the
approved period, and the service lines from the submitted Claim are persisted
too; and the authorization HTTP client now propagates the tenant header, without
which authorization-service falls back to its default partition and reads and
writes cross tenants.

**Zero GAPs still does not mean complete CMS-0057-F compliance.** This inventory
is implementation evidence, not certification or attestation, and the
QNXT/external-core column is unchanged — PAS-04 augment stays **GAP**.

**What changed in the CONSENT-01 PR:** Provider Access stopped being governed by
SMART scopes alone and now composes four independent, mandatory controls, the
consent one running on the same registry and policy as Payer-to-Payer.
CONSENT-01 moved PARTIAL → **PASSABLE**, so CHO Replace declares **16 PASSABLE /
5 PARTIAL / 0 GAP** (generator-computed from the manifest — nothing here is
hard-coded). Remaining PARTIAL: PAS-04, PAS-07, PAT-02, PAT-03, SEC-01.

Provider Access requires, independently and mandatorily:

1. **authentication** — middleware, unchanged;
2. **appropriate SMART authorization** — `SmartScopeEnforcementMiddleware`,
   unchanged and not re-implemented;
3. **provider/member attribution** — the member must be on this provider's panel;
4. **an active purpose-scoped `ProviderAccess` consent** — evaluated at the
   authorization instant through the one registry.

None implies another, and the composed decision fails closed on any refusal.

**A discrepancy worth recording:** the brief for this work assumed attribution
already existed and was to be preserved. It did not. There was **no attribution
code in the repository** — PROV-02's "attribution enforcement" test asserted a
dictionary miss on an unknown member id, and the capability text describing
Provider Access as "governed by attribution plus SMART scopes" was aspirational.
Attribution was therefore built as a real control here, backed by a configured
panel catalog that fails closed. It is genuine enforcement, but **no live roster
feed from a payer source system is wired up** and nothing claims one is; that
remains engagement integration behind `IProviderAttributionSource`. PROV-01/02/03
keep their existing statuses — this change does not re-score them on the back of
the consent work.

**Zero GAPs still does not mean complete CMS-0057-F compliance.** This inventory
is implementation evidence, not certification or attestation, and the
QNXT/external-core column is unchanged.

**What changed in the P2P-03 consent PR:** Payer-to-Payer authorization stopped
being "any Active consent" and became a first-class purpose on the existing
registry. P2P-03 product capability moved PARTIAL → **PASSABLE**, so CHO Replace
now declares **15 PASSABLE / 6 PARTIAL / 0 GAP** (the manifest is the source of
truth; the evidence generator computes the totals — nothing here is hard-coded).
Six scenarios remain PARTIAL: CONSENT-01, PAS-04, PAS-07, PAT-02, PAT-03, SEC-01.

Specifically:

* **Payer-to-Payer has its own consent purpose.**
  `ConsentPurposeOfUse.PayerToPayerExchange` is the only value that authorizes an
  exchange, and it is a constant on the gate rather than configuration.
* **Provider Access authorization does not imply Payer-to-Payer authorization.**
  The two purposes are compared as data inside one pure policy, so the separation
  cannot be lost by adding a route or a client.
* **Generic consent no longer satisfies Payer-to-Payer.** `Unspecified`
  authorizes nothing purpose-specific.
* **Consent is enforced server-side.** No Payer-to-Payer request type has a
  consent field, in either direction; an acceptance test asserts that by
  reflection over all four request types.
* **Both directions use the same authoritative registry.** Inbound and outbound
  call one gate over one policy against `consent-service`. There is no
  P2P-local consent store.
* **Revocation semantics are documented and tested** — including the in-flight
  case, where the outbound re-check between `$member-match` and export stops the
  data request. See
  [Consent → Revocation semantics](../architecture/consent.md#revocation-semantics).
* **Migration fails closed.** Historical consent deserializes to `Unspecified`
  and is not backfilled or inferred, so a deployment upgrading to this code
  authorizes no exchange until purposes are recorded. That is intentional on a
  live disclosure path.
* **CONSENT-01 deliberately stays PARTIAL**, with a new GAP test naming the
  reason: the Provider Access read path does not consult the registry.

**Zero GAPs still does not mean complete CMS-0057-F compliance.** This inventory
is implementation evidence, not certification or attestation, and the
QNXT/external-core column is unchanged — P2P-03 augment stays **GAP**.

**What changed in the P2P ingestion PR:** a validated Payer-to-Payer package is
now durably ingested into a CHO import store rather than validated and dropped,
and an exchange reaches `Completed` only after that commit lands. **No scenario
status changed**: P2P-02 was already PASSABLE (its rationale is updated), and
P2P-03, PAT-02, and PAT-03 stay **PARTIAL** — ingestion adds no dedicated
Payer-to-Payer `ConsentType`, does not make CHO serve USCDI clinical resources
(they are archived, not ingested, and not exposed through the read APIs), and
adds no PA-data retention job.

**What changed in the P2P-02 PR:** outbound Payer-to-Payer initiation is
implemented as CHO Replace-mode capability, so P2P-02 product capability moved
GAP → **PASSABLE** and CHO Replace now declares 14 PASSABLE / 7 PARTIAL / 0 GAP.
Zero GAPs is **not** completeness: seven scenarios remain PARTIAL (CONSENT-01,
P2P-03, PAS-04, PAS-07, PAT-02, PAT-03, SEC-01), the QNXT/external-core column is
unchanged (P2P-02 augment stays **GAP** — outbound initiation against a
QNXT-backed deployment is not implemented), and none of this is a CMS
certification or an attestation of full CMS-0057-F compliance. Specifically for
P2P-02: **P2P-03 stays PARTIAL** (opt-in is still a generic Active consent, with
no dedicated Payer-to-Payer `ConsentType`); the received package is retrieved,
validated, provenance-stamped, and audited but **not written into the CHO member
record** (durable ingestion is follow-up); exchange state lives in an in-process
store; and connecting to any *named* payer requires that payer's onboarding —
endpoint directory entry plus transport credentials (SMART Backend Services /
UDAP client registration, mTLS) behind `IPayerToPayerCredentialProvider`, which
supplies nothing by default rather than fabricating a credential.

---

## Executable harness

`tests/Cms0057Acceptance.Tests/` (xUnit, in the solution
`cloudhealthoffice-main.sln`). Every scenario is tagged
`[Trait("Scenario","PAS-01")]`; the authorization slice and the QNXT stubs also
carry `[Trait("Backend","Replace")]` or `[Trait("Backend","Augment")]` so the
two dimensions can be run independently, and integration/absence gaps carry
`[Trait("Kind","GAP")]`.

Filter by scenario id or backend, e.g. a `dotnet test` filter expression of
`Scenario=PAS-03` (both dimensions), `Backend=Replace` (product capability), or
`Backend=Augment` (integration capability). Replace-mode scenarios pass against
the real `ChoAuthorizationBackend`; Augment GAP tests pass by confirming the
QNXT backend is still a stub — when a QNXT integration ships for real, the
matching Augment test fails and must be replaced with a live-mode acceptance
test. Demo (synthetic FHIR projection) scenarios remain distinct from Replace.
