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
  projections), IG handling, consent/identity plumbing, the CHO-native
  authorization store, and this acceptance harness. Runs in **Demo/Cho mode by
  default** (`FhirAdapters:Mode = "Demo"`, synthetic data).
- **Per-engagement (QNXT adapter)**: binding a customer's system of record.
  **Most QNXT adapters today are stubs that throw `NotImplementedException`.**
  The suite asserts those stubs rather than papering over them.

Nothing here claims "production ready against QNXT". The QNXT adapters are
stubs; see the GAP rows.

## Operating mode

`docs/architecture/OPERATING-MODE.md` defines Augment / Replace / Legacy per
tenant/engine. Acceptance tests run in **Cho/Demo** by default and separately
assert the QNXT bindings are still stubs (`GapAdapterTests`). Default for
unconfigured engines is Replace (CHO authoritative); the FHIR adapter layer
defaults to Demo + synthetic so a deployment can never silently look live
(`FhirAdapterOptions`).

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
| `src/services/authorization-service/Adapters/QnxtAuthorizationAdapter.cs` | `QnxtAuthorizationAdapter : IAuthorizationAdapter` | **stub (added by this suite)** | throws `NotImplementedException` (PAS-03 GAP). Was **missing** before this work — see below. |

**PAS-03 QNXT finding:** there was **no** authorization-service QNXT adapter.
Per the adapter pattern (`I*Adapter` + `Cho*Adapter` + `Qnxt*Adapter`) this
suite added `IAuthorizationAdapter` + `QnxtAuthorizationAdapter` as a documented
stub with a single TODO (no fake QNXT SOAP client), and a GAP test. The
CHO-native authorization path (`AuthorizationsController` +
`AuthorizationRepository`) remains the default and is what PAS-03's happy path
runs against.

### Other surfaces

| Path | Key types | Mode | Note |
| --- | --- | --- | --- |
| `src/services/authorization-service` | `AuthorizationsController` (`POST`, `GET number/{n}`, `GET search`, `PUT {id}/status`, `POST {id}/response`, `GET summary`), `Authorization` (`Status`, `DenialReasonCode`, `SubmittedDate`, `ReviewedDate`), `AuthorizationsSummaryCalculator.CalculateTurnaroundDays` | live (CHO) | PAS-04 status, METRICS-01 |
| `src/services/consent-service` | `ConsentsController`, `Consent` (`MemberId`, `ConsentType`, `Status`), `ConsentStateMachine` (Draft→Active→Revoked/Expired) | live (CHO) | PROV-03, P2P-03, CONSENT-01 |
| `src/services/tenant-service` | adapter platform config (`cho`/`qnxt`) | n/a | mode routing |
| `api/openapi/prior-auth-api.yaml` | PA intake/status contract | n/a | reference |
| `src/fhir/*` (TypeScript CRD/DTR/PAS) | legacy prototype | **legacy** | `fhir-service` `Program.cs` is the system of record; TS is not in the C# runtime path — harness targets the C# services |

---

## Traceability table

