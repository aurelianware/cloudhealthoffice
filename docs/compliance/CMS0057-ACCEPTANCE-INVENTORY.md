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
| `Controllers/PasController.cs` | `Claim/$submit` only (no `$inquire`) | Demo | PAS-03/05/06 |
| `Services/PasAutoAdjudicator.cs` | `IPasAutoAdjudicator.TryDecideAsync` (rule engine + enrollment gate) | Demo | PAS-03 |
| `Services/PasResponseBuilder.cs` | approved/denied/pended ClaimResponse (PAS profile, X12 A4) | Demo | PAS-03/05/06/07 |
| `Services/Cms0057ComplianceChecker.cs` | `ICms0057ComplianceChecker`, `CheckPriorAuthTimeline` (72h/7d) | Demo | PAS-03/05/06, PAT-02/03 |
| `Mappers/PatientAccessMapper.cs` | member→US Core Patient, payment→CARIN EOB | Demo | PROV-01, PAT-01 |
| `Services/IPatientAccessDataProvider.cs` | `MockPatientAccessDataProvider` (synthetic pat-001..003) | Demo | PROV-01/02, PAT-01 |
| `Controllers/SmartConfigurationController.cs` | `.well-known/smart-configuration` | Demo (issuer configurable) | SEC-01 |
| `Controllers/MetadataController.cs` | R4 CapabilityStatement, SMART-on-FHIR security | Demo | SEC-01 (IG pins) |
| `Services/FhirAdapterStatusService.cs` | `/adapter-status`; PayerToPayer = **OutOfScope** | Demo | P2P-01, mode evidence |
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
| `src/services/consent-service` | `ConsentsController`, `Consent` (`MemberId`, `ConsentType`, `Status`), `ConsentStateMachine` (Draft→Active→Revoked/Expired) | live (CHO) | PROV-03, P2P-03, CONSENT-01 |
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
| PAS-08 drug exclusion | **GAP** | n/a | no drug-exclusion filter in the PAS path |
| PROV-01 attributed pull | **PASSABLE** | **GAP** | `MockPatientAccessDataProvider` + `PatientAccessMapper`; QNXT provider adapter stub |
| PROV-02 attribution enforce | **PASSABLE** | n/a | data layer returns no data for non-attributed member; 403-class via middleware (SEC-01) |
| PROV-03 opt-out honored | **PASSABLE** | n/a | `ConsentStateMachine` Active→Revoked |
| P2P-01 inbound respond | **GAP** | **GAP** | `FhirAdapterStatusService` → PayerToPayer OutOfScope |
| P2P-02 outbound initiate | **GAP** | **GAP** | no enrollment/opt-in initiation hook |
| P2P-03 opt-in enforcement | **PARTIAL** | **GAP** | opt-in modeled as Active consent; no dedicated P2P `ConsentType` |
| P2P-04 member-match/concurrent | **GAP** | **GAP** | no `$member-match`/concurrent-coverage surface |
| PAT-01 member claims / CARIN EOB | **PASSABLE** | **GAP** | `MockPatientAccessDataProvider` payments → `PatientAccessMapper` EOB; QNXT claim adapter stub |
| PAT-02 US Core clinical | **PARTIAL** | n/a | demographics validate; USCDI clinical is an external store |
| PAT-03 PA data except drugs | **PARTIAL** | n/a | `ClaimResponse` is a supported PA-data type; retention job absent |
| SEC-01 SMART/OAuth | **PARTIAL** | n/a | `SmartConfigurationController` (endpoints, scopes, S256); IdP per engagement |
| CONSENT-01 single registry | **PARTIAL** | n/a | one registry, opt-in/opt-out lifecycle; no dedicated Provider-Access/P2P `ConsentType` |
| METRICS-01 public metric set | **PASSABLE** | **GAP** | metrics derive from the persisted CHO authorization (`ChoAuthorizationBackend` + `AuthorizationsSummaryCalculator`); QNXT auth data stub. Full public-metrics *extract job* is still engagement work. |

**What changed in this PR:** PAS-03/PAS-04/PAS-05/PAS-06 and METRICS-01 are now
scored on the CHO-native authorization backend (Replace) rather than a flat GAP.
PAS-03 product capability moved GAP → **PASSABLE**; METRICS-01 product moved
PARTIAL → **PASSABLE** (derives from the persisted record, not a test-only
object). The QNXT column stays GAP: that integration is engagement work.

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
