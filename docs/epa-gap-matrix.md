# Electronic Prior Authorization (ePA) — Gap Matrix

**Status:** Historical Phase 2 audit with current-status reconciliation annotations (2026-10-19).
**Baseline:** `main` @ `31a462ff`, audited 2026-10-10.
**Original audit gate:** owner review before Phase 3; retained as historical process context.

The baseline findings below are historical, not proof of current defects. Use
[Capability Reconciliation](CAPABILITY_RECONCILIATION.md) for code-verified current
status, focused test results and limits. Neither this record nor a passing
implementation test establishes comprehensive standards conformance. The §11
scenario table remains the evidence generator input; its statuses are not
a blanket production-readiness claim.

---

## 0. How to read this

**Classification** (exactly one per item):

| Label | Meaning |
|---|---|
| **I+T** | Implemented + tested: the behaviour exists on a production code path *and* a test exercises that behaviour (not just constructs the object). |
| **I-U** | Implemented, untested. |
| **P** | Partial: some of the requirement is met; the gap is stated. |
| **S** | Stub: placeholder, `NotImplementedException`, mock/sample data, hard-coded response, or in-memory-only where durability is required. |
| **M** | Missing. |

**Severity** (of a gap or defect):

| Severity | Meaning |
|---|---|
| **Critical** | Produces a wrong or unsafe outcome today (lost decisions, a security hole, claims paid without a valid auth), or blocks a CMS-0057-F requirement that is already in force. |
| **High** | Blocks conformance with a required spec element, or a tenant-isolation gap. |
| **Medium** | Conformance or robustness gap with a workaround, or a latent defect on a non-default path. |
| **Low** | Cosmetic, reporting accuracy, or optional spec element. |

**Effort:** S ≤ 1 day · M 1–3 days · L 1–2 weeks · XL > 2 weeks. These are
rough, for sequencing only.

**Method.** Four parallel read-only audits covered (1) PAS, 278 and UM; (2) CRD
and DTR; (3) CMS-0057-F operational and API requirements; (4) SMART security,
tenancy, CMS-0062-P and public claims. The test projects listed below were run.
One runtime probe confirmed D-01 and one confirmed D-07 (both throwaway, outside
the repo). Every finding carries file:line evidence. Paths use
`FS` = `src/services/fhir-service`, `AS` = `src/services/authorization-service`,
`SA` = `src/services/smart-auth-service`.

**Tests run at baseline (all green):**

| Suite | Result |
|---|---|
| `tests/Cms0057Acceptance.Tests` | 362/362 |
| `CloudHealthOffice.AuthorizationService.Tests` | 54/54 |
| `AS/AuthorizationService.Tests` | 26/26 |
| `PriorAuthRuleEngine.Tests` | 27/27 |
| `FhirService.Tests`, filtered to PAS / CRD / DTR / SmartGap | 112/112 |

The suites pass because **every PAS `$submit` test mocks the adjudicator and
stubs the HTTP hand-off to authorization-service**. No test runs submit → persist
→ inquire → claim. The defects in §3 sit exactly on that untested seam.

**Regulatory clock (for prioritisation).** For impacted payers, CMS-0057-F's
operational provisions apply from 1 January 2026: the 72-hour / 7-day decision
timeframes and specific denial reasons. The first public PA metrics report was
due 31 March 2026. The API provisions apply from 1 January 2027: the Prior
Authorization API, PA data in Patient Access, Provider Access and Payer-to-Payer.
CHO has no live clients, but the operational items are the ones a prospect will
test first.

---

## 1. Executive summary

| Area | Verdict | Headline |
|---|---|---|
| **Da Vinci PAS** | **Implemented core; conformance unverified here** | Submit persists before responding, inquire reads stored outcomes, and service-line decisions are projected (CAP-01/02). Full profile/response conformance and production readiness are separate. |
| **Da Vinci CRD** | **Partially Implemented** | Discovery/cards and explicit uncertainty handling are implemented (CAP-03). Unknown/failed evaluations do not establish no-PA-needed; full member-context and structured coverage conformance remain separate. |
| **Da Vinci DTR** | **Partially Implemented** | Mongo-backed creation, conditional updates, reads, existence checks and searches work across service instances. Complete answer validation, canonical resolution and package semantics remain open (CAP-04/15). |
| **CMS-0057-F timeframes** | **Partial** | Receipt-based UTC targets are persisted and used by tenant-scoped watchdog escalation; PAS urgency is mapped and RFAI does not restart the clock. Program-specific extensions, tighter rules, alerts and tenant-database background support remain open. |
| **CMS-0057-F denial reasons** | **Partial** | Required and persisted for CHO full/partial adverse decisions, with line projections in PAS. X12 `reviewAction.reasonCode` semantics, 278 HCR encoding and notices remain. |
| **CMS-0057-F public metrics** | **Partially Implemented** | Standard/expedited counts, rates, mean/median and timeliness exist. Appeal/extension outcomes remain explicitly untracked; complete annual publication is not established (CAP-06). |
| **Patient / Provider / Payer-to-Payer PA data** | **Missing (PA) / Stub (data)** | No API exposes PA data. Patient and Coverage reads come from member-service and coverage-service when bound to `ChoNative` (CAP-27, ADR 018); Encounter and Claim reads, and the Payer-to-Payer member source, still come from a mock that ignores tenant (G-30, narrowed). Bulk `$export` is a stub. Provider Access is opt-in (D-11). |
| **SMART on FHIR / OAuth2** | **Partially Implemented** | Launch binding, patient/encounter token context and approval withdrawal affecting refresh are implemented and integration-tested (CAP-05). Do not equate those with every SMART conformance requirement or production deployment. |
| **CMS-0062-P readiness** | **Not ready (design only)** | No benefit-channel field, days supply or refills. PAS drops the drug code system. |
| **X12 278 bridging** | **Stub / Missing** | No .NET 278 parser or encoder. An Argo/Python tokenizer feeds a Kafka topic that nothing consumes. |
| **Tenant isolation** | **Scoped fixes verified; remaining areas distinct** | Authenticated PAS tenant selection, DTR search isolation and registered receiving-payer binding are implemented (CAP-08/09/10). Default mocks and payer-specific consent are separate concerns. |
| **Published evidence and copy** | **Overclaims** | The public CMS-0057-F evidence shows 21/21 PASSABLE. Statuses are hand-declared and several backing tests are tautological. Inferno and "compliant" claims have no evidence behind them. See §6. |

---

## 2. Component inventory (reconciled entries identified by CAP IDs)

### 2.1 Components