| Scenario | Code path | Adapter mode | Varies | Status |
| --- | --- | --- | --- | --- |
| PAS-01 CRD | `CrdController` + `CrdService` + CHO classification | Demo; QNXT benefit adapter stub | YES | **PASSABLE** (CHO); QNXT **GAP** |
| PAS-02 DTR | `DtrController` + `DtrService` (seeded, in-memory) | Demo | no | **PASSABLE** |
| PAS-03 PA submit | `PasController` `$submit` + `PasResponseBuilder` + `Cms0057ComplianceChecker`; persist → authorization-service | Demo; QNXT auth adapter stub | YES | **PASSABLE** (CHO); QNXT create-auth **GAP** |
| PAS-04 inquiry/status | tracking id from `PasResponseBuilder`; status via `AuthorizationsController` `number/{n}` + `search` | Demo/CHO | YES | **PARTIAL** — FHIR PAS `$inquire` **GAP** |
| PAS-05 specific denial | `PasResponseBuilder.BuildDeniedResponse` coded `ClaimResponse.error` | Demo | no | **PASSABLE** |
| PAS-06 decision timeframe | `Cms0057ComplianceChecker.CheckPriorAuthTimeline` (72h/7d), explicit-Z timestamps | Demo | no | **PASSABLE** |
| PAS-07 CDex additional-info | pended X12 A4 via `PasResponseBuilder`; `CommunicationController` is appeal-notes only | Demo | no | **PARTIAL** — CDex round-trip **GAP** |
| PAS-08 drug exclusion | `Cms0057ComplianceChecker` (no drug filter); DTR medication PA is a distinct Draft questionnaire | Demo | no | **GAP** — exclusion not enforced in PAS path |
| PROV-01 attributed pull | `MockPatientAccessDataProvider` + `PatientAccessMapper` (US Core / CARIN EOB) | Demo; QNXT provider adapter stub | YES | **PASSABLE** (CHO); QNXT **GAP** |
| PROV-02 attribution enforce | data-layer returns no data for non-attributed member; 403-class via `SmartScopeEnforcementMiddleware` | Demo | no | **PASSABLE** (data layer); middleware = SEC-01 |
| PROV-03 opt-out honored | `ConsentStateMachine` Active→Revoked; revoked ≠ Active | Demo/CHO | no | **PASSABLE** |
| P2P-01 inbound respond | `FhirAdapterStatusService` → PayerToPayer OutOfScope | OutOfScope | — | **GAP** |
| P2P-02 outbound initiate | no enrollment/opt-in initiation hook in product code | OutOfScope | — | **GAP** |
| P2P-03 opt-in enforcement | opt-in modeled as Active consent; no dedicated P2P `ConsentType` | Demo/CHO | — | **PARTIAL** |
| P2P-04 member-match/concurrent | no `$member-match`/concurrent-coverage surface in product code | OutOfScope | — | **GAP** |
| PAT-01 member claims / CARIN EOB | `MockPatientAccessDataProvider` payments → `PatientAccessMapper` EOB | Demo; QNXT claim adapter stub | YES | **PASSABLE** (CHO); QNXT **GAP** |
| PAT-02 US Core clinical | `Cms0057ComplianceChecker` validates US Core Patient; USCDI clinical is external store | Demo | no | **PARTIAL** (demographics PASSABLE; clinical external) |
| PAT-03 PA data except drugs | `ClaimResponse` is a supported PA-data type; retention job absent | Demo | no | **PARTIAL** — retention/1-day-freshness **GAP** |
| SEC-01 SMART/OAuth | `SmartConfigurationController` (endpoints, scopes, S256); IdP per customer | Demo | no | **PARTIAL** (IdP per-engagement) |
| CONSENT-01 single registry | `ConsentStateMachine` + `Consent` (one registry, opt-in/opt-out lifecycle) | Demo/CHO | no | **PARTIAL** — no dedicated Provider-Access/P2P `ConsentType` |
| METRICS-01 public metric set | `AuthorizationsSummaryCalculator` + `Authorization` timestamps/reason codes | Demo/CHO; QNXT auth data stub | YES | **PARTIAL** — building blocks PASSABLE; full extract job engagement work |

---

## Executable harness

`tests/Cms0057Acceptance.Tests/` (xUnit, in the solution
`cloudhealthoffice-main.sln`). Every scenario is tagged
`[Trait("Scenario","PAS-01")]` (etc.); GAP scenarios also carry
`[Trait("Kind","GAP")]` and assert the stub / missing type / OutOfScope mode.

Filter by scenario id with `dotnet test` and a filter expression of
`Scenario=PAS-03`. All non-GAP scenarios pass in Demo/Cho mode. GAP tests pass
by confirming the gap still exists — when a QNXT adapter or missing surface is
implemented for real, the matching GAP test fails and must be replaced with a
live-mode acceptance test.