| Component | Path | Class | Note |
|---|---|---|---|
| PAS `Claim/$submit` | `FS/Controllers/PasController.cs:RecordAndRespondAsync` | I+T (persistence) / P (conformance) | Persists mapped decisions before response; real-host/Mongo seam verified (CAP-01). |
| PAS `Claim/$inquire` | `FS/Controllers/PasController.cs`; `FS/Services/PriorAuthorizationInquiryService.cs` | I+T (scoped) | Submit → persist → inquire is verified over real hosts/Mongo (CAP-01); caller-binding limits remain distinct. |
| PAS response builder | `FS/Services/PasResponseBuilder.cs:AddRequestedServices` | I+T (line outcomes) / P (conformance) | Stored A1/A2/A3/A4 lines and adverse reasons projected; full STU2 semantics separate (CAP-02). |
| PAS auto-adjudicator | `FS/Services/PasAutoAdjudicator.cs` | P | Uses authenticated tenant (CAP-08); other policy-context/gold-card findings remain separate historical assessments pending their own verification. |
| Compliance checker | `FS/Services/Cms0057ComplianceChecker.cs` | P | Structural presence checks only; not IG validation. PAS `Claim` timeline returns `Applicable:false` (:600-611). |
| Authorization (UM) record + API | `AS/Controllers/AuthorizationsController.cs`; `AS/Backends/AuthorizationDecisionPolicy.cs` | I+T | Header and service-line decision validation/persistence, quantities and adverse reasons (CAP-02). |
| Mongo repository (default) | `AS/Repositories/AuthorizationRepositoryMongo.cs` | I+T (scoped) | Real-Mongo persistence, index, line-outcome, concurrency and deadline tests are recorded in CAP-01/02/07/12/21. |
| Cosmos repository (opt-in) | `AS/Repositories/AuthorizationRepository.cs` | I+T | Shared serializer, enums bound in their stored form (D-18 fixed, PR #1297). Emulator tests. |
| ChoAuthorizationBackend | `AS/Backends/ChoAuthorizationBackend.cs` | I+T | Status and decision persistence. |
| QNXT backend | `AS/Backends/QnxtAuthorizationBackend.cs` | S | Every method throws `NotImplementedException` (documented). |
| SLA watchdog | `AS/Services/SlaWatchdogService.cs` | I+T | Per tenant since D-07 (PR #1297); tested on MongoDB and the Cosmos emulator. Thresholds unchanged (G-18, G-19). |
| `GET sla/at-risk` | `AS/Controllers/AuthorizationsController.cs:634-652` | I+T (mocked repo) | Reads the persisted receipt-based deadline; legacy records use a receipt-based compatibility fallback. |
| RFAI / CDex additional info | `rfai-service`, `AS/Services/Rfai/*`, `AS/Consumers/RfaiDocsReceivedConsumer.cs` | I+T | 42 tests. Raised only from reviewer decisions, never from PAS pends. |
| Attachments | `attachment-service` | I-U (auth linkage) | Tenant-scoped; nothing writes back to the authorization. |
| Prior-auth rule engine | `src/engines/CloudHealthOffice.PriorAuthRuleEngine` | I+T (engine) / P (content) | Real, data-driven. Content is 10–11 TX Medicaid seed rules. History services are interface-only (S). |
| Rule admin | `benefit-plan-service/Controllers/PriorAuthRulesController.cs` | P | Seed endpoint only; no CRUD. |
| Drug/benefit exclusion | `AS/Services/BenefitExclusion/*` | I+T | Excludes drugs from CMS-0057-F medical PA scope. |
| PA retention | `FS/Services/Retention/PriorAuthorizationRetention*` | I+T | Off by default. |
| CRD (CDS Hooks) | `FS/Controllers/CrdController.cs`, `FS/Services/CrdService.cs` | P | See §4.2. |
| CRD classification store | `FS/Services/CrdClassificationStore.cs` | S (durability) | `IMemoryCache`: expires, per pod, lost on restart. |
| DTR | `FS/Controllers/DtrController.cs`, `FS/Services/DtrService.cs` | P | See §4.3. |
| Claims → auth check | `claims-service/.../BenefitCalculationStage.cs` (`EvaluatePriorAuthorizationAsync`); `HttpAuthorizationValidationClient.cs` | I+T | Every PA-required line checked, under claims-service's own service token (approval re-runs included). A failed lookup pends `PAUNAVAIL` before the benefit engine (no accumulator commit prepared), never pays or denies (D-04 fixed, PR #1299); bulk re-run for those pends. Scope: inpatient Medicaid floor (G-61); plan-flagged codes behind `ClaimsAdjudication:PriorAuth:UsePlanFlaggedCodes`, off pending G-64 scope rollout (G-25); D-36/D-40 pipeline identity fixes are implemented. |
| 837 REF*G1 capture | `claims-service/EDI/Inbound/X12837Parser.cs`; `X12837ClaimMapper.cs` | I+T | 2300 (837P and 837I) and 2400 (an 837P segment; read on an 837I too if sent); line overrides claim (D-05 fixed, PR #1299). |
| X12 278 inbound | `infrastructure/argo-workflows/x12-278-ingest.yaml`, `containers/x12-parser/parse_x12.py` | S | Generic tokenizer; Kafka `edi-278` has no consumer. |
| X12 278 outbound | `containers/x12-encoder` (277 only) | M | — |
| PAS ↔ 278 mapping (TS) | `src/fhir/prior-auth-api.ts` | S (orphaned) | JSON-object mapper; nothing imports it; clearinghouse functions are self-declared placeholders (:843-901). |
| FHIR Patient/Coverage/Encounter/Claim data | `FS/Services/ChoNative/RoutingFhirDataAdapter.cs`, `FS/Services/MockFhirDataAdapter.cs` | P | Updated 2026-10-20 (CAP-27): Patient and Coverage are I+T, tenant-scoped from member-service and coverage-service when bound to ChoNative; Encounter and Claim remain mock (S). |
| Patient Access claims/EOB | `FS/Controllers/ExplanationOfBenefitController.cs` → claims-service `FhirExplanationOfBenefitController`, `ExplanationOfBenefitProjector` | P | Updated 2026-10-20: real, tenant-scoped claims; patient-confined (another member's read is 404, search pinned to the token's member, results outside it 502). Not CARIN BB profiled (§4.5). `MockPatientAccessDataProvider` (5 hard-coded members) backs the P2P export and `$member-match` only when `FhirAdapters:Sources:PayerToPayer` is `Mock` (ADR 020). |
| Provider Access authorization | `FS/Services/ProviderAccess/*` | P | Shares unless the member opts out (D-11 fixed, ADR 021); static attribution. |
| Bulk `$export` | `FS/Services/BulkExportService.cs:24-70` | S | Marks jobs Complete instantly with count 0; output URLs have no route. |
| Payer-to-Payer | `FS/Services/PayerToPayer/*`, `consent-service` | P | Opt-in consent is I+T. Export excludes PA and uses mock data. Auth gap D-13. |
| `$member-match` | `FS/Services/PayerToPayer/PayerToPayerMemberMatch*` | P | Matching logic I+T (22 tests) over mock data, single tenant. |
| SMART authorization server | `SA/*` (OpenIddict 7.4) | P | See §4.6. |
| SMART resource-server enforcement | `FS/Middleware/SmartScopeEnforcementMiddleware.cs` | I+T | 19+ tests. |
| CapabilityStatement | `FS/Controllers/MetadataController.cs` | P | No `$member-match`, Group `$export`, `$questionnaire-package`, or PDex/CARIN profiles. |
| Portal PA submit | `src/portal/.../ServiceImplementations.cs:1005-1023` | S (broken) | Request has no requested services → 400 (D-24). |
| Denial notices / correspondence | `src/portal/.../Pages/Correspondence.razor:283-305` | S | Hard-coded template row; the backend `/correspondence/*` does not exist. |
| Da Vinci interop harness | `tests/DaVinciInterop.Tests` | I+T (CHO as **client**) | CHO is never the payer server under test. |
| CMS-0057-F evidence generator | `tools/Cms0057Evidence`, `tests/Cms0057Acceptance.Tests/scenarios.json` | P | Replace statuses come from §11 of this matrix (Q-1); auto-publish paused. Backing tests are still partly tautological (§6.1). |

### 2.2 End-to-end trace — one PAS request

Entry point: `POST /fhir/r4/Claim/$submit`. This is the only live entry point.
The X12 278 entry is a stub with no consumer. The portal entry returns 400. REST
`POST api/authorizations` works but never invokes the rules engine.

| # | Hop | Where | Result |
|---|---|---|---|
| 1 | Auth + tenant | `PasController.cs:56`; `FhirControllerBase.cs:21-22` | ✅ Tenant from token. SMART scope `authorizations:write`. |
| 2 | Bundle validation | `PasController.cs` (`ValidateAndExtractClaim`); `Services/Pas/PasAuthorizationMapper.cs` | ⚠️ Works. No PAS profile validation; `Claim.use` and `Bundle.type` not checked. Only the first Claim is used. The Patient, Coverage and requesting Practitioner/Organization the Claim references are now read (and required); a request missing them gets a 422 OperationOutcome before adjudication (PR #1300). |
| 3 | Compliance gate | `PasController.cs:86-93` → `Cms0057ComplianceChecker.ValidateClaim:513` | ⚠️ Presence checks only. |
| 4 | Provider verification | `PasController.cs:122-158` (helper `:613-663`) | ⚠️ Fails open when the service is down (`:648-662`). |
| 5 | Adjudication | `PasController.cs:160-183` → `PasAutoAdjudicator.TryDecideAsync:63` | ⚠️ Order: enrollment gate (only for Medicaid / Medicare / Marketplace) → deny list → allow list → gold card (wrong scope, D-19) → **< $500 auto-approve** → rules engine. Rule-set tenant from the token (D-12 fixed). Since PR #1300 (B1) it decides on the values mapped for the record (LOB, NPIs, service date, codes, member, DOB, diagnoses, place of service) and records only the inputs it read: an `Unknown` LOB is pended without adjudication, and the state is the request's `item.locationAddress.state` (no state → the state rules do not run and the request pends), never a TX / Medicaid default. fhir-service requires MongoDB in every environment, so the Mongo-backed engine always runs (the NoOp engine is a test double). |
| 6 | Response | `PasResponseBuilder` | Stored decisions, tracking references and service-line outcomes are projected (CAP-01/02); full PAS profile semantics remain separate. |
| 7 | **Persist** | `PasController.RecordAndRespondAsync` → `HttpPasAuthorizationRecorder` → `POST api/internal/authorizations/automated-decisions` (fhir-service service token only) | ✅ **Fixed (PR #1300).** The request is mapped from the Bundle (no placeholders) and stored with the adjudicator's decision, reason, approved period (the requested one) and a receipt + decision history, before the response is built. If the write fails, `$submit` answers 503/502 with an OperationOutcome and no `preAuthRef`. The public `POST api/authorizations` still clears body-supplied outcomes. Seam test: `PasSubmitPersistenceSeamTests` (both real hosts, real Mongo). |
| 8 | Pend notification | — | ❌ Missing. No Subscriptions, events or notices. |
| 9 | Reviewer decision | `POST api/authorizations/{id}/response` | CHO records and validates header/service-line decisions (CAP-01/02); complete reviewer UI/notification integration is not established by this reconciliation. |

**Net:** since PR #1300, steps 7 and 10 are connected: the system of record holds
what the provider was told, and `$inquire` reads it back. Claims now perform the
per-line PA check in step 11: definitive failures deny, while lookup outages pend
before pricing. Pend notification and the complete reviewer UI workflow remain separate from the
verified decision API; absence of a record is no longer a valid explanation.

---

## 3. Historical defect log with resolution status

Found during the Phase 1 audit. The Defect and Evidence columns retain the
original findings; they must not be read as current defects when the Status
column or the dated reconciliation marks them resolved or partial.

| ID | Historical severity | Original finding (not current when resolved) | Original evidence | Original fix sketch | Resolution status / current evidence |
|---|---|---|---|---|---|
| D-06 | High | Line-level `ServiceStatus` is never written by any API, so `/validate` with a procedure code always fails for API-decided auths. | `AS/Models/Authorization.cs:484`; `AuthorizationsController.cs:275-276` | Line-level decision model and API (also needed for PAS item adjudication and A2 partial approvals). | **Fixed for CHO Replace mode.** Reviewer/automated decisions populate stable lines and quantities; partial decisions require a complete line set. Claims validation uses those outcomes and refuses ambiguous repeated codes. PAS projects stored outcomes; G-13/G-17 conformance work remains. |
| D-07 | Critical | The SLA watchdog throws on every run. A hosted service has no `HttpContext` tenant, so no authorization is ever escalated. Tests mock the repository. | `AS/Services/SlaWatchdogService.cs:58`; `AuthorizationRepositoryMongo.cs:26-34`; confirmed by probe | Iterate tenants explicitly, as the retention worker does. Test against a real repository. | Fixed in `73df1861`, PR #1297. Each tick lists the tenants (`ListTenantIdsAsync`) and evaluates them one at a time in a fresh DI scope, with the tenant passed explicitly (no captured repository). Escalation is a new tenant-explicit conditional write, `RaiseSlaEscalationAsync`: only the escalation fields, only if the record is that tenant's, still open and below the level (Mongo: one conditional update; Cosmos: tenant-partition read and ETag-pinned replace). A record listed under another tenant is skipped and logged; one tenant's failure does not stop the others. Thresholds unchanged (due dates and alerts are G-18 / G-19). Tests run the watchdog the real host registers against MongoDB (EphemeralMongo) and the Cosmos emulator for two tenants with no request context (`SlaWatchdogRealStoreTests`); on the original audit baseline they threw `TenantId not found in request context`. Review of #1297: the Cosmos escalation patches only `/slaEscalation` and `/slaEscalatedAt` (ETag-pinned) instead of replacing the document, so a newer pod's properties survive a rolling deploy; guard tests on both stores (other tenant, closed, closed after listing, equal or lower level, parallel raises: exactly one wins); a record failure no longer stops its tenant; the tenant list is cached for an hour (D-37). **Limitation:** with `MongoDb:UseTenantScoping=true` the watchdog cannot reach per-tenant databases from a background thread; it logs a warning and does nothing (D-38). |
| D-08 | Critical | PAS `Claim.priority` isn't mapped to `LevelOfService`, so every PAS request runs on the 7-day clock. | `PasController.cs:348-373`; `SlaWatchdogService.cs:67` | Map `priority` (stat/urgent → expedited). Set the receipt timestamp on arrival, not after adjudication. | **Fixed** (`d0681cee`, PR #1300). `Claim.priority` stat/urgent/expedited (and X12 `U`, `03`) → `LevelOfService` `U`; normal/routine/deferred/`E` → `E`; absent → null (standard). Review follow-up, per the owner's decision Q-14 (2026-10-11): urgency is never under-classified. The most urgent signal across the PAS level-of-service extension (Claim and items) and `Claim.priority` wins. `stat`, `urgent`, `expedited`, `asap`, `U`, `03`, `emergency`, `emergent`, `immediate`, `UR` and `EM` (matched case-insensitively) are `U` under any code system or as the concept's text. HL7 v3-ActPriority `S`, `A`, `CS` and `CR` are `U` only in ActPriority itself (by URL or by OID `urn:oid:2.16.840.1.113883.5.7`): single letters and callback codes are ambiguous elsewhere, so under another system they are unrecognised (standard, logged). Outside its standard pair (processpriority `stat`; X12 1338 `U`/`03`; request-priority `urgent`/`asap`/`stat`; ActPriority `S`/`A`/`UR`/`EM`/`CS`/`CR`, exact case) an urgent value is still `U` and `$submit` logs a warning (values sanitized). `E` for an explicit standard value in a known system (processpriority `normal`/`deferred`; X12 1338 `E`; request-priority `routine`; ActPriority `R`/`EL`); any other value is unrecognised, gets the standard clock (`E`) and is logged. No priority at all stays null (standard). Note: X12 1338 `E` means "Elective", so a stored `E` can mean either "elective" or "not recognised"; only the warning log tells them apart, and that stays so until the owner decides otherwise. Receipt is stamped as the request arrives, before validation and adjudication, and stored as `SubmittedDate` and the first history entry. **2026-10-19 correction:** receipt-v1 deadlines are persisted and used by the tenant-explicit watchdog (CAP-07); program-specific extensions and alerts are separate limitations. |
| D-09 | High | When RFI documents arrive, the decision clock restarts with a full new window. CMS-0057-F has no RFI tolling or restart. | `AS/Consumers/RfaiDocsReceivedConsumer.cs:168-195` | Keep the clock from receipt. Model extensions explicitly, where the program permits them. | **Fixed (receipt-v1).** RFAI timestamps are informational; SLA enforcement and turnaround use original receipt. Persisted deadlines and revision-fenced legacy initialization are covered by real-Mongo and acceptance tests. Extensions remain G-18. |
| D-10 | High | A denial (A3/Denied) is accepted with no reason. The rule engine falls back to the rule name as the reason. | `AuthorizationsController.cs:445-469,535-539`; `PaRuleBase.cs:88`; test `UMCoordinator_CanDecide` | Require a coded and human-readable reason for every denial and partial denial. | **Fixed at the CHO decision write boundary.** A3 and reduced A2 lines require both code and text before mutation/persistence; reviewer and automated paths share the policy. X12 reason-code membership and provider notices remain G-17/G-21. |
| D-11 | High | Provider Access requires an active opt-in consent. CMS-0057-F requires sharing unless the patient **opts out**. | `FS/Services/ProviderAccess/ProviderAccessAuthorizationService.cs:129-140` | Invert to opt-out, keyed on attribution. | **Fixed** (ADR 021). Consent records carry a provision (Permit/Deny); a Deny record is an opt-out. `ConsentAuthorizationPolicy.EvaluateOptOut` allows an attributed provider unless an opt-out is in force, honours a withdrawn opt-in as a refusal, and the registry source now throws instead of answering empty, so an unreadable registry refuses (`RegistryUnavailable`). Tests: `ProviderAccessConsentTests` (CONSENT-01, PROV-03, Replace), `ConsentAuthorizationPolicyTests`, `ConsentsControllerTests`, `SmartScopeEnforcementTests` (HTTP: attributed and never opted out → 200, opted out → 403). |
| D-12 | High (tenant) | The PA rule set's tenant is taken from `Bundle.meta.tag "cho:tenant"` in the request body, not the authenticated tenant. | `FS/Services/PasAutoAdjudicator.cs:161,338-340` | Use the authenticated tenant only. | Fixed in `45322cb3`, PR #1294. Tenant passed from the controller; tag ignored. HTTP test `PasRuleSetTenantTests`. (The unbuilt reference copy `src/engines/cho-enrollment-wiring/fhir-service/PasAutoAdjudicator.cs` still contains the old code; no project compiles it.) |
| D-13 | Medium (security; High until ADR 022, Critical before PR #1294) | ~~`PayerToPayer/$member-data-export` and `Patient/$member-match` accept patient-context tokens, take the target member from the body, and trust a self-declared `receivingPayerId`; one payer client could export any opted-in member.~~ Fixed in PR #1294, PR #1296 and ADR 022 (below). Remaining: `TrustedIssuerOptions` has no per-issuer scope allowlist (G-39). | `FS/Controllers/FhirControllerBase.cs` (`TryBindReceivingPayer`); `FS/Services/Identity/CallerIdentityResolver.cs:62-81`; `SmartScopeEnforcementMiddleware.cs:307-308,350` | Restrict to backend (system) clients. Bind the receiving payer to the authenticated client. Add an HTTP-level negative test. | **Partially fixed** (PR #1294: `6e834bde`, `01008fc1`). Both operations are system-context only and need the explicit `system/PayerToPayer.read` scope; no wildcard (`system/*.read`, `system/*.*`) and no `system/Patient.read` satisfies it. The controllers refuse any patient-bound or non-system caller (scopes from `scope` and `scp`) before reading the body, with one fixed 403. HTTP and middleware-only tests (`PayerToPayerAuthorizationTests`, `PayerToPayerScopeMiddlewareTests`). Audit `InitiatedBy` is the authenticated caller. **Receiving payer bound to the client** (PR #1296: `87a5cf98`, `5b71cd0a`). A backend client registration carries a `payerId` (smart-auth-service, set by the tenant's administrator; required for, and before issuing, `system/PayerToPayer.read`), minted into its access tokens as `payer_id`. fhir-service believes `payer_id` only from the one trusted issuer marked `IssuesChoClientRegistrations` (startup requires exactly one). A client with no registered payer, including every client of an unmarked issuer, or a body naming a different payer, gets one fixed 403 before any member lookup; audit and logs record the registered payer. Tests that failed on the original audit baseline: `PayerToPayerReceivingPayerBindingTests`, `SmartClientPayerBindingTests`; end to end in `SmartAuthToFhirEndToEndTests`. Consequence: clients of external IdPs cannot use Payer-to-Payer until registered with CHO. **Consent bound to the receiving payer** (ADR 022): `$member-match` requires the member's HRex Consent naming the requesting (registered) payer as recipient and naming the matched member (identifier or family name and birth date), checked before any member lookup (422 otherwise), and records it per (tenant, member, payer); `$member-data-export` serves a member only to a payer whose consent is in force (supplied with the export or recorded by its match). Another payer's consent opens nothing; CHO's registry no longer decides inbound requests. Tests: `PdexConsentValidatorTests`, `MongoPayerToPayerConsentAttestationStoreTests` (real Mongo), `PayerToPayerAuthorizationTests` (HTTP: another payer's consent → 403; match without consent → 422), `P2P03_Replace_*`, `P2P04_Replace_*Consent*`. **Remaining:** `TrustedIssuerOptions` has no per-issuer scope allowlist, so any trusted issuer can mint `system/PayerToPayer.read` (its clients are now refused for want of a registered payer, not for want of the scope). Severity lowered to **Medium** for G-39. |
| D-14 | High (tenant) | DTR Questionnaire search iterates every tenant's questionnaires. Where ids collide, tenant A can see tenant B's copy. | `FS/Services/DtrService.cs:98-99,304-309` | Filter by tenant, plus the read-only default library. | Fixed (PR #1294: `08f22378`, `7bbab01b`). Search selects by the store key's tenant: own + un-overridden defaults. The tenant id `default` (the library's store key) is reserved: questionnaire writes under it are refused (`DtrQuestionnaireTenantIsolationTests`, `DtrQuestionnaireWriteAccessTests`, HTTP). Search now uses the durable store across service instances (CAP-04); exact tenant keys and default shadowing remain covered by CAP-09. |
| D-15 | Medium | SMART tokens (provider apps) can write payer Questionnaires. | `FS/Controllers/DtrController.cs:65,93`; `SmartScopeEnforcementMiddleware.cs:343-358` | Questionnaire writes are CHO-admin only. | Fixed in `fd331235`, PR #1294. Create/update are CHO `settings:manage` only, and the middleware refuses a SMART Questionnaire write in every context, whatever the scope. `Questionnaire.write` is not advertised or grantable to new registrations; existing clients/refresh tokens may still carry `Questionnaire.write` and are refused at the resource server. Stored client permissions were not changed (`DtrQuestionnaireWriteAccessTests`, HTTP). There is no Questionnaire delete endpoint. |
| D-16 | Medium (tenant, latent) | PA rule-repository Upsert/BulkUpsert/Delete filter on RuleId + StateCode with no tenant. | `PaRuleRepository.cs:140-163,242-279` | Include the tenant in write filters. | Fixed in `ee137a52`, PR #1294. Mongo writes target the composite id + tenant; Mongo and Cosmos Delete take the tenant (null = platform). The engine seeder counts platform rules only. EphemeralMongo and Cosmos-emulator tests. |
| D-17 | Medium | Cosmos `PaRuleRepository.ListAsync` builds a parameterised query, then runs a parameterless one. | `PaRuleRepository.cs:166-179` | Use the built query. Run a Cosmos emulator test. | Fixed (PR #1294: `9b3d8b52`; `1a8e1191`). **Found and fixed in review:** `GetRulesAsync` returned 0 rules on the production document shape (shared `CosmosSystemTextJsonSerializer`: nulls omitted, enums as strings) because it matched `IS_NULL(...)` and an int LOB; it now uses `(NOT IS_DEFINED(x) OR IS_NULL(x))` for tenant and program, binds LOB in the stored shape, and sorts by band in memory. Emulator tests (`Category=Cosmos`) now use the production serializer. The SDK-default client that benefit-plan-service registers was verified on the emulator and is broken (Upsert → 400); see D-31. |
| D-18 | Medium | Cosmos `AuthorizationRepository` stores status/LOB enums as ints but queries them as strings, so search, summary, SLA and retention would return nothing on Cosmos. (Mongo is the default.) | `AS/Repositories/AuthorizationRepository.cs:169,216-219,362,442`; `AS/Program.cs:91-95` | String enum converter. Cosmos emulator test. | Fixed in `c2e5dcd2`, PR #1297. The duplicate `CosmosClient` and the service's own serializer were removed, so the host uses the shared `CosmosSystemTextJsonSerializer` (owner accepted the stored-shape change, as for D-31): `status`, `lineOfBusiness` and `authorizationType` are stored as camelCase strings instead of numbers; nothing else changes. Every enum in a query is bound as a parameter. Also fixed: the summary used `CASE` (not Cosmos SQL) and `dynamic` rows (throws under System.Text.Json), now `GROUP BY c.status` with typed rows, and counts Expired; `requestedServiceDateTo = null` and `IS_NULL(c.slaResumedAt)` missed absent properties. Emulator tests use the client the host resolves (`AuthorizationRepositoryRealStoreTests`); the same suite passes on MongoDB before and after. Runbook: `docs/deployment/COSMOS-DB-DEPLOYMENT.md`. |
| D-19 | High | Gold-card lookup calls `summary?providerNPI=`, but the summary ignores `providerNPI`. A tenant-wide approval rate gold-cards every provider. | `PasAutoAdjudicator.cs:211-227`; `AuthorizationsController.cs:615-629` | Provider-scoped history (implement `IProviderApprovalHistoryService`). | Open |
| D-20 | Medium | Rule-engine history services (`IProviderApprovalHistoryService`, `IMemberAuthHistoryService`) are never implemented or registered. Gold-card exemption never fires, and quantity-limit rules always pend. | `PriorAuthRuleEngine/Abstractions/IPriorAuthRuleEngine.cs:83-103` | Implement them over authorization-service, with tenant in the signature. | Open |
| D-21 | High | CRD **fails open**: an unknown code returns "No prior authorization needed". The engine is called with hard-coded TX / Medicaid / STAR, no diagnoses, and cost 0. SNOMED codes are translated to ICD-10-CM and evaluated as procedures. | `FS/Services/CrdService.cs:90-93,106-140,226-242` | Unknown → "info-needed / contact payer". Derive context from eligibility/coverage. Fix the code-type mapping. | **First part fixed** (PR #1294: `8a858e18`, `146829b6`). "Not required" only from an explicit classification (tenant, or deployment config `CrdConfig.AutoApprovedCodes`). An unknown code, a NoOp/failed/timed-out engine, any engine Approve, a draft order with no code (text only, `medicationReference`) and an exception during evaluation all give a "could not be determined — contact the payer / submit for review" warning card, never zero cards or a 500 (`CrdFailClosedTests`, HTTP + service + controller). **Remaining:** hard-coded TX / Medicaid / STAR context, no diagnoses, SNOMED → ICD-10-CM typed as procedures; engine Approve is not trusted until context is real. |
| D-22 | Medium | CRD and DTR stores aren't durable. Classification lives in an expiring `IMemoryCache`. DTR Mongo updates aren't written, and search reads memory only. | `CrdClassificationStore.cs`; `DtrService.cs:95-121,153-176,198-202` | Persist in the existing tenant store. *Persistence change — owner decision (§7).* | **DTR durability implemented (2026-10-19):** creation, conditional updates, reads, searches and existence checks use the existing Mongo collections, with cross-instance and concurrent-write tests. CRD classification is still expiring memory, so D-22 remains partial. See CAP-04/16 and operations/DTR-DURABILITY.md. |
| D-23 | Medium | No authorization state machine: any status can move to any status (Denied→Approved, Cancelled→Approved). Cancel skips history. `Expired` is never set. | `AuthorizationsController.cs:431-480,657-677` | Explicit transitions, with history on every change and an expiry job. | **Implemented for ordinary CHO workflows.** Explicit backend/controller transitions; forbidden final-state changes return 409 before mutation. Cancellation appends history through the backend. Registered expiry sweep atomically checks tenant/revision/status/end date and records history. Inclusive UTC calendar dates; D-38 tenant-database sweeps and separate overturn/reopening workflows remain. |
| D-24 | Medium | The portal PA submit sends no requested services (400) and reads a response field that doesn't exist. | `src/portal/.../ServiceImplementations.cs:1005-1023` | Fix the contract. | Open |
| D-25 | Medium | `.well-known/smart-configuration` advertises `private_key_jwt` (unsupported) and omits `refresh_token`. Code comments say "v2". | `FS/Controllers/SmartConfigurationController.cs:8,83,96`; `SA/Models/SmartScopes.cs:4` | Advertise only what works (or implement it, G-41). | Open |
| D-26 | Medium | PAS and SMART don't bind the caller: `$inquire` takes its corroborating member/NPI from the body, and `$submit` doesn't compare `Claim.provider` to the token's NPI. (CDex does.) | `PriorAuthorizationInquiryService.cs:144-156,224-251` | Bind to token claims. | Open. Note (PR #1300): `$inquire` now also accepts the record's `PatientReference` (the submitter's own `Patient/...` reference) as the corroborating key. That is a weaker key than the member id or NPI: it is chosen by the submitter, so it binds an inquiry to "whoever knows how the request named the patient", not to an identity in the token. |
| D-27 | Low | Summary approval rate counts pending requests in its denominator. The Cosmos average is truncated to whole days. The Mongo version doesn't compute turnaround. | `AuthorizationRepository.cs:261-321`; `AuthorizationRepositoryMongo.cs:145-149` | Replaced by the metrics work (G-20). | Open |
| D-28 | Medium | Bulk `$export` reports Complete with count 0 and output URLs that no route serves, which a client would read as success. | `FS/Services/BulkExportService.cs:24-70` | Return 501/OperationOutcome until real (G-34). | Open |
| D-29 | Medium | The Provider Directory requires authentication; CMS requires it to be public. | `FS/Controllers/ProviderDirectoryController.cs:27` | Public read endpoint. | Open |
| D-30 | High (honesty) | The CMS-0057-F evidence pipeline publishes hand-declared PASSABLE statuses backed by tautological tests (METRICS-01, PAS-06, PROV-0x, PAT-03, CONSENT-01). | `tests/Cms0057Acceptance.Tests/scenarios.json`; `tools/Cms0057Evidence/EvidenceBuilder.cs:117-125` | **Owner decision (§6.1).** The output is public. | Partly fixed (#1292): statuses come from §11; the tautological tests themselves remain; publish paused (#1291). |
| D-31 | Medium (latent) | benefit-plan-service registers a second, SDK-default `CosmosClient` that overrides the shared one from `AddChoDatabase` (`CosmosSystemTextJsonSerializer`). Under it a `PaRuleDocument` serialises as `Id` (not `id`) and PascalCase, so `PaRuleRepositoryCosmos.UpsertAsync` returns **400** and the camelCase queries match nothing. Every benefit-plan Cosmos model is serialised this way, not only PA rules. Verified on the Cosmos emulator during review of PR #1294; Mongo is the default backend. | `src/services/benefit-plan-service/Program.cs:44,95-99` | Remove the second registration so the shared serializer applies, with Cosmos emulator tests for every benefit-plan repository first. *Changes the stored shape of every benefit-plan Cosmos document — owner decision.* | Fixed in `044ce68b`, PR #1295. The duplicate client was removed, so the shared serializer applies (owner approved the stored-shape change). Emulator tests run on the host's own DI (`WebApplicationFactory<Program>`, Cosmos mode) and cover the PA seeder, `seed-platform`, and a write plus read-back for every Cosmos repository the host resolves. All 12 failed on the original audit baseline (400, no `id`). Defects surfaced once writes went through, fixed in the same PR: `versionState`/search enum parameters (`BenefitPlanRepository`); typed benefits lost under the Cosmos serializer; `AccumulatorRepositoryCosmos.DeleteByPlanYearAsync` used `dynamic`; `FeeScheduleRepositoryCosmos` used `termDate = null`. The PA-rule queries were fixed by #1294 (D-17). Runbook for older Cosmos data: `docs/deployment/COSMOS-DB-DEPLOYMENT.md`. |
| D-32 | Medium | The SMART JwtBearer scheme keeps the default inbound claim mapping, so `scp` arrives as `http://schemas.microsoft.com/identity/claims/scope`. `CallerIdentityResolver.ParseScopes`'s `scp` branch is therefore dead, and `SmartScopeEnforcementMiddleware` reads only `scope`. Effect: grants held only in `scp` fail closed (a functional gap for Entra-style issuers), and narrowing scopes carried in `scp` are ignored outside the P2P controllers (low risk). Found during review of PR #1294; `IsBackendSystemCaller` reads both spellings as a stopgap. | `FS/Services/Identity/SmartTrustServiceCollectionExtensions.cs:51`; `CallerIdentityResolver.cs` (`ParseScopes`); `SmartScopeEnforcementMiddleware.cs` (`ParseScopes`) | Turn off `MapInboundClaims` on the SMART scheme and use one scope parser everywhere; audit dependencies on mapped claim names first. | Fixed in `c608ea84`, PR #1298 (pulled forward from wave 6 by the owner). The SMART scheme sets `MapInboundClaims = false`. One parser, `SmartScopeClaims`, serves the middleware, `CallerIdentityResolver` and `IsBackendSystemCaller`; the stopgap is removed. `scp`-only tokens (string or array) are read like `scope`. A token whose `scope` and `scp` sets differ is refused at authentication (401), with no union or intersection. **Found in review:** a `system/*.read` grant in `scope` plus `patient/*.read` only in `scp` was a system caller (`$export` 202, Provider Access read 200), not just a low-risk narrowing gap. Readers of `sub` no longer fall back to `NameIdentifier`; `PasController` is left to its own PR. HTTP tests `SmartScpClaimTests` failed on the original audit baseline (17 of 31; 12 of 23 in the first commit). Review follow-ups: an empty-array claim (`"scope": []`) counts as present and conflicts with a non-empty one, a conflicting token is refused at the CRD hook too, and the scope-denied log is sanitised. smart-auth-service tokens carry `scope` only, verified end to end for every grant type. |
| D-33 | Low (latent) | `GetDefaultForPlanAsync` filters on `defaultForPlanId`, but `FeeSchedule` has no such property and nothing writes it, so on **both** backends the query never matches and a plan's default fee schedule is never found. Not caused by the serializer. Found while fixing D-31 (PR #1295). | `src/engines/CloudHealthOffice.FeeScheduleEngine/Persistence/FeeScheduleRepositoryCosmos.cs:56-85` (`c.defaultForPlanId`); `FeeScheduleRepositoryMongo.cs:61` (`Filter.Eq("defaultForPlanId", planId)`); `Models/FeeScheduleModels.cs` (`FeeSchedule`) | Decide how a plan names its default schedule (a `DefaultForPlanId` property on `FeeSchedule`, or a plan-side reference) and use it in both repositories; add Mongo and Cosmos emulator tests. | Open |
| D-34 | Low | `RedisPaRuleRepository.FlushStateViaScanAsync` catches only `RedisException`. `RedisTimeoutException` derives from `TimeoutException`, so when the SCAN times out a rule delete succeeds in Cosmos/Mongo and then throws, leaving the cached rule sets unflushed. Reproduced in review of PR #1295; no production caller of `DeleteAsync` today. | `src/engines/CloudHealthOffice.PriorAuthRuleEngine/Persistence/PaRuleRepository.cs:559` | `catch (Exception ex) when (ex is RedisException or RedisTimeoutException)`, with a test on an unreachable Redis; separate PR. | Open |
| D-35 | Low | `FeeScheduleRepositoryCosmos` binds `@serviceDate` as `serviceDate.ToString("O")` (7 fractional digits), while the serializer stores `DateTime`s with fractional zeros trimmed. The string comparison with `effectiveDate` / `termDate` is then wrong on the boundary day for UTC dates. Pre-existing; found in review of PR #1295. | `src/engines/CloudHealthOffice.FeeScheduleEngine/Persistence/FeeScheduleRepositoryCosmos.cs:72` (`GetDefaultForPlanAsync`) and `:215` (`QueryContractAsync`) | Bind the `DateTime` itself so the serializer writes it as it stores the property, as `BenefitPlanRepository` does for `@asOf`; add boundary-day emulator tests. | Open **Same class, found in review of PR #1297:** the authorization summary (`AS/Repositories/AuthorizationRepository.cs`, `GetAuthorizationsSummaryAsync`) compares `c.submittedDate` with `@from` / `@to` as strings. Both sides come from the same serializer, but trimmed fractional seconds sort after the bare second (`"…:00Z"` > `"…:00.5Z"`), so records within the boundary second can fall on the wrong side. Same fix: compare in one fixed-width format, or with `DateTimeToTicks`. |
| D-36 | High | Examiner approval re-runs call coverage-service with the examiner's own token (the outbound handler forwards the caller's token). ClaimsExaminer and ClaimsSupervisor do not hold `coverage:read`, so the COB stage's coverage lookups on an approval re-run are refused (403) → "Coverage-service unavailable" transient pend → the approval is refused (409). Same pattern as #1299 B1 (fixed there for the PA lookup only). | `claims-service/Services/Resolution/HttpCoverageClient.cs`, `HttpCoverageResolver.cs`; `ChoOutboundTokenHandler.cs`; `ClaimsController.cs` (approval re-run) | Send these pipeline calls with the service identity (`UseChoServiceIdentity`), as the PA lookup now does; do not widen roles. Test through the real handler chain. | **Fixed in code:** both coverage clients request the tenant-guarded claims-service identity. Real outbound-handler tests cover examiner/supervisor approval preparation and COB, background calls, missing/foreign tenants and issuer outages; receiving coverage API tests verify token acceptance and tenancy. No user role grants are widened. Deployment identity configuration remains operational validation; G-64 stays disabled by default. |
| D-37 | Low (performance) | Listing tenants is unindexed. Mongo `ListTenantIdsAsync` is a `Distinct("TenantId")` over the whole collection, and `GetOpenAuthorizationsAsync` filters on `TenantId` + `Status` with no `{TenantId: 1, Status: 1}` index (none exists for the collection); Cosmos runs `SELECT DISTINCT VALUE c.tenantId` across every partition. The SLA watchdog and the retention worker both list tenants. Found in review of PR #1297. | `AS/Repositories/AuthorizationRepositoryMongo.cs` (`ListTenantIdsAsync`, `GetOpenAuthorizationsAsync`); `AS/Repositories/AuthorizationRepository.cs` (`ListTenantIdsAsync`) | Add a Mongo index `{TenantId: 1, Status: 1}` (serves both the distinct and the open-status query). For Cosmos, list tenants from tenant-service, or keep the DISTINCT with a cache. | **Fixed for the supported Mongo backend:** TenantId + Status index created with the authorization-number index. Cosmos-only follow-up is obsolete after ADR 017. |
| D-38 | Medium (latent) | With `MongoDb:UseTenantScoping=true` (a database per tenant, `{base}_{tenant}`), the background workers cannot reach tenant data: with no request, `IMongoDatabase` resolves to the base database, so `ListTenantIdsAsync` finds no tenants. The SLA watchdog now logs a warning and does nothing in that mode (PR #1297); the **retention worker** still sweeps the base database and silently purges nothing. Not the default configuration. Found in review of PR #1297. | `src/services/shared/CloudHealthOffice.Infrastructure/Data/MongoDbConnectionFactory.cs` (`GetTenantScopedDatabaseName`); `AS/Services/Retention/PriorAuthorizationRetentionWorker.cs`; `AS/Services/SlaWatchdogService.cs` | Give background workers a tenant source (tenant-service, or listing `{base}_*` databases) and a repository built over `MongoDbConnectionFactory.GetDatabase(tenantId)`; until then, the retention worker warns and skips as the watchdog does. | Open |

---

## 4. Requirement tracking

CAP-linked entries summarize reconciled behavior. Other entries retain the dated audit assessment and must be reverified before reuse as current claims. The **Gap** column links the historical planning register; it is not proof that every referenced behavior remains absent.

### 4.1 Da Vinci PAS (STU 2.x)

| Requirement | Class | Evidence | Gap |
|---|---|---|---|
| `Claim/$submit` operation | P | `PasController.cs` `ClaimSubmit`. Persists through the real seam (`PasSubmitPersistenceSeamTests`); `Claim.use` unchecked, no profile validation, response not STU2-complete. | G-06, G-13, G-14 |
| Request Bundle validation against PAS profiles | M | Presence checks only (`Cms0057ComplianceChecker.cs:513-600`). No FHIR validator. | G-14 |
| `Claim.use = preauthorization` enforced on submit | M | Checked only on `$inquire` (`:314`). | G-13 |
| Supporting resources (Patient, Coverage, Practitioner, Organization) read from the Bundle | I+T | `PasAuthorizationMapper` (PR #1300); `PasAuthorizationMapperTests`, seam test. PractitionerRole is followed if present but is not an allowed entry type yet. | — |
| Durable record of each request and decision | I+T | Internal automated-decision path (D-01, D-02 fixed, PR #1300); seam test over a real Mongo repository. | — |
| `ClaimResponse` PAS profile (bundle contents, identifiers) | P | Response Bundle holds only the ClaimResponse (`PasResponseBuilder.cs:317-333`). | G-13 |
| `reviewAction` / `reviewActionCode` at `item.adjudication` | P | Stored service-line review actions are projected by AddRequestedServices; full STU2 reason/profile semantics not established (CAP-02). | G-13 |
| Item-level decisions (A1 / A2 / A3 / A4 per line) | I+T | CHO decision policy writes stable line outcomes; PAS submit/inquiry project them on item adjudications. Full PAS STU2 validation remains. | G-13, G-17 |
| Denial reason in `reviewAction.reasonCode` (X12 HCR03) | P | Placed in `ClaimResponse.error` (`:54-62,191-201`). | G-17 |
| `preAuthRef` / `preAuthPeriod` | I+T | Issued only after the record is stored; period = requested period (PR #1300). | — |
| Pended response (A4 / queued) | I+T | `PAS07_PendedResponse_SignalsOutstandingReviewViaX12A4`. | — |
| `Claim/$inquire` | I+T | `PasInquiryTests`; finds PAS-issued numbers end to end (seam test, PR #1300). Caller not bound to the token (D-26). | G-27 |
| Pended → final via R5-backport topic-based Subscription | M | No Subscription / SubscriptionTopic anywhere. | G-15 |
| Update / cancel of a submitted request | M | Only `$submit` / `$inquire` exist. | G-16 |
| Additional information (CDex, attachments) | I+T | PAS-07 (42 tests). Never triggered by PAS pends. | G-16 |
| QuestionnaireResponse in the PAS Bundle used in adjudication | M | Allowed as an entry, never read (`PasController.cs:437`). | G-16 |
| X12 278 request → PAS / authorization | S | Argo/Python tokenizer; no consumer. | G-50 |
| PAS / authorization → X12 278 response | M | 277 encoder only. | G-50 |
| 278 inquiry (X215 / X217) | M | JSON schemas referenced only by docs. | G-51 |
| Intermediary pattern (FHIR ↔ X12 payer) | M | — | G-52 (Phase 4) |
| Capability statement declares PAS operations | P | `$submit` / `$inquire` declared; no supportedProfile. | G-36 |
| CHO tested as a PAS **server** against RI / Inferno | M | Interop harness tests CHO as client only; BR-PAS-INQUIRE-001 `implemented=false`. | G-60 |

### 4.2 Da Vinci CRD (STU 2.x) — CDS Hooks

| Requirement | Class | Evidence | Gap |
|---|---|---|---|
| `/cds-services` discovery | I+T | `CrdController.cs:23-79`, static, anonymous. | — |
| `order-sign` | P | Cards only (`CrdService.cs:49-104`). | G-44 |
| `order-select` (with `selections`) | P | `selections` not modelled (`CrdModels.cs:25-35`). | G-44 |
| `order-dispatch` | M | — | G-47 |
| `appointment-book` | M | Advertised in `api/openapi/prior-auth-api.yaml` but not served. | G-47 |
| `encounter-start` / `encounter-discharge` | M | — | G-47 |
| Prefetch templates used | P | Declared, never read. | G-46 |
| FHIR fallback via `fhirAuthorization` | M | Field not modelled; no EHR FHIR client. | G-46 |
| `coverage-information` extension via systemActions (covered, pa-needed, doc-needed, info-needed, billingCode, reason, coverage-assertion-id, questionnaire) | M | `CrdCardResponse` has cards only (`CrdModels.cs:109-113`). A parser exists only in the test harness. | G-44 |
| Card: coverage / instructions | P | Free text; topic codes in a `temp` code system. | G-44 |
| Card: launch DTR SMART app | S | Hard-coded URL, no `appContext`, no app (`CrdService.cs:336-369`). | G-56 |
| Card: propose alternate request | M | — | G-45 |
| Card: request form completion | M | — | G-45 |
| CDS client authentication (EHR-signed JWT, JWKS / `jku`, `jti`) | M | Requires a CHO-issued SMART token instead (`CrdController.cs:84-132`). That substitute is I+T. | G-43 |
| Configuration options | M | `CrdConfig.Enabled` is never read. | G-48 |
| Response time budget (~5 s) | P (untested) | No timeouts (terminology HttpClient uses the 100 s default). Sequential per-code evaluation. | G-48 |
| Coverage rules from real member context (state, LOB, plan, diagnoses) | S | Hard-coded TX / Medicaid / STAR (D-21). | G-05, G-46 |
| Uses benefit-plan PA flags and reference-data `requires_prior_auth` | M | Not consulted. | G-46 |
| Safe default for unknown codes | I+T | Unknown → "could not be determined" warning card; "not required" only on an explicit classification (D-21 first part, PR #1294). | — |
| CHO tested as a CRD **server** against RI / Inferno | M | Discovery shape only (`ChoCrdSurface.cs`). | G-60 |

### 4.3 Da Vinci DTR (STU 2.x)

| Requirement | Class | Evidence | Gap |
|---|---|---|---|
| Questionnaire storage and serving | P | Mongo-backed creation, conditional updates, reads, existence checks and tenant/default search across instances (CAP-04). Full validation/canonical semantics remain separate. | G-53 |
| SDC / DTR Questionnaire profiles (canonical url, version, cqf-library, launchContext, variables) | S | 5 hand-written seeds that only claim the profile (`DtrService.cs:332-751`). | G-55 |
| `$questionnaire-package` (inputs: coverage + order; output: Parameters with PackageBundle containing Questionnaire, Library, ValueSet) | P | Takes an id; returns a collection Bundle with the Questionnaire only; undeclared. | G-54 |
| CQL Library resources | M | — | G-55 |
| Prepopulation (`initialExpression`, `cqf-expression`) | M | — | G-55 |
| Adaptive forms (`$next-question`) | M | — | G-57 |
| CQL engine (for a CHO-hosted DTR app, if built) | M | — | G-56 (decision) |
| QuestionnaireResponse storage | P | Submission, id reads and tenant/patient-filtered searches use durable store inputs across instances (CAP-04). | G-53 |
| QuestionnaireResponse linked to the PAS request | M | — | G-16 |
| DTR SMART app launch (`fhirContext`, `appContext`) | M | `LaunchContext` has patient/encounter only. | G-40, G-56 |
| Native EHR DTR support | P | Raw forms only. | G-54, G-55 |
| CHO tested as a DTR payer server against RI / Inferno | M | — | G-60 |

### 4.4 CMS-0057-F operational requirements

| Requirement | Class | Evidence | Gap |
|---|---|---|---|
| Urgency (expedited vs standard) captured | I+T | REST `LevelOfService`; PAS takes the most urgent signal across the level-of-service extension and `Claim.priority`, never under-classifying (D-08 fixed, PR #1300; owner decision Q-14). | — |
| Clock starts at receipt | I+T | PAS stamps receipt on arrival, before adjudication, as `SubmittedDate` (D-08 fixed, PR #1300). REST: insert time. | — |
| Due date computed **and persisted** (72 h expedited / 7 calendar days standard) | I+T | CHO receipt-v1 UTC target stored on intake; legacy records initialized with tenant/revision fencing. Program-specific extensions and tighter rules remain. | G-18 |
| Timers / escalation | P | Tenant-explicit watchdog uses persisted receipt deadlines; tenant-database mode and operational alerts remain distinct (CAP-07). | G-18, G-19 |
| Alerts to the UM team | M | Log line only. | G-19 |
| RFI does not restart the clock | I+T | Receipt-based deadline and turnaround remain unchanged when documentation arrives (D-09 fixed). | — |
| Extension tracking (where permitted) | M | No field. | G-18 |
| Late-decision reporting | P | Reporting includes timeliness/late counts in the standard and expedited groups (CAP-06); full public publication workflow is separate. | G-20 |
| Specific reason for every denial | I+T | Coded and human-readable reasons required by CHO policy before storing denied/reduced lines. Code-set validation remains. | G-17 |
| Partial denials carry line-level reasons | I+T | Complete line-decision API, durable coded reasons and history snapshots. | G-17 |
| Denial reason in PAS ClaimResponse | P | In `error`, with ad-hoc codes in the HL7 code system. | G-17 |
| Denial reason in X12 278 HCR | M | — | G-50 |
| Provider and member denial notices | S | Hard-coded portal template; no backend. | G-21 |
| **Public metrics:** list of items requiring PA (excluding drugs) | M | No public artifact. | G-20 |
| **Public metrics:** % standard approved / denied / approved after appeal | P | Standard counts/rates exist; approved-after-appeal remains explicitly untracked (CAP-06). | G-20 |
| **Public metrics:** % timeframe extended then approved | M | No extension field. | G-18, G-20 |
| **Public metrics:** % expedited approved / denied | I+T (calculator) | Cms0057Reporting computes the expedited group counts/rates (CAP-06). | G-20 (complete publication) |
| **Public metrics:** average **and median** time to decision, standard and expedited | I+T (calculator) | Mean and median computed separately per request type (CAP-06). | G-20 (complete publication) |
| Annual extract for website publication | M | Template doc only. | G-20 |
| Drugs excluded from CMS-0057-F PA scope | I+T | `DrugExclusionEvaluator`. | — |

### 4.5 PA data through the CMS-0057-F APIs

| Requirement | Class | Evidence | Gap |
|---|---|---|---|
| Real, tenant-scoped data source behind FHIR reads (Patient, Coverage, Claim, EOB) | P | Updated 2026-10-20 (ADR 018, PR #1420): Patient and Coverage are served from member-service and coverage-service, tenant-scoped, when bound to `ChoNative` (the Kubernetes manifest binds them; a mock binding is labelled Demo). ExplanationOfBenefit is proxied to claims-service's EOB projection (not CARIN BB profiled) with patient confinement. Payer-to-Payer members, coverage and claims come from the same CHO services when `FhirAdapters:Sources:PayerToPayer` is `ChoNative` (ADR 020; the manifest binds it). Encounter and Claim still come from `MockFhirDataAdapter`, which ignores tenant. | G-30 |
| **Patient Access:** member claims as ExplanationOfBenefit (CARIN BB) | P | Added 2026-10-20. The path is I+T: claims-service projects tenant-scoped claims to ExplanationOfBenefit and fhir-service serves them under SMART patient binding, with reads of another member answered 404, searches pinned to the bound member and any result outside it withheld (502). Not CARIN BB: no profile is declared, `insurer` is a display string, `insurance` appears only when the claim carries a coverage id, and the CARIN identifier type, care team and supporting information are absent; no CARIN BB validation has run. | G-30, G-60 |
| **Patient Access:** PA requests and decisions (status, dates, items, quantity, denial reason; excluding drugs), within 1 business day | P | Added 2026-10-22. authorization-service projects its own prior-authorization record to an `ExplanationOfBenefit` with `use = preauthorization` (`Services/Fhir/PriorAuthorizationEobProjector.cs`) and serves read + member search at `fhir/ExplanationOfBenefit/prior-authorization`; fhir-service routes `ExplanationOfBenefit?use=preauthorization` and a `pa-`-prefixed read there, under the same patient confinement as the claim EOB. Carries status (`outcome` + `disposition`), requested and approved periods, items with modifiers, requested and approved units, and the denial reason coded and in words at header and line level. Drugs are excluded by `PriorAuthorizationApiScope` (pharmacy service type 88, NDC/RxNorm lines, HCPCS J-codes), and an out-of-scope read answers 404 rather than disclosing that it exists. Within 1 business day is structural: the live record is read, so there is no propagation delay. Not I+T: the backing tests are service-level (`PriorAuthorizationEobProjectorTests`, `FhirPriorAuthorizationControllerTests`, `PriorAuthorizationEobRoutingTests`), not in the CMS-0057-F acceptance suite, and the projection is not a validated PDex prior-authorization resource — `type` is claim-type `other`, `insurer` is a display string, unit adjudication uses a CHO-private category system, and no profile validation has run. | G-31 |
| PA retention (life of the auth + 1 year) | I+T | Retention worker, off by default. | — |
| **Provider Access:** attribution | P | Static config panels. | G-33 |
| **Provider Access:** patient opt-out | I+T | Shares with attributed providers unless the member opts out; opt-out recorded as a Deny consent, revocable; registry failure refuses (ADR 021). Member notices and portal screens are the plan's. | — |
| **Provider Access:** PA data | P | Added 2026-10-22. Served by the same path as Patient Access: `ProviderAccessAuthorizationFilter` records the one member it authorized (attribution + no opt-out, ADR 021), and the EOB controller pins a `use=preauthorization` search to that member and answers a read of anyone else's 404. So an attributed provider receives the member's prior authorizations with the same drug exclusion and the same confinement. P, not I+T, for the reasons on the Patient Access row, and because attribution itself is still static configured panels (PROV-02). | G-33 |
| **Provider Access:** bulk `$export` (Group) | S | Stub (D-28). | G-34 |
| **Payer-to-Payer:** opt-in consent | I+T | Inbound: the requesting payer's HRex Consent, validated and recorded per payer (ADR 022). Outbound: the member's opt-in from CHO's registry, sent as an HRex Consent. `#regular` (segmentation) refused. | — |
| **Payer-to-Payer:** `$member-match` | P | Logic I+T; mock data; single tenant; undeclared. | G-35 |
| **Payer-to-Payer:** PA data (active, pending, recently changed) | P | Added 2026-10-22. With the `ChoNative` binding the export now carries the member's prior authorizations as `use = preauthorization` EOBs, read through `ChoNativeMemberDirectory.GetPriorAuthorizationsAsync` with the same two refusals as the claim read (a record for another member, or more pages than the bound) so the export is never silently partial. The window follows the requirement rather than the claims lookback: a still-open request is always in, and a decided one while its approved period ended within a year — a record whose dates cannot be read is excluded. A separate redaction allowlist (`RedactPriorAuthorization`) keeps the decision, the approved period, the units and the denial reason, which the CLAIM allowlist deliberately withholds; nothing monetary is listed and the projection carries no amounts. Still P: the mock export source does not carry PA data, so a Mock binding exports none, and the backing tests are service-level rather than in the acceptance suite. | G-35 |
| **Payer-to-Payer:** 5-year claims/encounter history via bulk | P | Mock payments; not bulk. | G-34, G-35 |
| **Payer-to-Payer:** caller authorization | P | Backend (system) clients with the explicit `system/PayerToPayer.read` scope only (PR #1294), and the receiving payer is the client's registered payer, not the body's (PR #1296); HTTP-tested. Consent is bound to the requesting payer and `$member-match` is consent-gated (ADR 022). No per-issuer scope allowlist (G-39). | G-02 |
| **Prior Authorization API:** list of covered items requiring PA and documentation requirements | P | CRD rules exist; no published list (D-21). | G-20, G-44 |
| CapabilityStatement completeness | P | Missing `$member-match`, Group `$export`, `$questionnaire-package`, supportedProfiles. | G-36 |
| Public Provider Directory | ❌ defect | Requires auth (D-29). | G-36 |

### 4.6 SMART on FHIR / OAuth2

CAP-05/11 in the canonical ledger define the current verified launch, approval-withdrawal and scope behavior. Unrechecked requirements below retain their historical assessment; a row about the full token-response or RFC contract does not negate those implemented capabilities.

| Requirement | Class | Evidence | Gap |
|---|---|---|---|
| `.well-known/smart-configuration` | I+T, content partly wrong | D-25. | G-41 |
| Authorization code + PKCE (S256) | I+T | `SA/Program.cs:89-91`; `SmartAuthDriver.cs`. | — |
| Standalone launch (member app) | I+T | `SmartTenancyTests.cs:37`. | — |
| EHR launch from an **external** EHR (`iss` + `launch` handshake) | P | CHO-registered launches only. | G-40 |
| Launch context in the token response (`patient`, `encounter`, `fhirContext`, `need_patient_banner`) | Unverified (complete response contract) | Tenant/client-bound launch and patient/encounter token context are implemented (CAP-05); the full response-field contract is not established by those tests. | G-40 |
| SMART v2 scopes (`.cruds`, `.rs`, granular `?category=`) | M | v1 only; a v2 scope gets 403. | G-41 |
| Scope enforcement per resource and operation, with patient binding | I+T | 19+ tests. Scopes from `scope` and/or `scp`; a token whose two claims differ is refused (D-32, `SmartScpClaimTests`). | — |
| Client registration | I+T (admin API) | Secrets only. | G-42 |
| Refresh tokens with re-validation | I+T | `SmartConsentRevocationTests`. | — |
| Backend services: `client_credentials` | I+T (shared secret) | — | — |
| Backend services: `private_key_jwt` / JWKS (required by SMART Backend Services) | M | Advertised but unsupported (D-25). | G-42 |
| `aud` validated by the authorization server | M | Always issues `fhir-api`. | G-41 |
| `aud` validated by the resource server | I-U | `SmartTokenValidation.cs:315-323`; no negative test. | G-41 |
| Token introspection | P | No client has permission. | G-41 |
| Token revocation (RFC 7009) | Unverified (complete RFC contract) | Approval withdrawal affecting refresh is implemented and tested (CAP-05); do not describe that as absence of all revocation capability. Full RFC semantics require separate evidence. | G-41 |
| CDS Hooks JWT verification | M | D-21 / G-43. | G-43 |
| Multi-issuer trust (customer IdP) | I+T | `FS/Services/Identity/*`. Claims are read under their JWT names (D-32), so Entra/Okta `scp`-only grants and issuer claim mappings that name `tid`, `oid`, `upn` or `email` now take effect (the contract's Entra example now maps a CHO tenant claim, not `tid`). | — |
| UDAP (optional, for payer-to-payer scale) | M | Comments only. | G-58 (deferred) |
| Payer-to-Payer outbound credentials | S | `UnconfiguredPayerToPayerCredentialProvider`, by design. | G-42 |

### 4.7 CMS-0062-P forward compatibility (design only; nothing is built)

| Capability | Today | Needed (design) |
|---|---|---|
| Medical vs pharmacy benefit | Inferred only from UM03 = "88" | Explicit `BenefitChannel` enum (Medical, Pharmacy) on the authorization and each line. Timelines and rules keyed by channel. |
| Drug identity | `ProcedureCode` plus free-text `ProductOrServiceSystem`. PAS now keeps `productOrService.coding.system` (PR #1300). | Typed coding (system URI + code). NDC normalised to 11 digits. Optional RxNorm cross-walk. |
| Quantity semantics | Decimal units, free-text unit type | UCUM / NCPDP quantity unit, days supply, refills, dispense-as-written, strength and dose form. |
| Participants | Requesting / servicing / facility NPI | Prescriber and dispensing pharmacy (NPI / NCPDP ID). |
| FHIR intake | MedicationRequest and Medication not allowed in the PAS Bundle | Allow them. CRD already reads `MedicationRequest.medicationCodeableConcept`. |
| Scope gate | `DrugExclusionEvaluator` excludes drugs from CMS-0057-F | Becomes channel routing rather than exclusion once a pharmacy rule applies. |
| NCPDP SCRIPT ePA | — | **Out of scope by instruction.** Interface boundary only (Phase 4). |

This is tracked as G-59: a design ADR plus additive, nullable model fields, done
only with owner approval because it changes persistence.

---

## 5. Tenant isolation

**Pattern (sound).**
- The tenant comes only from the validated token. `TenantMiddleware` returns 401
  when there is no tenant claim and 403 when `X-Tenant-ID` disagrees; there is no
  header fallback.
- SMART tokens are bound server-side to tenant and patient, and re-checked on
  refresh (30 tests in `SmartTenancyTests`).
- Repositories filter by tenant or partition key. Outbound calls carry the tenant
  plus a service token.

| # | Gap | Sev | Ref |
|---|---|---|---|
| T-1 | ~~Payer-to-Payer export and member-match: target member taken from the body, and patient tokens accepted.~~ Patient and user tokens refused; only the explicit `system/PayerToPayer.read` scope opens them (PR #1294). ~~Any system client holding the explicit P2P scope can name any receivingPayerId.~~ The receiving payer is now the client's registered payer (`payer_id` from the one marked issuer), and a different body payer or a client with no registered payer is refused (PR #1296). ~~A mapped payer client could still `$member-match` any member and export every opted-in member.~~ Consent is now bound to the requesting payer and `$member-match` is consent-gated (ADR 022). Remaining: no per-issuer scope allowlist (G-39, see D-13). Medium. Cross-tenant requests were and remain refused (404 / 422). | Medium | D-13 |
| T-2 | ~~PA rule-set tenant comes from the request body.~~ Fixed (PR #1294). | — | D-12 |
| T-3 | ~~DTR Questionnaire search crosses tenants.~~ Fixed (PR #1294). | — | D-14 |
| T-4 | `MockFhirDataAdapter` and `MockPatientAccessDataProvider` ignore tenant and are registered in every environment (narrowed 2026-10-20: Patient and Coverage can be bound to CHO services, ADR 018; ExplanationOfBenefit is proxied to claims-service; the Payer-to-Payer member source, `$member-match` candidates and export can be bound to CHO services, ADR 020; Encounter and Claim cannot). `$member-match` and P2P are served for one configured tenant only. | High | G-30 |
| T-5 | ~~PA rule-repository writes don't include the tenant (latent).~~ Fixed (D-16). ~~Any tenant with `settings:manage` can run the platform seed endpoint.~~ Now `platform:admin` (`4bdb1e95`, PR #1294). Remaining: `ListAsync(tenantId: null)` still returns every tenant's rules; its only callers are the two platform seeders, which now filter to platform rules. | Low | D-16 |
| T-6 | ~~Background SLA watchdog has no tenant.~~ Fixed (D-07, PR #1297): tenants are iterated explicitly, and a record listed under another tenant is skipped. | — | D-07 |
| T-7 | RFAI docs-received consumer trusts the tenant in the Kafka message (trusted internal bus). | Low | note |
| T-8 | Rule-engine history-service interfaces have no tenant parameter. Fix before implementing them. | Medium | D-20 |
| T-9 | `AUDIT/investor-readiness-2026-09.md:504-507` describes a header fallback that no longer exists. Stale doc that *under*-claims; owner review. | Low | §6 |

---

## 6. Historical public-claim assessment (October 10 baseline)

The quoted outputs, test descriptions and recommendations in this section are retained audit evidence, not assertions about current main. In particular, METRICS-01 and PAS-06 now have behavioral evidence (CAP-06/07); use the canonical ledger for current status.

### 6.1 The published CMS-0057-F evidence

- **What the pipeline does.** `.github/workflows/cms0057-acceptance-evidence.yml`
  runs the acceptance suite, then `tools/Cms0057Evidence` writes and commits
  `src/site/insights/cms-0057-f/cms0057-public-evidence.json`. That file
  currently shows **21/21 PASSABLE**.
- **Where statuses come from.** Each status is **hand-declared** in
  `tests/Cms0057Acceptance.Tests/scenarios.json`. *(Superseded by §11 (#1292):
  Replace statuses now come from this matrix.)* The generator only checks that
  a PASSABLE scenario has at least one passing test with the matching trait. It
  does not check what the test asserts.
- **Tautological backing tests:**
  - **METRICS-01:** date subtraction equals 3.0, and object properties are set.
    No CMS metric is computed.
  - **PAS-06:** asserts the label strings "72 hours" / "7 calendar days" on a
    ServiceRequest. The real PAS Claim path returns `Applicable:false`.
  - **PROV-01/02/03:** run against mock data. "Not attributed" is a nonexistent
    member ID. PROV-03 asserts `Revoked != Active`.
  - **PAT-03:** asserts that a supported-types list contains "ClaimResponse".
    No patient-facing PA endpoint exists.
  - **CONSENT-01:** reflection on constructor parameter types. The same manifest
    elsewhere says CONSENT-01 "stays PARTIAL".
- **The repo's own readiness matrix disagrees.**
  `docs/compliance/CMS-0057-F-READINESS-MATRIX.md` lists:
  - metrics: Phase 2 required (line 65);
  - Payer-to-Payer: Phase 2 required (line 59);
  - bulk export: scaffold (line 68).
- **Recommendation (owner decision):** make the evidence generator derive status
  from this matrix, or from assertion-level criteria, rather than hand
  declaration. Until then, pause the auto-publish or republish with corrected
  statuses. Because the output is public, I have **not** changed it.

### 6.2 Overclaims found (quote → reality)

| # | Location | Claim | Reality |
|---|---|---|---|
| 1 | `src/site/pricing.html:1030` | "All four required APIs are validated against ONC Inferno test suites." | No Inferno suite has run (`docs/interop/davinci.md:374`). |
| 2 | `src/site/docs/prior-auth-guide.html:563` | "passed Touchstone and Inferno testing suites and are production-deployed" | No such evidence; no production customer. |
| 3 | `src/site/docs/prior-auth-guide.html:545,555-557` | CRD / DTR / PAS "Production". Endpoints `/cds-services/prior-auth-check`, `Questionnaire/$populate`, `/fhir/Claim/$submit`. "Update … or cancel" via PAS. | Real routes differ (`cho-order-*`, `$questionnaire-package`, `/fhir/r4/...`). `$populate`, update and cancel don't exist. |
| 4 | `src/site/portal/api-docs.html:54,326-327` | "CMS-0057-F COMPLIANT"; "App Launch v2.0" | Not compliant (this matrix). SMART v1 scopes. |
| 5 | `src/site/docs/api.html:22,96` | "SMART App Launch v2 configuration" | v1 scopes; advertises unsupported `private_key_jwt`. |
| 6 | `src/site/docs/compliance.html:98-101,107,288-291` | All four APIs and SMART "Complete" / "Implemented November 2024" | Mock data, stub bulk export, PA data missing from APIs. |
| 7 | `src/site/docs/compliance.html:322` | Checker "validates … Da Vinci IG conformance" | Structural presence checks only. |
| 8 | `src/site/cms-0057f-compliance.html:1327,1330` | "X12 278 → FHIR PAS — Implemented"; SMART via "Azure AD — Implemented" | No 278 parser (fhir-service's own status text says so). The authorization server is OpenIddict. |
| 9 | Portal `Apis.razor:19,139`, `Welcome.razor:102,123`, `Pricing.razor:301` (anonymous pages) | "Production-ready", "CMS-0057-F compliant" | As above. |
| 10 | `docs/features/PRIOR-AUTHORIZATION-API.md:43,298-300` | "Meets all regulatory requirements"; `/api/prior-auth/cancel` | Describes a TS library plus removed Logic Apps. Cancel is only a URL string. |
| 11 | `docs/implementation/PRIOR-AUTH-IMPLEMENTATION-SUMMARY.md:7,18,495,505,513`; `docs/features/PRIOR-AUTH-IMPLEMENTATION-SUMMARY.md:511` | "Full CMS-0057-F compliance delivered", "Production Ready" | As above. |
| 12 | `docs/features/FHIR-INTEGRATION.md:11,69,75,175` | "Full Prior Authorization Final Rule implementation" | As above. |
| 13 | `docs/features/CMS-0057-F-COMPLIANCE.md:37-44,81-85,883` | "Complete implementation", Bulk FHIR, X12 278 ↔ FHIR "✅ Production" | Contradicts its own table at :64-66. |
| 14 | `docs/features/ROADMAP-2026.md:604-606` | "ONC certified"; "Bulk export tested" | No certification; bulk export is a stub. |
| 15 | `docs/releases/README.md:54`; `docs/features/UPDATES-SUMMARY.md:131,145` | "Complete CMS-0057-F compliance", "18 months early" | As above. |
| 16 | `docs/compliance/CMS-0057-F-READINESS-MATRIX.md` | Bulk export "Implemented / integration required"; mock adapters "in development"; the SMART row | Understates: bulk export is a stub, mocks are registered in every environment, and the SMART gaps aren't listed. |
| 17 | Code comments: `SA/Models/SmartScopes.cs:4`; `FS/Controllers/SmartConfigurationController.cs:8` | "v2" | v1. *Code comment, not public; fix in Phase 3 under the honesty rule.* |
| 18 | `README.md:160-166` (low) | Interop harness "proves" standards-conformant exchange | True only with CHO as the provider-side client; the README omits that. |
| 19 | `api/openapi/prior-auth-api.yaml` | `appointment-book` and `/cds-services/prior-auth-crd` | Not served. |

**Already honest (no action):** `src/site/assessment.html`,
`src/site/insights/cms-0057-f/qnxt-facets-healthedge-crd-dtr-pas.html`,
`src/site/evidence/*.html`, `docs/interop/davinci.md`, `docs/diligence/README.md`.

---

## 7. Historical owner-decision record

These touch the ground rules (major dependencies, persistence, public content)
or product policy.

| # | Decision | Why it matters | Recommendation |
|---|---|---|---|
| Q-1 | Evidence pipeline and public overclaims (§6) | Ground rule: no public copy changes without you. | Pause the auto-publish now. Approve a generator change that derives status from this matrix. You edit the copy. |
| Q-2 | Replace the mock FHIR data adapters with real adapters over member, eligibility, claims and authorization services (G-30) | **Persistence/data-source change.** It underpins every API requirement. | Approve. Adapters read existing services (no new store), tenant-scoped. Mock stays for Development only, behind an explicit flag. |
| Q-3 | FHIR profile validation in CI and at runtime (G-14) | **New dependency:** Firely .NET validator package and/or the HL7 validator jar in CI. | Firely validator in tests and CI; optional at runtime. |
| Q-4 | Subscription storage and delivery for PAS pended updates (G-15) | **Persistence:** a new Subscription collection and a delivery worker. | Approve a Mongo collection in fhir-service using the existing outbox pattern; rest-hook channel first. |
| Q-5 | Durable CRD classification and DTR stores (D-22) | **Persistence change.** | Approve: use the existing tenant Mongo store; remove the in-memory paths in production. |
| Q-6 | CQL and the DTR app (G-55, G-56) | **Major dependency:** a CQL engine (e.g. a Java CQF service or JS `cql-execution`) plus a SMART app. | **Don't build a DTR app.** Serve conformant packages (Questionnaire + CQL Library + ValueSets) for external and EHR-native DTR apps. That fits the 1upHealth-style connector goal. Author CQL as content; run CQL only in tests (a container in CI). |
| Q-7 | X12 278 parser/encoder (G-50) | **Dependency choice:** a library (e.g. an X12 toolkit) vs an in-house parser like the 837/835 code. | In-house, following the existing 837/835 parser pattern. No new dependency. |
| Q-8 | The < $500 auto-approve threshold that runs before clinical rules (`PasAutoAdjudicator.cs:150`) | Policy: auto-approves any low-cost request regardless of rules. | Make it per-tenant config, default **off**. |
| Q-9 | Fail-closed behaviour in claims when the auth lookup is down (D-04) | Changes claim outcomes; no posted money is affected. | Pend the claim (retry) rather than deny. |
| Q-10 | CMS-0062-P model fields (G-59) | **Schema change** (additive, nullable). | Approve the design ADR now; add the fields with the PAS fix so PAS stops dropping the code system. |

### 7.1 Owner decisions (2026-10-10)

| # | Decision |
|---|---|
| Q-1 | **Approved.** Pause CI's auto-publish of the CMS-0057-F evidence now. Change the generator so statuses come from this matrix. Corrected public wording goes in a separate PR that the owner approves. |
| Q-2 | **Approved.** Replace the mock FHIR data adapters with adapters that read the existing services. |
| Q-3 | **Resolved (2026-10-11).** The HL7 FHIR Validator (`validator_cli.jar`, Apache-2.0), in CI only, not at runtime (G-14). |
| Q-4 / Q-5 | **Use existing persistence only** for subscriptions and the CRD/DTR stores. If a new persistence path seems necessary, stop and ask. |
| Q-6 | **Approved.** No CHO DTR app; serve conformant questionnaire packages for external and EHR DTR apps. |
| Q-7 | **Approved.** In-house X12 278 parser and encoder, following the 837/835 pattern. |
| Q-8 | **Approved.** The auto-approve threshold becomes per-tenant and is off by default. |
| Q-9 | **Approved.** Pend the claim when the auth lookup is unavailable. Never pay or deny on a failed lookup. |
| Q-10 | Add nullable drug fields (benefit type, days supply, refills, code system) **only if small**; otherwise design-only in this matrix. |
| Q-11 (2026-10-11) | **Inpatient-Medicaid floor kept as it is** (every line of an institutional Medicaid POS-21 claim requires PA). Making it a plan-level setting, on by default for existing tenants, is a new open gap: G-61. |
| Q-12 (2026-10-11) | **EDI inpatient check not widened in PR #1299.** A separate PR aligns the EDI inpatient definition with the one pricing uses (bill type 11x), with tests that list exactly which claims change: G-62. |
| Q-13 (2026-10-11) | **Plan-outage pend kept**, under its own code `PLANUNAVAIL` (not `PAUNAVAIL`, not counted as missing authorization). A definitive plan 404 keeps the earlier path. |
| Q-15 (2026-10-11) | **Bulk re-run denial approved.** The `PAUNAVAIL`/`PLANUNAVAIL` bulk re-run may deny with CARC 197 after a definitive "not found" or "invalid" from authorization-service. A failed lookup must never lead to a denial. *As implemented (PR #1299):* the re-run finalizes a Deny only when it is the PA check's CARC 197 and no check on that run failed (PA or plan lookup, pricing, benefit engine, NCCI engine, any stage that threw); every other Deny stays pended for an examiner. |
| Q-16 (2026-10-11) | **Flag dependency.** `ClaimsAdjudication:PriorAuth:UsePlanFlaggedCodes` is turned on only after G-07 and two fixes: D-36 (approval re-runs call coverage-service with the examiner's token) and D-40 (plan lookups need `benefits:read` for examiner roles, or the service identity). Tracked as G-64. |

**Owner's sequencing:**
1. Step 0: overclaims PR, plus pausing the evidence auto-publish.
2. Owner's "wave 1": safety and isolation, covering G-02 (D-13), G-10 (D-12, D-16), G-11 (D-14, D-15) and G-05 (D-21). Each starts with a failing test.
3. Stop and report, then wait for go-ahead.

G-12, G-22 and G-23 from §8 Wave 0 follow in later waves.

**Owner wave mapping.** The owner numbers waves from 1; §8 numbers them from 0. The
owner's wave *N* is §8's Wave *N − 1*, with these explicit assignments (2026-10-11):

| Owner wave | Contents | §8 |
|---|---|---|
| 1 | G-02, G-10, G-11, G-05 (PR #1294) | Wave 0 (part) |
| 2 | G-01, G-03, G-04, G-07, G-08, G-24, G-25, G-26, G-27, **plus G-12** (claims pend when the auth lookup fails, Q-9) | Wave 1, plus G-12 from Wave 0 |
| 5 | Backlog G-37, G-38, G-39 (D-13 remainder) | Wave 4 |

**Backlog decided 2026-10-11 (owner's wave 5):** consent tied to the receiving payer
(G-37), a consent check on `$member-match` (G-38), and per-issuer scope limits (G-39), the
D-13 remainder.

**2026-10-11:** the owner pulled D-32 (SMART `scope`/`scp` claim mapping) forward from wave 6 as its own PR (#1298).

**2026-10-11, Q-14 (PAS urgency, PR #1300):** recognised urgent values (`stat`, `urgent`, `asap`, `U`, `03`, `expedited`) map to expedited (`U`) even under an unexpected code system, with the nonstandard system logged at warning level (values sanitized). Only genuinely unrecognised values default to standard, and are logged too. Extended in review: the most urgent signal across all sources wins, urgency stated as text counts, and `E` requires an explicit standard value in a known system.

---

## Remediation provenance

PRs #1391–#1399 strengthened persistence/failure handling; ADR 017 retired
Cosmos-only defects for the supported Mongo architecture. PR #1409 added ordinary
authorization lifecycle guards and expiry. Current scoped statuses and tests are
maintained once, in the [canonical capability ledger](CAPABILITY_RECONCILIATION.md).
The [earlier reconciliation text](https://github.com/aurelianware/cloudhealthoffice/blob/3b5a2035/docs/epa-gap-matrix.md)
remains a dated record, not a second current-status summary.

## 8. Historical planning register with scoped resolution annotations

The wave order, effort and dependencies below preserve the original planning record. Explicit resolved/partial annotations identify subsequent changes; unrechecked rows are not fresh gap findings. CAP-linked statuses are governed by the canonical ledger.

### Wave 0 — Safety and isolation (no new features)

| Order | ID | Gap | Sev | Effort | Deps |
|---|---|---|---|---|---|
| 1 | G-02 | Lock down P2P export and member-match: backend clients only, receiving payer bound to the client, HTTP-level negative tests (D-13). | Critical | S–M | — |
| 2 | G-05 | CRD fails safe: unknown → "contact payer / info-needed", never "no PA needed" (D-21, first part). | High | S | — |
| 3 | G-10 | Rule-set tenant from the token (D-12). Tenant on rule-repo writes (D-16). Restrict the platform seed endpoint (T-5). | High | S | — |
| 4 | G-11 | DTR search tenant filter (D-14). Admin-only questionnaire writes (D-15). | High | S | — |
| 5 | G-12 | Claims: never treat a failed auth lookup as valid (D-04). | Critical | S | Q-9 |
| 6 | G-22 | Gold card provider-scoped, or disabled until history exists (D-19). Auto-approve threshold off by default (Q-8). | High | S | Q-8 |
| 7 | G-23 | Smart-configuration advertises only what's supported. Fix the "v2" comments (D-25). Bulk `$export` returns 501 until real (D-28). | Medium | S | — |

### Wave 1 — Make the authorization record the system of record

| Order | ID | Gap | Sev | Effort | Deps |
|---|---|---|---|---|---|
| 8 | G-01 | PAS persistence: correct mapping from the Bundle, an internal decision-recording path, and fail-if-unpersisted. Integration test submit → persist → inquire → claim (D-01, D-02, D-03). | Critical | M | — |
| 9 | G-03 | Map `Claim.priority` → expedited; stamp receipt on arrival (D-08). | Critical | S | G-01 |
| 10 | G-04 | **Implemented tenant iteration:** real-store watchdog tests pass; per-tenant-database mode and alert delivery remain separate (CAP-07). | Historical Critical | I+T (scoped) | — |
| 11 | G-07 | **Implemented (CHO Replace mode).** Line-level A1/A2/A3/A4 decisions, stable identities, quantities, reasons, history snapshots and PAS projections (D-06). G-13/G-17 conformance remains. | High | M | G-01 |
| 12 | G-08 | **Implemented at CHO write boundary.** Coded and human-readable reasons required for denied/reduced lines (D-10); G-17 validates code-set semantics. | High | S | G-07 |
| 13 | G-24 | **Implemented for ordinary CHO workflows:** guarded transitions, cancellation history, atomic tenant/revision-fenced expiry sweep (D-23). D-38 and separate overturn/reopening workflows remain. | Medium | M | G-07 |
| 14 | G-25 | 837 REF*G1 at claim and line level (D-05). Claims check auth beyond inpatient Medicaid, configurable per plan. | High | S–M | G-12 |
| 14a | G-61 | The inpatient-Medicaid PA floor becomes a plan-level setting, on by default for existing tenants (Q-11). Today it is hard-coded in `BenefitCalculationStage`. | Medium | S–M | G-25 |
| 14b | G-62 | EDI inpatient claims: align the PA check's inpatient definition with pricing's (bill type 11x) so 837I inpatient claims reach the floor; tests list exactly which claims change (Q-12). | High | S–M | D-05, G-25 |
| 14c | G-64 | **Still disabled by default:** G-07 and D-36/D-40 code fixes are implemented. Enabling `ClaimsAdjudication:PriorAuth:UsePlanFlaggedCodes` remains a separate scoped rollout (G-25, Q-16), with deployment identity and claim/authorization line mapping verified. **Line-selection follow-up:** explicit native authorization line references and ambiguity pending are implemented; member binding is implemented with explicit comparison confirmation and dependent intake guards; unit identity preservation is implemented (CAP-25), but reservation/consumption accounting, EDI enrichment and target-deployment validation remain open. See `operations/AUTHORIZATION-UNIT-ACCOUNTING.md`. See `operations/CLAIMS-AUTHORIZATION-MEMBER-BINDING.md` for rollout and historical identity limits. See `operations/CLAIMS-AUTHORIZATION-LINE-MATCHING.md`. | Medium | S | G-07, D-36, D-40 |
| 15 | G-26 | Cosmos defects: enum serialisation (D-18), rule ListAsync (D-17), with emulator tests. **Done:** D-17 (PR #1294), D-18 (PR #1297). | Medium | S | — |
| 16 | G-27 | Bind the PAS caller to token claims (D-26). Fix the portal submit (D-24). | Medium | S | G-01 |

### Wave 2 — CMS-0057-F operational (already in force)

| Order | ID | Gap | Sev | Effort | Deps |
|---|---|---|---|---|---|
| 17 | G-18 | **Partial:** receipt-based 72 h / 7 calendar day UTC operational target persisted; no RFI restart (D-09 fixed). Program-specific extensions/tracking and tighter rules remain. | Critical | M | G-03 |
| 18 | G-19 | Breach and at-risk alerts as events to the UM queue. | High | M | G-04, G-18 |
| 19 | G-20 | **Partial:** counts/rates, standard/expedited split and mean/median exist. Appeal/extension outcomes, annual publication and complete public artifacts remain distinct (CAP-06). | Historical Critical | Partial | G-01, G-07, G-18 |
| 20 | G-17 | Denial reasons in PAS `reviewAction.reasonCode` (X12 HCR03 code set); stop putting ad-hoc codes in the HL7 adjudication-error system. | High | S–M | G-08, G-13 |
| 21 | G-21 | Provider and member decision notices (letters), using correspondence/document infrastructure. | High | L | G-08 |
| 22 | G-09 | **Done** (ADR 021): Provider Access opt-out semantics (D-11). | High | S | — |

### Wave 3 — PAS conformance

| Order | ID | Gap | Sev | Effort | Deps |
|---|---|---|---|---|---|
| 23 | G-13 | PAS STU2 response: `reviewAction` on `item.adjudication`, item-level responses on `$submit`, a complete response Bundle, `Claim.use` check. | High | M | G-07 |
| 24 | G-14 | PAS / CRD / DTR / PDex profile validation in tests and CI (D-30 relief), with the HL7 FHIR Validator (`validator_cli.jar`, Apache-2.0) in CI only; no runtime validation (Q-3 resolved). | High | M | Q-3 (resolved) |
| 25 | G-15 | R5-backport topic-based Subscriptions for pended → final (rest-hook). | High | L | Q-4, G-01 |
| 26a | G-63 | Exchange / Marketplace line of business: map the X12 1032 and SOPT codes that identify Marketplace (QHP) coverage to `Exchange`, so Marketplace requests reach the Marketplace enrollment gate and rule sets instead of being decided as Commercial or pended as Unknown (found in review of PR #1300). | Medium | S | — |
| 26 | G-16 | Update and cancel of a submitted request. QuestionnaireResponse used in adjudication. RFAI raised automatically on PAS pends. Attachments write back to the authorization. | Medium | M | G-01 |

### Wave 4 — PA data in the CMS-0057-F APIs (due 1 Jan 2027)

| Order | ID | Gap | Sev | Effort | Deps |
|---|---|---|---|---|---|
| 27 | G-30 | Real tenant-scoped data adapters replace the mocks in non-Development environments (T-4). | Critical | XL | Q-2 |
| 28 | G-31 | Patient Access PA data (PDex prior-authorization profile), within 1 business day, drugs excluded. | High | L | G-30, G-01 |
| 29 | G-33 | Provider Access: attribution feed and PA data. | High | L | G-30, G-09 |
| 30 | G-34 | Real Bulk Data `$export` (Group / Patient / system; `_since`; NDJSON output; download route). | High | L | G-30; storage decision under Q-2 |
| 31 | G-35 | Payer-to-Payer: PA data, multi-tenant member-match, bulk transfer, 1-business-day handling. | High | L | G-30, G-34, G-02 |
| 32 | G-36 | CapabilityStatement completeness. Public Provider Directory (D-29). | Medium | S–M | — |
| 32a | G-37 | **Done** (ADR 022): Payer-to-Payer consent bound to the receiving payer: an export returns only members whose consent names the requesting payer (D-13 remainder). Owner's wave 5. | High | M | G-02, G-35 |
| 32b | G-38 | **Done** (ADR 022): Consent check on `Patient/$member-match` (PDex Consent in the request, matched to the receiving payer) before any match is returned (D-13 remainder). Owner's wave 5. | High | M | G-37 |
| 32c | G-39 | Per-issuer scope limits: `TrustedIssuerOptions` gains an allowlist so only named issuers can mint `system/PayerToPayer.read` and other sensitive scopes (D-13 remainder). Owner's wave 5. | Medium | S–M | G-02 |

### Wave 5 — SMART / OAuth2

| Order | ID | Gap | Sev | Effort | Deps |
|---|---|---|---|---|---|
| 33 | G-40 | Launch context in the token response (`patient`, `encounter`, `fhirContext`, `need_patient_banner`). External EHR launch handshake. | High | M | — |
| 34 | G-41 | SMART v2 scopes (alongside v1). `aud` validation at the authorization server, with negative tests at the resource server. Revocation. Introspection permissions. | High | M | — |
| 35 | G-42 | Asymmetric client auth (`private_key_jwt`, JWKS / jwks_uri) for backend services. P2P outbound credentials. | High | M | — |

### Wave 6 — CRD

| Order | ID | Gap | Sev | Effort | Deps |
|---|---|---|---|---|---|
| 36 | G-43 | CDS Hooks JWT authentication (EHR JWKS, `iss`, `aud`, `jti` replay protection, per-tenant EHR registry). | High | M | G-42 |
| 37 | G-46 | Real context: prefetch and `fhirAuthorization` fallback. Member coverage, LOB and state from eligibility. Diagnoses. Benefit-plan and reference-data PA flags. Correct code typing (D-21 remainder). | High | M | G-43, G-30 |
| 38 | G-44 | `coverage-information` extension via systemActions. Spec card types. `selections`. | High | M | G-46 |
| 39 | G-45 | Propose-alternate and request-form-completion actions. | Medium | M | G-44 |
| 40 | G-47 | Hooks: `appointment-book`, `order-dispatch`, `encounter-start`, `encounter-discharge`. | Medium | M | G-44 |
| 41 | G-48 | Configuration options. Timeouts and response-time budget, with a test. | Medium | S | G-44 |

### Wave 7 — DTR

| Order | ID | Gap | Sev | Effort | Deps |
|---|---|---|---|---|---|
| 42 | G-53 | **Partial:** DTR create, conditional update, reads, search and existence durability implemented and real-Mongo tested. Complete answer validation and canonical URL/version resolution remain; package work is G-54/G-55 (CAP-04/15). | Medium | Partial | Q-5 |
| 43 | G-54 | `$questionnaire-package` per STU2 (coverage + order inputs; Parameters with PackageBundle), declared in the CapabilityStatement. | High | M | G-53 |
| 44 | G-55 | SDC/DTR-conformant questionnaire content plus CQL Libraries and ValueSets (content, not engine). CQL tested in CI. | High | L | Q-6, G-54 |
| 45 | G-56 | DTR launch link with `appContext` to an **external** DTR app (no CHO app). | Medium | S | G-40, G-55, Q-6 |
| 46 | G-57 | Adaptive forms (`$next-question`). | Low | L | G-55 |

### Wave 8 — X12 278 and connectors (overlaps Phase 4)

| Order | ID | Gap | Sev | Effort | Deps |
|---|---|---|---|---|---|
| 47 | G-50 | .NET X12 278 request parser and response encoder (HCR reasons), bridged to the authorization service and PAS. Retire the orphaned TS mapper and Argo stub, or wire them in. | High | L–XL | Q-7, G-07, G-08 |
| 48 | G-51 | 278 inquiry (X215 / X217). | Medium | M | G-50 |
| 49 | G-52 | Intermediary pattern (FHIR PAS ↔ X12 payer). Phase 4 connector. | Medium | L | G-50 |
| 50 | G-59 | CMS-0062-P design ADR plus additive model fields; PAS keeps the drug code system (§4.7). | Medium | S (design) + S (fields) | Q-10 |
| 51 | G-58 | UDAP (deferred; optional). | Low | L | G-42 |

### Continuous

| ID | Item |
|---|---|
| G-60 | Validation against the HL7 Da Vinci reference implementations (CHO as **server**) and Inferno test kits. Results recorded in §9 as each wave lands. |

---

## 9. External validation results

Nothing has been run against CHO **as a payer server** yet. The existing
`tests/DaVinciInterop.Tests` runs CHO as a *client* of the Da Vinci reference
payer; that is real but different evidence.

| Test kit / RI | CHO role | Wave | Result |
|---|---|---|---|
| Inferno: Da Vinci PAS test kit (server) | Payer | 3 | Not run |
| Inferno: Da Vinci CRD test kit (server) | Payer CDS service | 6 | Not run |
| Inferno: Da Vinci DTR test kit (payer server) | Payer | 7 | Not run |
| Inferno: SMART App Launch STU2 test kit | Authorization server + FHIR server | 5 | Not run |
| Inferno: Bulk Data Access test kit | FHIR server | 4 | Not run |
| Inferno: PDex / Payer-to-Payer, Patient Access (CARIN BB) test kits | FHIR server | 4 | Not run |
| Da Vinci PAS / CRD / DTR reference implementations | Counterparty to CHO as server | 3, 6, 7 | Not run |
| Existing interop harness (CHO as client) | Provider-side client | — | Passing (pre-existing) |

Whether these kits can run in CI depends on Docker and the network policy;
that will be confirmed at the start of Phase 3.

---

## 10. Change log

| Date | Change |
|---|---|
| 2026-10-10 | Initial audit and gap matrix (Phase 1–2), baseline `31a462ff`. |
| 2026-10-10 | Added §11, the per-scenario evidence status table read by `tools/Cms0057Evidence` (Q-1). Evidence auto-publish paused in CI. |
| 2026-10-11 | Owner's wave 1 (PR #1294): D-12, D-14, D-15, D-16, D-17 fixed; D-13 and D-21 partially fixed (remainders stated in §3); T-5 seed endpoint restricted. §4.2, §4.5 and §5 rows updated. Each fix has a test that failed on the original audit baseline. Review follow-ups in the same PR: explicit `system/PayerToPayer.read` for P2P (D-13 stays High); Cosmos `GetRulesAsync` fixed on the production serializer (D-17); tenant id `default` reserved (D-14); CRD uncoded orders and evaluation failures answer unknown (D-21); D-15 wording; new defect D-31. §11 justifications for PAS-01, PAS-02 and P2P-01 updated; no status changed. |
| 2026-10-11 | PR #1294 re-verification: D-13 / T-1 remainder extended (consent not bound to the receiving payer; `$member-match` not consent-gated; conditions for a Medium window) and the stale "no HTTP-level test" sentence dropped; new D-32 (`scp` claim mapping); CRD uncoded-order labels fixed and blank codes skipped (D-21). |
| 2026-10-11 | D-31 fixed (PR #1295): benefit-plan-service uses the shared Cosmos serializer; queries and models fixed for the stored shape; runbook for older Cosmos data. New D-33 (`GetDefaultForPlanAsync` filters on a property nothing writes). |
| 2026-10-11 | PR #1295 review: D-33 widened to both backends (Mongo filters on the same unwritten property); new D-34 (PA-rule cache flush misses `RedisTimeoutException`) and D-35 (fee-schedule `@serviceDate` bound as a string); D-31 runbook revised for containers shared with fhir-service and claims-service; benchmark seeder writes plan enums in the stored form. |
| 2026-10-11 | D-13 / T-1 receiving-payer binding (PR #1296): backend client registrations carry a `payerId`, minted as `payer_id`; fhir-service believes it only from the one issuer marked `IssuesChoClientRegistrations` (startup requires exactly one) and refuses unmapped clients and mismatched body payers before any member lookup. D-13 stays High (consent not bound to the receiving payer; `$member-match` not consent-gated; no per-issuer scope allowlist). §4.5 row and the §11 P2P-01 justification updated; no status changed. Deploy impact: clients of external IdPs must be registered with CHO before using Payer-to-Payer. |
| 2026-10-11 | D-32 fixed (PR #1298, pulled forward from wave 6 by the owner): the SMART scheme no longer maps inbound claims; one `scope`/`scp` parser; tokens whose two claims differ are refused (401). §4.6 scope-enforcement and multi-issuer rows updated. Behaviour change: `scp`-only issuers (Entra, Okta) now work, and issuer claim mappings naming `tid`/`oid`/`upn`/`email` now resolve; the idp-integration-contract Entra example, which mapped `tid`, was corrected to map a CHO tenant claim. |
| 2026-10-11 | Owner's wave 2, part B (PR #1297): D-07 fixed (SLA watchdog per tenant, conditional escalation, tested on MongoDB and the Cosmos emulator) and D-18 fixed (shared Cosmos serializer, enums stored as strings, queries bind enums; summary, open-ended dates and turnaround fixed); runbook section for older Cosmos authorization data. Owner decisions recorded: Q-3 resolved (HL7 FHIR Validator in CI only; G-14); new backlog G-37, G-38, G-39 (D-13 remainder, owner's wave 5); D-32 pulled forward and fixed in #1298; owner wave mapping added under §7.1. §1, §2.1 and §4.4 rows for D-07 / D-18 updated. |
| 2026-10-11 | PR #1297 review: D-07 escalation on Cosmos is a two-field ETag patch (no whole-document rewrite), with guard tests on both stores; watchdog caches tenants hourly, isolates record failures, and warns instead of silently doing nothing under Mongo tenant scoping; a deployed AS host with no database refuses to start. New D-37 (unindexed tenant listing; index proposed, owner approval) and D-38 (Mongo tenant scoping unreachable from background workers, incl. retention). D-35 extended with the AS summary's date-string comparison. G-26 done. |
| 2026-10-20 | Rescoring after ADR 018 (PR #1420 merged): §4.5 "Real, tenant-scoped data source" S → P (Patient and Coverage from member-service and coverage-service when bound to `ChoNative`); T-4 narrowed. §11 PAT-01, PROV-01 and P2P-01 justifications updated; no status changed. PAT-01's product path is real (EOB proxied to claims-service) but its acceptance test still runs on the mock; PROV-01 lacks PA data; P2P-01 still reads the mock member source and D-13 is open. |
| 2026-10-20 | PAT-01 GAP → PARTIAL: new §4.5 row for member claims as ExplanationOfBenefit (P; real, tenant-scoped, patient-confined path, not CARIN BB), backed by the Replace-tagged `PatientAccessClaimsTests` over the real fhir-service → claims-service path. fhir-service now withholds (502) a confined EOB search whose results include another member. Inventory row for Patient Access data corrected (the mock backs only P2P). |
| 2026-10-20 | Fixed: Payer-to-Payer `$member-data-export`, the `$member-match` answer and the Provider Directory Location search wrote each bundle entry's resource by its declared base type, so only `resourceType`, `id`, `meta` and `extension` reached the caller (no demographics, coverage or claims content). Entries are now written by their runtime type; an HTTP-level export test asserts the content. No §11 status changes: the export's data source is still the mock (P2P-01 GAP). |
| 2026-10-20 | ADR 020: Payer-to-Payer member source, `$member-match` candidates and export can be bound to CHO's own services (`FhirAdapters:Sources:PayerToPayer=ChoNative`, set in the manifest). §4.5 data-source row, T-4 and the Patient Access inventory row updated; P2P-01 and P2P-04 justifications updated, no status changed (P2P-01 still rests on missing PA data and D-13; P2P-04 is single-tenant and undeclared). |
| 2026-10-20 | D-11 fixed (ADR 021): Provider Access shares unless the member opts out. PROV-03 GAP → PASSABLE and CONSENT-01 PARTIAL → PASSABLE, each on new Replace tests through the real composed decision; §4 Provider Access rows and G-09 updated. Totals 5 PASSABLE, 13 PARTIAL, 3 GAP. |
| 2026-10-20 | ADR 022 (D-13 remainder, G-37/G-38): `$member-match` requires the member's HRex Consent naming the requesting payer and records it per payer; the export serves only a payer whose consent is in force; outbound sends the consent. D-13 and T-1 lowered to Medium (G-39 remains). P2P-01, P2P-03, P2P-04 justifications updated; no status changed. |
| 2026-10-22 | G-31 closed and the PA halves of G-33 / G-35 closed: prior authorizations are now served over FHIR as PDex prior-authorization `ExplanationOfBenefit` (`use = preauthorization`). authorization-service projects its own records (`PriorAuthorizationEobProjector`) behind `GET fhir/ExplanationOfBenefit/prior-authorization[/{id}]`, with the rule's drug exclusion applied as a pure, fail-closed scope test (`PriorAuthorizationApiScope`: pharmacy service type 88, NDC, RxNorm, HCPCS J-codes; one drug line excludes the record) and out-of-scope reads answering 404 rather than 403 so existence does not leak. fhir-service routes to it on a `pa-` id prefix or `use=preauthorization`, under a per-branch `authorizations:read` guard, through the same `AuthorizedMemberId` confinement Patient Access and Provider Access already use; the two result sets are deliberately not merged (independent upstream cursors), and the CapabilityStatement declares the `use` search parameter and the PDex profile. Payer-to-Payer exports the member's active, pending and recently decided authorizations under a separate allowlist (decision, approved period, denial reason and unit counts leave; nothing monetary does, and the claim allowlist is unchanged) with a one-year lookback matching the prior-auth retention anchor. §4.5 PA rows M → P; §11 PAT-03 GAP → PARTIAL and P2P-01 GAP → PARTIAL; PROV-01 stays GAP (Encounter and Claim still come from the mock adapter, G-30). Totals 5 PASSABLE, 15 PARTIAL, 1 GAP. Nothing moved to PASSABLE: the 93 new tests are service-level and HTTP-level, not Replace-tagged `tests/Cms0057Acceptance.Tests` scenarios, and the projection has not been validated against the PDex profile (G-14). |

---

## 11. Evidence scenario status (generator input)

This section is **machine-read**. `tools/Cms0057Evidence` takes the Cloud Health
Office (Replace) status of every CMS-0057-F acceptance scenario from the table
below, not from `tests/Cms0057Acceptance.Tests/scenarios.json`. The manifest
still holds each scenario's name, capability and external-core (Augment)
statuses; it no longer holds a Replace status.

**How a status is chosen.** Each scenario takes the class of the requirement
that defines it in §4 (or §2.1 when §4 has no row):

| Matrix class | Evidence status |
|---|---|
| I+T, and the backing acceptance tests exercise the behaviour (not tautological, §6.1) | `PASSABLE` |
| P or I-U, or I+T backed only by tautological tests | `PARTIAL` |
| S or M, or a defect that inverts the required behaviour | `GAP` |
| No dependency on CHO product code | `N/A` |

`PASSABLE` here means the scenario as this repository defines it is supported by
the tested code. It is not CMS certification and not a conformance claim for any
Da Vinci IG.

**Rules the generator enforces** (the build fails otherwise):
- exactly one row per scenario in `scenarios.json`, and no row for anything else;
- no duplicate rows;
- status is exactly one of `PASSABLE`, `PARTIAL`, `GAP`, `N/A`;
- matrix refs and justification are not empty;
- a `PASSABLE` row still needs at least one passing, non-GAP, Replace-tagged
  acceptance test for that scenario.

**Editing.** Keep the table between the two marker comments. Keep the four
columns in this order. Write a literal pipe in a cell as `\|`. When a fix lands,
change the row here in the same PR as the code and its test.

<!-- cms0057-evidence-status:begin -->
| Scenario | Status | Matrix refs | Justification |
|---|---|---|---|
| PAS-01 | PARTIAL | §4.2, D-21, G-05, G-44, G-46 | CRD order-select/order-sign answer with free-text cards only; no coverage-information extension; context is hard-coded TX/Medicaid/STAR. Unknown codes now answer "could not be determined" (D-21 first part, PR #1294), which does not lift the class. |
| PAS-02 | PARTIAL | §4.3, D-22, G-53, G-54, G-55 | DTR now persists Questionnaires and responses through MongoDtrResourceStore (ADR 017). Updates, searches and existence checks now also use Mongo across pods, with conditional writes and legacy compatibility tests. Complete answer validation, canonical resolution and STU2 packages with CQL Libraries/ValueSets remain open. |
| PAS-03 | PARTIAL | §2.2, §4.1, D-01, D-02, D-03, G-01, G-13, G-14 | `$submit` now stores the request and its decision as a durable record before answering, and fails without a `preAuthRef` if it cannot (D-01/D-02/D-03 fixed, PR #1300; real-seam test). Still partial: `Claim.use` is not enforced, there is no PAS profile validation, and the response is not STU2-complete (§4.1 `$submit` row, P). |
| PAS-04 | PARTIAL | §4.1, D-26, G-13, G-27 | `$inquire` finds authorizations issued by PAS `$submit` and answers with the stored decision (PR #1300, seam-tested). Still partial: the corroborating key comes from the request body, not the token (D-26), and complete PAS STU2 response conformance remains unverified (G-13); committed item-level outcomes are now projected. |
| PAS-05 | PARTIAL | §4.4, D-10, G-08, G-17 | A coded and human-readable reason is required for CHO denied/reduced decisions and persisted per line. PAS projects line outcomes and reasons; X12 reason-code semantics and `reviewAction.reasonCode` conformance remain (G-17). |
| PAS-06 | PARTIAL | §4.4, §6.1, G-18, G-19, D-38 | Receipt and urgency produce a persisted target, enforced by tenant-scoped watchdog escalation; RFAI cannot restart it. Real-Mongo and CHO acceptance tests replace label-only evidence. Program-specific extensions, alerts and tenant-database background support remain open. |
| PAS-07 | PASSABLE | §2.1, §4.1, D-09, G-16, G-18 | CDex additional-information exchange is implemented and tested (I+T). Limitations: raised only from reviewer decisions, never from PAS pends; document arrival returns the record to review while preserving its original receipt deadline (D-09 fixed). |
| PAS-08 | PASSABLE | §2.1, §4.4 | Drug and benefit exclusion from CMS-0057-F medical PA scope is implemented and tested (`DrugExclusionEvaluator`, I+T). Since PR #1300 it also applies to PAS `$submit` records (the stored exclusion denial is what the provider is told). Limitation: the exclusion catalog is empty by default. |
| PROV-01 | GAP | §4.5, §5 T-4, §6.1, G-30, G-33 | Updated 2026-10-22; status unchanged. The PA-data half is no longer missing: the §4.5 "Provider Access: PA data" row is now P, because an attributed provider receives the member's prior authorizations through the same confined path as Patient Access. Still GAP on the rest of the row it rests on: the claims and encounter data a provider receives still come from `MockFhirDataAdapter`, which has no CHO-native source for Encounter or Claim (G-30), and the backing acceptance test still runs on mock data. |
| PROV-02 | PARTIAL | §4.5, §6.1, G-33 | Attribution is enforced and fails closed, but only from static configured panels. The backing test's "not attributed" case is a nonexistent member id. |
| PROV-03 | PASSABLE | §4.5, §6.1, D-11, G-09 | Updated for ADR 021: an attributed provider is served unless the member has an in-force opt-out (a Deny consent); revoking the opt-out resumes sharing; an unreadable registry refuses. Backed by Replace tests through the real attribution source, registry evaluator and policy (`PROV03_Replace_*`) and by an HTTP test. Limitations: member notices about the opt-out are the plan's process; attribution is static (PROV-02). |
| P2P-01 | PARTIAL | §4.5, §5 T-1/T-4, D-13, G-02, G-30, G-35 | Rests on an M row plus an open Medium defect (updated 2026-10-20 for ADR 020 and ADR 022; status unchanged): the export now sends the member's Patient, Coverage and in-window EOBs from CHO's own services when bound to `ChoNative` (since PR #1427 in full, and with remittance and cost-sharing data removed as CMS-0057-F requires), and since ADR 022 is served only to a requesting payer whose consent naming it is in force. Rescored 2026-10-22: the last M on the row is closed — the export now also carries the member's prior authorizations as `use = preauthorization` EOBs, with the active and pending ones always in and the decided ones for a year past their approved period, under a redaction allowlist of their own. PARTIAL, not PASSABLE: a Mock binding still exports no PA data, the projection is not validated PDex, and the backing tests are service-level rather than in the acceptance suite. D-13 is down to Medium (no per-issuer scope allowlist, G-39). |
| P2P-02 | PARTIAL | §4.5, §4.6, G-35, G-42 | Outbound initiation is built behind a transport seam; outbound payer credentials are unconfigured by design and PA data is not exchanged. |
| P2P-03 | PASSABLE | §4.5 | Updated for ADR 022. Inbound, the requesting payer's opt-in arrives as an HRex Consent naming it; CHO validates it, records it per payer, and serves only that payer (`P2P03_Replace_*` inbound, through the real exchange service). Outbound, the member's opt-in comes from CHO's tenant-scoped registry and travels as an HRex Consent a peer can bind (`P2P03_Replace_*` outbound). Limitations: a `#regular` or otherwise restricted consent is refused because CHO cannot segment sensitive data; CHO relies on the requesting payer's attestation; an opt-out in CHO's own registry does not refuse inbound exchange (owner sign-off pending). |
| P2P-04 | PARTIAL | §4.5, G-35 | Updated 2026-10-20 for ADR 020: with the `ChoNative` binding, `$member-match` finds candidates in member-service by member id or subscriber in any case or separator spelling, or, without a member id, by birth date (a superset of every strong match except by SSN; more than 500 on one path, or paging that changes mid-read, answers ambiguous) and selects among coverage-service coverages; SSN-only requests answer insufficient criteria and a supplied SSN that cannot be verified answers ambiguous. Since ADR 022 it requires the member's HRex Consent naming the requesting payer before any lookup and records it for that payer. Still partial: the input is CHO's JSON shape carrying the Consent, not the PDex `Parameters` resource; a single configured tenant, not declared in the CapabilityStatement, and no SSN index. |
| PAT-01 | PARTIAL | §4.5, §5 T-4, G-30, G-60 | Rests on the §4.5 row "member claims as ExplanationOfBenefit (CARIN BB)", P. The path is real and tested: `PatientAccessClaimsTests` (Replace) drives fhir-service's EOB read and search over HTTP into claims-service's controller and projector, proving own-claims search, another member's read answered 404, an explicit patient parameter unable to widen a patient token, and results outside the member withheld (502). Not PASSABLE: the projection is not CARIN BB (no profile, required elements missing, not validated). |
| PAT-02 | PARTIAL | §4.5, G-14, G-30 | Clinical resources are served only from Payer-to-Payer imports; no US Core profile is claimed or validated. |
| PAT-03 | PARTIAL | §4.5, §6.1, G-31 | Rescored 2026-10-22. An API now exposes PA data to patients: authorization-service projects the prior-authorization record to `use = preauthorization` EOBs and fhir-service serves them under the patient binding, with status, requested and approved dates, items, requested and approved units, and the denial reason, and with drugs excluded (an out-of-scope read answers 404). PARTIAL, not PASSABLE: the §4.5 row is P — the projection is not a validated PDex prior-authorization resource (`type` is claim-type `other`, `insurer` is a display string, unit adjudication uses a CHO-private category system) — and the backing tests are service-level, so the acceptance suite's own test for this scenario still asserts only a supported-types list. |
| SEC-01 | PARTIAL | §4.6, D-25, G-40, G-41, G-42 | Auth code/PKCE, refresh, scope/tenant controls, CHO launch context and approval withdrawal have implementation evidence (CAP-05/11). Full SMART token-response, v2/asymmetric authentication and production conformance remain separate validation scopes. |
| CONSENT-01 | PASSABLE | §4.5, §6.1, D-11, G-09 | Updated for ADR 021: one registry and one policy serve both purposes with the semantics each requires: Payer-to-Payer needs an opt-in, Provider Access shares unless the member opts out, and a record for one purpose never decides the other. The Replace tests drive the real service, evaluator, policy and HTTP registry source (`CONSENT01_Replace_*`), replacing the reflection test. |
| METRICS-01 | PARTIAL | §4.4, G-20 | Production Cms0057Reporting computes standard/expedited counts, rates, mean/median decision time and timeliness. Acceptance now drives that real calculator. Approved-after-appeal and extended-then-approved remain explicitly untracked; the complete required metric set is not yet supported. |
<!-- cms0057-evidence-status:end -->

**Totals:** 5 PASSABLE, 15 PARTIAL, 1 GAP, 0 N/A (21 scenarios). The generator
reports these itself; update this line when a row changes.
| 10 | Status | `POST Claim/$inquire` → `GET api/authorizations/number/{n}` | ✅ Finds PAS-issued numbers and answers with the stored decision (PR #1300, seam-tested). Corroboration still comes from the body, not the token (D-26). |
| 11 | Claim against the auth | 837: `X12837Parser` (REF*G1 at 2300; at 2400 on an 837P). REST and EDI: `/validate` per line, with claims-service's service token | ✅ with limits (PR #1299). Each line that requires PA is validated with its own number (2400 REF*G1, else 2300), procedure code and date. Valid → continues. Number missing, unknown to the tenant (the service's own 404) or Invalid → deny, CARC 197. Lookup unavailable (5xx, 401/403, timeout, transport error, bare 404, unreadable body) → pend `PAUNAVAIL` before the benefit engine (no accumulator commit prepared); with plan-flagged codes on, a benefit-plan outage on a claim outside the floor → pend `PLANUNAVAIL`. Neither is overridden by an approval; both are cleared by the examiner approval re-run or the bulk re-run, which pass or deny only on a definitive answer (D-04, Q-9). Scope: every line of an institutional Medicaid POS-21 claim (floor; G-61); plan-flagged codes (`benefits[].priorAuthRequired`/`requiresPriorAuth` + `cptCodes`) only with `ClaimsAdjudication:PriorAuth:UsePlanFlaggedCodes`, off by default pending G-64 service-identity/scope fixes (G-25). Limits: the floor does not recognise EDI claims (the mapper sets LOB Commercial and an 837I carries the facility type, e.g. 11, where POS is read), so EDI claims are not checked by default (G-62); `/validate` now reads service-line outcomes written by reviewer/automated decisions (D-06 fixed); repeated procedure codes require an explicit authorization `lineNumber`, which claims-service does not yet correlate automatically; PAS-issued numbers are persisted before `$submit` responds (D-01, PR #1300). |
| D-01 | Critical | PAS `$submit` never persists. The payload fails validation, the 400 is ignored, and the exception is swallowed. A `preAuthRef` is issued for a record that does not exist. | `FS/Controllers/PasController.cs:344-381`; `AS/Models/Authorization.cs:80-82,135-137`; confirmed by probe | Map fields correctly. Fail `$submit` (OperationOutcome) if the record isn't stored. Add an integration test across the seam. | **Fixed** (`d0681cee`, `6a1d76d3`; failing test `7f901d5c`; PR #1300). `$submit` maps the record from the Bundle, refuses an incomplete request (422) before adjudicating, stores it through the internal automated-decision path, and answers only after the write succeeded; a failed write is a 503/502 OperationOutcome with no `preAuthRef`. The answer follows the stored status (a plan exclusion denies). Review follow-ups: the response is built from the stored record (status, approved period, denial reason); a stored status other than Approved/Denied/Pended is a 502 with no `preAuthRef`. Recording is idempotent across a lost answer: fhir-service sends a payload hash, authorization-service answers a repeat of the same number and hash with the stored record (200) and keeps 409 for anything else, and on an unavailable write fhir-service reads the record back once by number and answers from it if the hash matches. **2026-10-19:** the subsequent Mongo uniqueness fix is verified in CAP-12; this earlier race description is historical. Seam test `PasSubmitPersistenceSeamTests` runs fhir-service and authorization-service in process over EphemeralMongo; all nine of its tests failed on the original audit baseline (400: `ServiceTypeCode` "professional", empty `RequestingProviderNPI`; `$inquire` 404). |
| D-02 | Critical | `POST api/authorizations` forces `Submitted` and runs `ClearReviewOutcome`, wiping automated PAS decisions and reasons. (The clearing is a deliberate security control for external REST callers and is pinned by `Submit_IgnoresBodyTenantActorAndReviewOutcome`.) | `AS/Controllers/AuthorizationsController.cs:132-145,162-192` | Keep the control. Add a separate internal, service-token-only "record automated decision" path that writes status history attributed to the adjudicator. | **Fixed** (`6a1d76d3`, PR #1300). Control kept: `POST api/authorizations` still forces Submitted and clears outcomes (`Submit_IgnoresBodyTenantActorAndReviewOutcome` unchanged). New `POST api/internal/authorizations/automated-decisions`, `[RequireServiceClient("fhir-service")]`: user tokens (even with `authorizations:decide`), other services and user-issuer tokens claiming `sub=fhir-service` get 401/403 (`AutomatedDecisionEndpointTests`). Tenant and actor from the service token; history = receipt (at `ReceivedAt`) + decision (`Source = automated:<rule>;lob=<lob|n/a>;state=<state|none>`); duplicates 409 (same request hash → 200 with the stored record); backdated receipts and inconsistent periods 400; a plan exclusion still outranks the outcome. Review follow-ups: `GET .../automated-decisions/{number}` (same restriction) for the compensating read; `AuthorizationNumber` must match `^[A-Za-z0-9-]{1,50}$`; a decision whose LOB is not the record's is 400; negative tests with hand-forged tokens (user issuer carrying `cho.service` with `sub=azp=fhir-service`, `sub≠azp`, no `azp`, workload issuer, unknown issuer) and an `X-Tenant-ID` spoof (403). `[BsonIgnoreExtraElements]` on the Authorization document and its nested documents, so a later build's fields do not break reads after a rollback (Mongo only; Cosmos ignores unknown properties. A rollback to a build from before this PR still throws, on Mongo, on documents carrying `PatientReference`, `RequestHash` or `StatusHistory[].Source`). |
| D-03 | High | PAS persists placeholder data ("PAS"/"Patient", DOB = now−30y, LOB Commercial, service date = now). The approved period is hard-coded to today..+1 year. | `PasController.cs:353-360`; `PasAutoAdjudicator.cs:357-366` | Map from the bundle's Patient, Coverage and Claim, and use the requested period. | **Fixed** (`d0681cee`, PR #1300). Member id (Patient identifier typed MB, else `Coverage.subscriberId`), name and DOB from the Patient; LOB from `Coverage.type` (X12 1032 or SOPT), otherwise `Unknown`, never Commercial; NPIs from the referenced Practitioner/Organization; `ServiceTypeCode` from `item.category` (X12 1365) or null, never the claim type; requested dates from `item.serviced[x]` (else `billablePeriod`); codes keep their system. Approved period = requested period; start + 1 year only for an open-ended period (the IG sets none; documented in `PasAuthorizationMapper`). Missing required data → 422. `PasAuthorizationMapperTests`. Review follow-ups (PR #1300): the decision is made on the same mapping (B1): `PasController` maps once and passes the LOB and requesting/servicing NPIs to the adjudicator, which no longer defaults LOB to Medicaid or state to TX; an `Unknown` LOB is pended (A4, reason stated) without adjudication; with no `item.locationAddress.state` the state rules do not run (pend `StateUndetermined`); the history `Source` states only the inputs the deciding path read (`automated:<rule>;lob=<lob|n/a>;state=<state|none>`: the state only when the enrollment gate ran or the state rule engine was reached; `lob=n/a` for provider exclusion, disabled adjudication, an exceeded time budget, an undetermined LOB and the NoOp rule engine, each of which authorization-service accepts only with its own outcome; the four no-evaluation pends are refused if they carry any LOB or state, and the NoOp engine is recognised by its rule id) and authorization-service refuses a stated decision LOB that is not the record's, and a missing one for any other rule (seam tests: Commercial `CI` is not sent to the Medicaid enrollment gate; Unknown is pended). The adjudicator decides on the mapped record: requested service date (it used to ignore a `servicedDate` and ask the enrollment gate about today), procedure codes with their system, member id, DOB, diagnoses and place of service come from `PasDecisionBasis`, not the Claim. Mapping: level of service per the owner's rule (Q-14, see D-08); ICD-10-CM (else ICD-10) diagnoses only; `subscriberId` used as member id only when the patient is the subscriber; SOPT by OID too; with mixed open and closed periods the approval ends at the later of the closed end and start + 1 year. **Remaining:** the adjudicator still reads from the Claim itself, not the record: provider taxonomy (`careTeam[0].qualification`), estimated cost (`Claim.total` or item `unitPrice` × quantity), program (`Coverage.identifier.type.text`) and the service state (`item[0].locationAddress.state`, with no Coverage- or plan-based state). The stored record holds no state field (only the history `Source` does). **Operational changes:** a request whose `Coverage.type` is absent or unrecognised now has LOB `Unknown` and is always pended, so those submitters get no automated approvals; a request naming no service state pends instead of running TX rules; and the mapper never produces `Exchange`, so Marketplace plans (X12 1032 / SOPT codes for them are not mapped) are decided as Commercial or Unknown and the Marketplace enrollment gate never runs (G-63). |
| D-04 | Critical | Claims adjudication **fails open**: a null auth validation (404, error or exception) is treated as valid. Pinned by test `Execute_InpatientWithPriorAuthLookupDegraded_ContinuesToBenefitEngine`. | `claims-service/.../BenefitCalculationStage.cs:465`; `HttpAuthorizationValidationClient.cs:69-97` | 404 → deny or pend. Transport error → pend (retry), never pay. *Changes claim outcomes; doesn't touch posted payments.* | Fixed in `f491ba34`, `83462697`, PR #1299 (Q-9); review fixes `5dca9095`, `a138d13a`, `1145e074`. Typed lookup result (Valid / Invalid / NotFound / Unavailable). The service's own 404 body (pinned on both sides) → deny CARC 197; anything not definitive → pend `PAUNAVAIL` before the benefit engine (no accumulator commit prepared), a transient reason an approval never overrides. The lookup always carries claims-service's service token, so examiner approval re-runs work (they forwarded the examiner's token: 403). Bulk re-run for `PAUNAVAIL`/`PLANUNAVAIL` pends. The pinning test is replaced by `Execute_InpatientWithPriorAuthLookupUnavailable_PendsWithoutEngineCall`; golden-path accumulator tests on Mongo and Redis (Cosmos skipped: no emulator in the run). Related open gap for coverage-service calls: D-36. |
| D-05 | High | The 837 parser never reads REF*G1, so EDI inpatient Medicaid claims are always denied with CARC 197. | `claims-service/EDI/Inbound/X12837Parser.cs:196-226`; `X12837ClaimMapper.cs:105` | Parse REF*G1 at claim and line level. | Fixed in `4850f3be`, PR #1299. 2300 REF*G1 parsed and mapped; 2400 REF*G1 (an 837P segment) parsed and mapped too; 2320/2330 REF*G1 ignored; line overrides claim. **Correction:** on `main` EDI claims were not "always denied": the mapper sets LOB Commercial and an 837I's CLM05-1 facility type lands in `PlaceOfServiceCode`, so the Medicaid + POS 21 rule never matched an EDI claim; the number was simply lost. Aligning the EDI inpatient definition: G-62. |
| D-39 | Medium (latent) | No unique constraint on (tenantId, authorizationNumber). The automated-decision path checks for an existing number and then inserts (check-then-act), so two concurrent records of the same number in the same tenant can both pass the check and both be stored; `$inquire` then reads whichever the store returns first. Today numbers are random (`PAS-yyyyMMdd-` + 8 hex) and the realistic case, a retry after a lost answer, is covered by the request-hash idempotency (PR #1300). Found in review of PR #1300. | `AS/Controllers/AutomatedDecisionsController.cs` (`Record`: `GetByNumberAsync` then `RecordAutomatedDecisionAsync`); `AS/Repositories/AuthorizationRepositoryMongo.cs`, `AuthorizationRepository.cs` (no index / unique key) | Mongo: unique index on `{ TenantId: 1, AuthorizationNumber: 1 }`. Cosmos: a unique key policy on `/authorizationNumber` within the `/tenantId` partition; it can only be set when the container is created, so the container is recreated and the data copied. A duplicate-key error on insert is handled like the idempotent resend: the same payload (request hash) returns the existing record, a different payload is refused (409). A concurrent-insert test that fails first. Runbook: detect existing duplicates before building the index, and the container-recreate steps. | **Fixed.** Unique Mongo index on TenantId + AuthorizationNumber; duplicate writes become 409. A concurrent PAS retry returns the winning stored record only when request hashes match, otherwise 409. Real-Mongo concurrent-create/tenant tests and HTTP race tests cover the paths. Startup and writes ensure indexes; legacy duplicates fail initialization without data changes. See docs/operations/AUTHORIZATION-NUMBER-UNIQUENESS.md. Cosmos recreation is obsolete after ADR 017. |
| D-40 | Medium (latent) | Plan lookups (`HttpBenefitPlanResolver`, `GET /api/v1/plans/{id}`) forward the caller's token. On an examiner approval re-run, an examiner role without `benefits:read` gets 403 → `BenefitPlanUnavailableException` → with `UsePlanFlaggedCodes` on, every approval re-run of a claim outside the floor pends `PLANUNAVAIL`. The code role catalogue grants ClaimsExaminer `benefits:read`; the stored TenantRole catalogue must be checked per tenant. Latent while the flag is off (plan flags unused). | `claims-service/Services/Resolution/HttpBenefitPlanResolver.cs`; `ChoOutboundTokenHandler.cs`; stored TenantRole catalogue | Either every examiner role holds `benefits:read` in the stored catalogue, or plan lookups use the service identity (`UseChoServiceIdentity`). | **Fixed in code:** plan lookup requests the claims-service identity, so pipeline access no longer depends on the examiner role catalogue. Real handler/readjudicator tests and receiving plan API tests cover service acceptance, insufficient direct-user permissions, mismatched tenants and failures. G-64 remains a separate rollout; no flag is enabled here. |
| 2026-10-11 | Wave 2 part C (PR #1299): D-04 and D-05 fixed (G-12; G-25). A failed PA lookup pends `PAUNAVAIL` and never pays or denies (Q-9); every PA-required line is checked; REF*G1 read at 2300 and 2400; PA scope also from the plan's existing benefit flags. §2.1 claims rows, §2.2 row 11 and §3 updated; D-05's "always denied" corrected. No §4.1 / §4.4 row covers claims-side auth checks, so none changed. |
| 2026-10-11 | PR #1299 review: the PA lookup always uses claims-service's service token (examiners could not clear PA pends: B1); `PLANUNAVAIL` for transient plan outages only (a definitive 404 keeps the earlier path), single-flight plan resolver no longer passes a cancelled caller's token to waiters; plan-flagged PA codes behind `ClaimsAdjudication:PriorAuth:UsePlanFlaggedCodes`, off until G-07; bulk re-run for `PAUNAVAIL`/`PLANUNAVAIL` pends; not-found body pinned on both sides. New D-36 (coverage-service calls on approval re-runs forward the examiner's token). §2.1, §2.2 row 11, D-04 and D-05 wording corrected ("before the benefit engine"; 2400 REF*G1 is an 837P segment). |
| 2026-10-11 | Owner decisions on PR #1299 recorded in §7.1 (Q-11 floor kept, Q-12 EDI inpatient check not widened here, Q-13 `PLANUNAVAIL` kept); new open gaps G-61 and G-62 in §8. |
| 2026-10-11 | PR #1299 re-verification: a bulk re-run Deny is finalized only when it is the PA check's CARC 197 and no check failed on that run (decided from the run's structured state), and records the denying stage's reason; the bulk re-runs (`PAUNAVAIL`/`PLANUNAVAIL` and ACCUMCOMMIT) take the least recently pended first, with a fair share per pend code; `UseChoServiceIdentity` attaches no token for an authenticated caller without a resolved tenant. Owner decisions Q-15 (bulk re-run denial) and Q-16 (flag dependency) in §7.1; new D-40 and G-64; D-36 marked as blocking the flag. |
| 2026-10-11 | PR #1299 second re-verification: the bulk re-run no longer finalizes a Deny produced by a failed check (benefit engine 503/429 → code 999; NCCI in Deny mode with its engine throwing; PA NotFound while pricing was unavailable) — only the PA branch's CARC 197 on a run where every check answered; pipeline-level tests. First adjudication still denies (999) when the benefit engine answers non-2xx: unchanged here, for the owner. Bulk re-run batches skip claims with other pend reasons, and a run that makes no progress re-stamps the claim to the back of the queue. The Cosmos contradiction repair is decided on the live row (ETag) instead of a patch filter the emulator could not evaluate. |
| 2026-10-11 | PAS persistence (PR #1300, G-01/G-03): D-01, D-02, D-03, D-08 fixed. New internal, fhir-service-only automated-decision path in authorization-service; the public create still clears outcomes. `$submit` maps the record from the Bundle, refuses incomplete requests (422), stores before answering and fails (503/502, no `preAuthRef`) if it cannot. Model additions on the existing Authorization document: `PatientReference`, `StatusHistory[].Source`; `ServiceTypeCode` nullable; `ProductOrServiceSystem` 256 and `DenialReasonCode` 50 chars. §2.2 rows 2/7/10, §4.1, §4.4 and §4.7 rows updated. §11: PAS-03 GAP → PARTIAL; PAS-04/05/06/08 justifications updated, no other status changed. Review follow-ups in the same PR: the decision is made on the record's mapped LOB and NPIs and its basis is stored (B1; Unknown LOB pends; no TX default); the response is built from the stored record; idempotent recording with a compensating read; mapping refinements (level of service by system and PAS extension, ICD-10 diagnoses, subscriber id, SOPT OID, mixed periods); `$inquire` caller from JWT claim names only (D-32 follow-up); `[BsonIgnoreExtraElements]`. D-01, D-02, D-03, D-08 and D-26 rows and §2.2 rows 3–5 updated; new D-39 (no unique number constraint; fix approved by the owner 2026-10-11, to ship as its own PR after #1300). Re-verification follow-ups: the history `Source` claims only the inputs read (`lob=n/a`, `state=none` otherwise); the adjudicator decides on the mapped service date, codes, member, DOB, diagnoses and place of service; urgency per owner decision Q-14 (§7.1); D-03 lists the remaining Claim reads and the operational changes; D-02 rollback note scoped to Mongo; new G-63 (Exchange / Marketplace LOB). Last follow-ups: ActPriority, request-priority and emergency/emergent/immediate urgency; the no-evaluation rules are bound to their outcome (provider exclusion only Denied; ConfigDisabled, TimeBudgetExceeded, LineOfBusinessUndetermined and the NoOp rule engine only Pended); the NoOp engine claims no basis; D-08 notes that a stored `E` can mean "not recognised". Final re-check: the ActPriority OID is ActPriority; `UR` and `EM` are urgent in any system and case; the no-evaluation pends may carry no LOB or state (400); NoOp recognised by `FiringRuleId`; seam test for the NoOp pend end to end. §11 unchanged. |
