# Electronic Prior Authorization (ePA) — Gap Matrix

**Status:** Phase 2 deliverable — audit and gap matrix only. No code has been changed.
**Baseline:** `main` @ `31a462ff`, audited 2026-10-10.
**Owner review required before Phase 3.**

This document is the source of truth for CHO's ePA conformance. If any other
document, page, comment or test manifest disagrees with it, this document wins
until it is updated.

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
| **Da Vinci PAS** | **Not conformant** | `$submit` adjudicates and answers, but **never creates a durable record** (D-01, D-02). `$inquire` logic is solid but can't find PAS-issued numbers. No Subscriptions. No `reviewAction` at item level. No profile validation. |
| **Da Vinci CRD** | **Not conformant** | Discovery plus 2 of the hooks (order-select, order-sign), returning free-text cards. No coverage-information extension or systemActions. No CDS Hooks JWT. **Fails open:** an unknown code gets "no PA needed" (D-21). |
| **Da Vinci DTR** | **Not conformant** | Questionnaire/QR CRUD (largely in memory). `$questionnaire-package` returns the Questionnaire only. No CQL, prepopulation, adaptive forms, DTR launch context, or QR→PAS link. |
| **CMS-0057-F timeframes** | **Not met** | The SLA watchdog throws on every run (D-07). PAS urgency is dropped (D-08). No persisted due date or extension tracking. The RFI clock restarts in full (D-09). |
| **CMS-0057-F denial reasons** | **Partial** | Stored when supplied via REST, but not required (D-10), lost for PAS (D-02), not in PAS `reviewAction`, no 278 HCR, no notices. |
| **CMS-0057-F public metrics** | **Missing** | Only a counts/averages summary endpoint. No CMS metric set, median, standard/expedited split, appeal or extension rates, or annual extract. |
| **Patient / Provider / Payer-to-Payer PA data** | **Missing (PA) / Stub (data)** | No API exposes PA data. Patient, Coverage and Claim reads come from a **mock adapter registered in every environment** that ignores tenant (G-30). Bulk `$export` is a stub. Provider Access is opt-in (D-11). |
| **SMART on FHIR / OAuth2** | **Partial, solid core** | Auth code + PKCE, refresh, per-resource scope enforcement and tenant binding are I+T. Missing: launch context in the token response, SMART v2 scopes, `private_key_jwt`, `aud` validation, revocation. |
| **CMS-0062-P readiness** | **Not ready (design only)** | No benefit-channel field, days supply or refills. PAS drops the drug code system. |
| **X12 278 bridging** | **Stub / Missing** | No .NET 278 parser or encoder. An Argo/Python tokenizer feeds a Kafka topic that nothing consumes. |
| **Tenant isolation** | **Mostly sound, 5 gaps** | Token-derived tenant and repository filtering are strong. Gaps: PA rule set chosen from the request body (D-12), DTR search leak (D-14), P2P export authorization (D-13), mock adapters ignoring tenant (G-30), rule-repository writes without tenant (D-16). |
| **Published evidence and copy** | **Overclaims** | The public CMS-0057-F evidence shows 21/21 PASSABLE. Statuses are hand-declared and several backing tests are tautological. Inferno and "compliant" claims have no evidence behind them. See §6. |

---

## 2. Phase 1 — Inventory

### 2.1 Components

| Component | Path | Class | Note |
|---|---|---|---|
| PAS `Claim/$submit` | `FS/Controllers/PasController.cs:56-170` | P | Validation, provider exclusion and auto-adjudication work. Persistence is broken (D-01/D-02). |
| PAS `Claim/$inquire` | `FS/Controllers/PasController.cs:197-250`, `FS/Services/PriorAuthorizationInquiryService.cs` | I+T (logic) / P (end to end) | ~20 tests, tenant-checked. Can't find PAS-submitted auths. |
| PAS response builder | `FS/Services/PasResponseBuilder.cs` | P | Root-level `reviewAction`, STU1 URL, pended responses only. No items on `$submit`. |
| PAS auto-adjudicator | `FS/Services/PasAutoAdjudicator.cs` | P | Real pipeline, but the rule tenant comes from the body (D-12), gold card has the wrong scope (D-19), and there's a $500 auto-approve (§7). |
| Compliance checker | `FS/Services/Cms0057ComplianceChecker.cs` | P | Structural presence checks only; not IG validation. PAS `Claim` timeline returns `Applicable:false` (:600-611). |
| Authorization (UM) record + API | `AS/Models/Authorization.cs`, `AS/Controllers/AuthorizationsController.cs` | I+T | 278-shaped header model with status history. Header-level decisions only. |
| Mongo repository (default) | `AS/Repositories/AuthorizationRepositoryMongo.cs` | I-U | Tenant filter on every query; no repository tests. |
| Cosmos repository (opt-in) | `AS/Repositories/AuthorizationRepository.cs` | I-U, defect | Enums stored as ints but queried as strings (D-18). |
| ChoAuthorizationBackend | `AS/Backends/ChoAuthorizationBackend.cs` | I+T | Status and decision persistence. |
| QNXT backend | `AS/Backends/QnxtAuthorizationBackend.cs` | S | Every method throws `NotImplementedException` (documented). |
| SLA watchdog | `AS/Services/SlaWatchdogService.cs` | S (non-functional) | D-07. |
| `GET sla/at-risk` | `AS/Controllers/AuthorizationsController.cs:634-652` | I+T (mocked repo) | Computes the deadline on read; hours-based. |
| RFAI / CDex additional info | `rfai-service`, `AS/Services/Rfai/*`, `AS/Consumers/RfaiDocsReceivedConsumer.cs` | I+T | 42 tests. Raised only from reviewer decisions, never from PAS pends. |
| Attachments | `attachment-service` | I-U (auth linkage) | Tenant-scoped; nothing writes back to the authorization. |
| Prior-auth rule engine | `src/engines/CloudHealthOffice.PriorAuthRuleEngine` | I+T (engine) / P (content) | Real, data-driven. Content is 10–11 TX Medicaid seed rules. History services are interface-only (S). |
| Rule admin | `benefit-plan-service/Controllers/PriorAuthRulesController.cs` | P | Seed endpoint only; no CRUD. |
| Drug/benefit exclusion | `AS/Services/BenefitExclusion/*` | I+T | Excludes drugs from CMS-0057-F medical PA scope. |
| PA retention | `FS/Services/Retention/PriorAuthorizationRetention*` | I+T | Off by default. |
| CRD (CDS Hooks) | `FS/Controllers/CrdController.cs`, `FS/Services/CrdService.cs` | P | See §4.2. |
| CRD classification store | `FS/Services/CrdClassificationStore.cs` | S (durability) | `IMemoryCache`: expires, per pod, lost on restart. |
| DTR | `FS/Controllers/DtrController.cs`, `FS/Services/DtrService.cs` | P | See §4.3. |
| Claims → auth check | `claims-service/.../BenefitCalculationStage.cs:191-204,433-480` | P, defect | Inpatient Medicaid only; **fails open** (D-04). |
| 837 REF*G1 capture | `claims-service/EDI/Inbound/X12837Parser.cs:196-226` | M | D-05. |
| X12 278 inbound | `infrastructure/argo-workflows/x12-278-ingest.yaml`, `containers/x12-parser/parse_x12.py` | S | Generic tokenizer; Kafka `edi-278` has no consumer. |
| X12 278 outbound | `containers/x12-encoder` (277 only) | M | — |
| PAS ↔ 278 mapping (TS) | `src/fhir/prior-auth-api.ts` | S (orphaned) | JSON-object mapper; nothing imports it; clearinghouse functions are self-declared placeholders (:843-901). |
| FHIR Patient/Coverage/Encounter/Claim data | `FS/Services/MockFhirDataAdapter.cs`, `FS/Program.cs:144-151` | S | Mock registered unconditionally; ignores tenant. |
| Patient Access data | `FS/Services/IPatientAccessDataProvider.cs` (Mock) | S | 5 hard-coded members, 3 payments. |
| Provider Access authorization | `FS/Services/ProviderAccess/*` | P, defect | Opt-in instead of opt-out (D-11); static attribution. |
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
| 2 | Bundle validation | `PasController.cs:445-498` | ⚠️ Works. No PAS profile validation; `Claim.use` and `Bundle.type` not checked. Only the first Claim is used; Patient and Coverage entries are ignored. |
| 3 | Compliance gate | `PasController.cs:80-86` → `Cms0057ComplianceChecker.ValidateClaim:513` | ⚠️ Presence checks only. |
| 4 | Provider verification | `PasController.cs:89-123` | ⚠️ Fails open when the service is down (`:554-561`). |
| 5 | Adjudication | `PasAutoAdjudicator.TryDecideAsync:49` | ⚠️ Order: enrollment gate → deny list → allow list → gold card (wrong scope, D-19) → **< $500 auto-approve** → rules engine. The rule-set **tenant comes from `Bundle.meta.tag` in the request body** (D-12). Defaults to TX / Medicaid. With no database configured, the NoOp engine always pends. |
| 6 | Response | `PasResponseBuilder` | ⚠️ Provider gets an approved, denied or pended ClaimResponse plus a `preAuthRef`. No item-level decisions; `reviewAction` at the root, on pends only. |
| 7 | **Persist** | `PasController.PersistAuthorizationAsync:344-381` → `POST api/authorizations` | ❌ **Breaks.** The payload fails model validation: `ServiceTypeCode` = "professional" (max length 2) and an empty `RequestingProviderNPI` (verified by probe). The 400 is ignored and exceptions are swallowed. Even a valid payload has its decision wiped by `ClearReviewOutcome` (D-02). Placeholder demographics are posted (D-03). |
| 8 | Pend notification | — | ❌ Missing. No Subscriptions, events or notices. |
| 9 | Reviewer decision | `POST api/authorizations/{id}/response` | ❌ No record to decide. No work queue or reviewer UI. |
| 10 | Status | `POST Claim/$inquire` → `GET api/authorizations/number/{n}` | ❌ 404 for PAS-issued numbers. |
| 11 | Claim against the auth | 837: `X12837Parser` (REF*G1 not read). REST: `/validate` | ❌ EDI inpatient Medicaid claims are **always denied (CARC 197)** (D-05). For REST claims, a missing auth gives `/validate` 404 → null → **fails open, and the claim proceeds** (D-04). Outside inpatient Medicaid, no auth check runs at all. |

**Net:** no hop after step 6 is connected for a real PAS request. The provider is
told one thing; the system of record holds nothing (or "Submitted"); claims
either deny or pay regardless.

---

## 3. Defect log

Found during the Phase 1 audit. Status is **Open** for all. Each fix in Phase 3
will reference its D-number and update this table.

| ID | Sev | Defect | Evidence | Fix sketch | Status |
|---|---|---|---|---|---|
| D-01 | Critical | PAS `$submit` never persists. The payload fails validation, the 400 is ignored, and the exception is swallowed. A `preAuthRef` is issued for a record that does not exist. | `FS/Controllers/PasController.cs:344-381`; `AS/Models/Authorization.cs:80-82,135-137`; confirmed by probe | Map fields correctly. Fail `$submit` (OperationOutcome) if the record isn't stored. Add an integration test across the seam. | Open |
| D-02 | Critical | `POST api/authorizations` forces `Submitted` and runs `ClearReviewOutcome`, wiping automated PAS decisions and reasons. (The clearing is a deliberate security control for external REST callers and is pinned by `Submit_IgnoresBodyTenantActorAndReviewOutcome`.) | `AS/Controllers/AuthorizationsController.cs:132-145,162-192` | Keep the control. Add a separate internal, service-token-only "record automated decision" path that writes status history attributed to the adjudicator. | Open |
| D-03 | High | PAS persists placeholder data ("PAS"/"Patient", DOB = now−30y, LOB Commercial, service date = now). The approved period is hard-coded to today..+1 year. | `PasController.cs:353-360`; `PasAutoAdjudicator.cs:357-366` | Map from the bundle's Patient, Coverage and Claim, and use the requested period. | Open |
| D-04 | Critical | Claims adjudication **fails open**: a null auth validation (404, error or exception) is treated as valid. Pinned by test `Execute_InpatientWithPriorAuthLookupDegraded_ContinuesToBenefitEngine`. | `claims-service/.../BenefitCalculationStage.cs:465`; `HttpAuthorizationValidationClient.cs:69-97` | 404 → deny or pend. Transport error → pend (retry), never pay. *Changes claim outcomes; doesn't touch posted payments.* | Open |
| D-05 | High | The 837 parser never reads REF*G1, so EDI inpatient Medicaid claims are always denied with CARC 197. | `claims-service/EDI/Inbound/X12837Parser.cs:196-226`; `X12837ClaimMapper.cs:105` | Parse REF*G1 at claim and line level. | Open |
| D-06 | High | Line-level `ServiceStatus` is never written by any API, so `/validate` with a procedure code always fails for API-decided auths. | `AS/Models/Authorization.cs:484`; `AuthorizationsController.cs:275-276` | Line-level decision model and API (also needed for PAS item adjudication and A2 partial approvals). | Open |
| D-07 | Critical | The SLA watchdog throws on every run. A hosted service has no `HttpContext` tenant, so no authorization is ever escalated. Tests mock the repository. | `AS/Services/SlaWatchdogService.cs:58`; `AuthorizationRepositoryMongo.cs:26-34`; confirmed by probe | Iterate tenants explicitly, as the retention worker does. Test against a real repository. | Open |
| D-08 | Critical | PAS `Claim.priority` isn't mapped to `LevelOfService`, so every PAS request runs on the 7-day clock. | `PasController.cs:348-373`; `SlaWatchdogService.cs:67` | Map `priority` (stat/urgent → expedited). Set the receipt timestamp on arrival, not after adjudication. | Open |
| D-09 | High | When RFI documents arrive, the decision clock restarts with a full new window. CMS-0057-F has no RFI tolling or restart. | `AS/Consumers/RfaiDocsReceivedConsumer.cs:168-195` | Keep the clock from receipt. Model extensions explicitly, where the program permits them. | Open |
| D-10 | High | A denial (A3/Denied) is accepted with no reason. The rule engine falls back to the rule name as the reason. | `AuthorizationsController.cs:445-469,535-539`; `PaRuleBase.cs:88`; test `UMCoordinator_CanDecide` | Require a coded and human-readable reason for every denial and partial denial. | Open |
| D-11 | High | Provider Access requires an active opt-in consent. CMS-0057-F requires sharing unless the patient **opts out**. | `FS/Services/ProviderAccess/ProviderAccessAuthorizationService.cs:129-140` | Invert to opt-out, keyed on attribution. | Open |
| D-12 | High (tenant) | The PA rule set's tenant is taken from `Bundle.meta.tag "cho:tenant"` in the request body, not the authenticated tenant. | `FS/Services/PasAutoAdjudicator.cs:161,338-340` | Use the authenticated tenant only. | Open |
| D-13 | Critical (security) | `PayerToPayer/$member-data-export` and `Patient/$member-match` accept patient-context tokens. The target member comes from the POST body and is never compared to the token's patient. `receivingPayerId` is self-declared. One member could export another opted-in member's data in the same tenant. Found by code reading; there is no HTTP-level test yet. | `FS/Controllers/PayerToPayerController.cs:28,52-56`; `SmartScopeEnforcementMiddleware.cs:307-308,350` | Restrict to backend (system) clients. Bind the receiving payer to the authenticated client. Add an HTTP-level negative test. | Open |
| D-14 | High (tenant) | DTR Questionnaire search iterates every tenant's questionnaires. Where ids collide, tenant A can see tenant B's copy. | `FS/Services/DtrService.cs:98-99,304-309` | Filter by tenant, plus the read-only default library. | Open |
| D-15 | Medium | SMART tokens (provider apps) can write payer Questionnaires. | `FS/Controllers/DtrController.cs:65,93`; `SmartScopeEnforcementMiddleware.cs:343-358` | Questionnaire writes are CHO-admin only. | Open |
| D-16 | Medium (tenant, latent) | PA rule-repository Upsert/BulkUpsert/Delete filter on RuleId + StateCode with no tenant. | `PaRuleRepository.cs:140-163,242-279` | Include the tenant in write filters. | Open |
| D-17 | Medium | Cosmos `PaRuleRepository.ListAsync` builds a parameterised query, then runs a parameterless one. | `PaRuleRepository.cs:166-179` | Use the built query. Run a Cosmos emulator test. | Open |
| D-18 | Medium | Cosmos `AuthorizationRepository` stores status/LOB enums as ints but queries them as strings, so search, summary, SLA and retention would return nothing on Cosmos. (Mongo is the default.) | `AS/Repositories/AuthorizationRepository.cs:169,216-219,362,442`; `AS/Program.cs:91-95` | String enum converter. Cosmos emulator test. | Open |
| D-19 | High | Gold-card lookup calls `summary?providerNPI=`, but the summary ignores `providerNPI`. A tenant-wide approval rate gold-cards every provider. | `PasAutoAdjudicator.cs:211-227`; `AuthorizationsController.cs:615-629` | Provider-scoped history (implement `IProviderApprovalHistoryService`). | Open |
| D-20 | Medium | Rule-engine history services (`IProviderApprovalHistoryService`, `IMemberAuthHistoryService`) are never implemented or registered. Gold-card exemption never fires, and quantity-limit rules always pend. | `PriorAuthRuleEngine/Abstractions/IPriorAuthRuleEngine.cs:83-103` | Implement them over authorization-service, with tenant in the signature. | Open |
| D-21 | High | CRD **fails open**: an unknown code returns "No prior authorization needed". The engine is called with hard-coded TX / Medicaid / STAR, no diagnoses, and cost 0. SNOMED codes are translated to ICD-10-CM and evaluated as procedures. | `FS/Services/CrdService.cs:90-93,106-140,226-242` | Unknown → "info-needed / contact payer". Derive context from eligibility/coverage. Fix the code-type mapping. | Open |
| D-22 | Medium | CRD and DTR stores aren't durable. Classification lives in an expiring `IMemoryCache`. DTR Mongo updates aren't written, and search reads memory only. | `CrdClassificationStore.cs`; `DtrService.cs:95-121,153-176,198-202` | Persist in the existing tenant store. *Persistence change — owner decision (§7).* | Open |
| D-23 | Medium | No authorization state machine: any status can move to any status (Denied→Approved, Cancelled→Approved). Cancel skips history. `Expired` is never set. | `AuthorizationsController.cs:431-480,657-677` | Explicit transitions, with history on every change and an expiry job. | Open |
| D-24 | Medium | The portal PA submit sends no requested services (400) and reads a response field that doesn't exist. | `src/portal/.../ServiceImplementations.cs:1005-1023` | Fix the contract. | Open |
| D-25 | Medium | `.well-known/smart-configuration` advertises `private_key_jwt` (unsupported) and omits `refresh_token`. Code comments say "v2". | `FS/Controllers/SmartConfigurationController.cs:8,83,96`; `SA/Models/SmartScopes.cs:4` | Advertise only what works (or implement it, G-41). | Open |
| D-26 | Medium | PAS and SMART don't bind the caller: `$inquire` takes its corroborating member/NPI from the body, and `$submit` doesn't compare `Claim.provider` to the token's NPI. (CDex does.) | `PriorAuthorizationInquiryService.cs:144-156,224-251` | Bind to token claims. | Open |
| D-27 | Low | Summary approval rate counts pending requests in its denominator. The Cosmos average is truncated to whole days. The Mongo version doesn't compute turnaround. | `AuthorizationRepository.cs:261-321`; `AuthorizationRepositoryMongo.cs:145-149` | Replaced by the metrics work (G-20). | Open |
| D-28 | Medium | Bulk `$export` reports Complete with count 0 and output URLs that no route serves, which a client would read as success. | `FS/Services/BulkExportService.cs:24-70` | Return 501/OperationOutcome until real (G-34). | Open |
| D-29 | Medium | The Provider Directory requires authentication; CMS requires it to be public. | `FS/Controllers/ProviderDirectoryController.cs:27` | Public read endpoint. | Open |
| D-30 | High (honesty) | The CMS-0057-F evidence pipeline publishes hand-declared PASSABLE statuses backed by tautological tests (METRICS-01, PAS-06, PROV-0x, PAT-03, CONSENT-01). | `tests/Cms0057Acceptance.Tests/scenarios.json`; `tools/Cms0057Evidence/EvidenceBuilder.cs:117-125` | **Owner decision (§6.1).** The output is public. | Partly fixed (#1292): statuses come from §11; the tautological tests themselves remain; publish paused (#1291). |

---

## 4. Gap matrix — requirement by requirement

The **Gap** column points to the register in §8, which holds severity, effort,
dependencies and order.

### 4.1 Da Vinci PAS (STU 2.x)

| Requirement | Class | Evidence | Gap |
|---|---|---|---|
| `Claim/$submit` operation | P | `PasController.cs:56-170`. Tests mock the adjudicator and stub persistence. | G-01, G-06 |
| Request Bundle validation against PAS profiles | M | Presence checks only (`Cms0057ComplianceChecker.cs:513-600`). No FHIR validator. | G-14 |
| `Claim.use = preauthorization` enforced on submit | M | Checked only on `$inquire` (`:314`). | G-13 |
| Supporting resources (Patient, Coverage, Practitioner, Organization) read from the Bundle | M | Ignored; placeholder data instead (D-03). | G-01 |
| Durable record of each request and decision | S | D-01, D-02. | G-01 |
| `ClaimResponse` PAS profile (bundle contents, identifiers) | P | Response Bundle holds only the ClaimResponse (`PasResponseBuilder.cs:317-333`). | G-13 |
| `reviewAction` / `reviewActionCode` at `item.adjudication` | P | At the root, STU1 URL, pends only (`PasResponseBuilder.cs:14-18,80-95,176-188`). | G-13 |
| Item-level decisions (A1 / A2 / A3 / A4 per line) | M (`$submit`) / P (`$inquire`) | `PasResponseBuilder.cs:224-258`; no line decision writer (D-06). | G-07, G-13 |
| Denial reason in `reviewAction.reasonCode` (X12 HCR03) | P | Placed in `ClaimResponse.error` (`:54-62,191-201`). | G-17 |
| `preAuthRef` / `preAuthPeriod` | I+T (emitted) | Period hard-coded to 1 year (D-03). The ref points at a missing record (D-01). | G-01 |
| Pended response (A4 / queued) | I+T | `PAS07_PendedResponse_SignalsOutstandingReviewViaX12A4`. | — |
| `Claim/$inquire` | I+T (logic) / P (end to end) | `PasInquiryTests` (~20). Can't find PAS-issued numbers (D-01). | G-01 |
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
| Safe default for unknown codes | ❌ defect | Fails open (D-21). | G-05 |
| CHO tested as a CRD **server** against RI / Inferno | M | Discovery shape only (`ChoCrdSurface.cs`). | G-60 |

### 4.3 Da Vinci DTR (STU 2.x)

| Requirement | Class | Evidence | Gap |
|---|---|---|---|
| Questionnaire storage and serving | P | Singleton in-memory dictionary. Mongo optional, and its updates aren't persisted (D-22). | G-53 |
| SDC / DTR Questionnaire profiles (canonical url, version, cqf-library, launchContext, variables) | S | 5 hand-written seeds that only claim the profile (`DtrService.cs:332-751`). | G-55 |
| `$questionnaire-package` (inputs: coverage + order; output: Parameters with PackageBundle containing Questionnaire, Library, ValueSet) | P | Takes an id; returns a collection Bundle with the Questionnaire only; undeclared. | G-54 |
| CQL Library resources | M | — | G-55 |
| Prepopulation (`initialExpression`, `cqf-expression`) | M | — | G-55 |
| Adaptive forms (`$next-question`) | M | — | G-57 |
| CQL engine (for a CHO-hosted DTR app, if built) | M | — | G-56 (decision) |
| QuestionnaireResponse storage | P | Search reads memory only. Answers not validated. Canonical refs rejected. | G-53 |
| QuestionnaireResponse linked to the PAS request | M | — | G-16 |
| DTR SMART app launch (`fhirContext`, `appContext`) | M | `LaunchContext` has patient/encounter only. | G-40, G-56 |
| Native EHR DTR support | P | Raw forms only. | G-54, G-55 |
| CHO tested as a DTR payer server against RI / Inferno | M | — | G-60 |

### 4.4 CMS-0057-F operational requirements

| Requirement | Class | Evidence | Gap |
|---|---|---|---|
| Urgency (expedited vs standard) captured | P | REST `LevelOfService` only; PAS drops it (D-08). | G-03 |
| Clock starts at receipt | P | `SubmittedDate` = REST insert time. For PAS that is after adjudication (D-08). | G-03 |
| Due date computed **and persisted** (72 h expedited / 7 calendar days standard) | P | Computed on read, hours-based, not persisted. Appeals already has a richer deadline model to reuse. | G-18 |
| Timers / escalation | S | Non-functional (D-07). | G-04 |
| Alerts to the UM team | M | Log line only. | G-19 |
| RFI does not restart the clock | ❌ defect | Restarts in full (D-09). | G-18 |
| Extension tracking (where permitted) | M | No field. | G-18 |
| Late-decision reporting | M | — | G-20 |
| Specific reason for every denial | P | Not required (D-10). Lost for PAS (D-02). | G-08 |
| Partial denials carry line-level reasons | S | Fields exist; nothing writes them (D-06). | G-07 |
| Denial reason in PAS ClaimResponse | P | In `error`, with ad-hoc codes in the HL7 code system. | G-17 |
| Denial reason in X12 278 HCR | M | — | G-50 |
| Provider and member denial notices | S | Hard-coded portal template; no backend. | G-21 |
| **Public metrics:** list of items requiring PA (excluding drugs) | M | No public artifact. | G-20 |
| **Public metrics:** % standard approved / denied / approved after appeal | M | Appeals keyed by claim, not authorization. | G-20 |
| **Public metrics:** % timeframe extended then approved | M | No extension field. | G-18, G-20 |
| **Public metrics:** % expedited approved / denied | M | No split. | G-20 |
| **Public metrics:** average **and median** time to decision, standard and expedited | M (median) / P (average) | Average only, truncated, unsplit (D-27). | G-20 |
| Annual extract for website publication | M | Template doc only. | G-20 |
| Drugs excluded from CMS-0057-F PA scope | I+T | `DrugExclusionEvaluator`. | — |

### 4.5 PA data through the CMS-0057-F APIs

| Requirement | Class | Evidence | Gap |
|---|---|---|---|
| Real, tenant-scoped data source behind FHIR reads (Patient, Coverage, Claim, EOB) | S | Mock adapter registered unconditionally, ignores tenant (`FS/Program.cs:144-151`). | G-30 |
| **Patient Access:** PA requests and decisions (status, dates, items, quantity, denial reason; excluding drugs), within 1 business day | M | No PA resource; no PDex prior-auth profile. PAT-03 rests on a type-list assertion. | G-31 |
| PA retention (life of the auth + 1 year) | I+T | Retention worker, off by default. | — |
| **Provider Access:** attribution | P | Static config panels. | G-33 |
| **Provider Access:** patient opt-out | ❌ defect | Opt-in (D-11). | G-09 |
| **Provider Access:** PA data | M | — | G-33 |
| **Provider Access:** bulk `$export` (Group) | S | Stub (D-28). | G-34 |
| **Payer-to-Payer:** opt-in consent | I+T | 17 tests; tenant-scoped consent-service. | — |
| **Payer-to-Payer:** `$member-match` | P | Logic I+T; mock data; single tenant; undeclared. | G-35 |
| **Payer-to-Payer:** PA data (active, pending, recently changed) | M | Export contains Patient, Coverage and EOB only. | G-35 |
| **Payer-to-Payer:** 5-year claims/encounter history via bulk | P | Mock payments; not bulk. | G-34, G-35 |
| **Payer-to-Payer:** caller authorization | ❌ defect | D-13. | G-02 |
| **Prior Authorization API:** list of covered items requiring PA and documentation requirements | P | CRD rules exist; no published list (D-21). | G-20, G-44 |
| CapabilityStatement completeness | P | Missing `$member-match`, Group `$export`, `$questionnaire-package`, supportedProfiles. | G-36 |
| Public Provider Directory | ❌ defect | Requires auth (D-29). | G-36 |

### 4.6 SMART on FHIR / OAuth2

| Requirement | Class | Evidence | Gap |
|---|---|---|---|
| `.well-known/smart-configuration` | I+T, content partly wrong | D-25. | G-41 |
| Authorization code + PKCE (S256) | I+T | `SA/Program.cs:89-91`; `SmartAuthDriver.cs`. | — |
| Standalone launch (member app) | I+T | `SmartTenancyTests.cs:37`. | — |
| EHR launch from an **external** EHR (`iss` + `launch` handshake) | P | CHO-registered launches only. | G-40 |
| Launch context in the token response (`patient`, `encounter`, `fhirContext`, `need_patient_banner`) | M | JWT claims only. This blocks standard SMART clients and Inferno. | G-40 |
| SMART v2 scopes (`.cruds`, `.rs`, granular `?category=`) | M | v1 only; a v2 scope gets 403. | G-41 |
| Scope enforcement per resource and operation, with patient binding | I+T | 19+ tests. | — |
| Client registration | I+T (admin API) | Secrets only. | G-42 |
| Refresh tokens with re-validation | I+T | `SmartConsentRevocationTests`. | — |
| Backend services: `client_credentials` | I+T (shared secret) | — | — |
| Backend services: `private_key_jwt` / JWKS (required by SMART Backend Services) | M | Advertised but unsupported (D-25). | G-42 |
| `aud` validated by the authorization server | M | Always issues `fhir-api`. | G-41 |
| `aud` validated by the resource server | I-U | `SmartTokenValidation.cs:315-323`; no negative test. | G-41 |
| Token introspection | P | No client has permission. | G-41 |
| Token revocation (RFC 7009) | M | App-approval withdrawal is I+T. | G-41 |
| CDS Hooks JWT verification | M | D-21 / G-43. | G-43 |
| Multi-issuer trust (customer IdP) | I+T | `FS/Services/Identity/*`. | — |
| UDAP (optional, for payer-to-payer scale) | M | Comments only. | G-58 (deferred) |
| Payer-to-Payer outbound credentials | S | `UnconfiguredPayerToPayerCredentialProvider`, by design. | G-42 |

### 4.7 CMS-0062-P forward compatibility (design only; nothing is built)

| Capability | Today | Needed (design) |
|---|---|---|
| Medical vs pharmacy benefit | Inferred only from UM03 = "88" | Explicit `BenefitChannel` enum (Medical, Pharmacy) on the authorization and each line. Timelines and rules keyed by channel. |
| Drug identity | `ProcedureCode` plus free-text `ProductOrServiceSystem`. PAS drops the system (`PasController.cs:401-416`). | Typed coding (system URI + code). NDC normalised to 11 digits. Optional RxNorm cross-walk. PAS keeps `productOrService.coding.system`. |
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
| T-1 | Payer-to-Payer export and member-match: target member taken from the body, and patient tokens accepted. | Critical | D-13 |
| T-2 | PA rule-set tenant comes from the request body. | High | D-12 |
| T-3 | DTR Questionnaire search crosses tenants. | High | D-14 |
| T-4 | `MockFhirDataAdapter` and `MockPatientAccessDataProvider` ignore tenant and are registered in every environment. `$member-match` and P2P are served for one configured tenant only. | High | G-30 |
| T-5 | PA rule-repository writes don't include the tenant (latent). `ListAsync(tenantId: null)` returns every tenant's rules. Any tenant with `settings:manage` can run the platform seed endpoint. | Medium | D-16 |
| T-6 | Background SLA watchdog has no tenant. It fails closed, so this is a functional defect, not a leak. | — | D-07 |
| T-7 | RFAI docs-received consumer trusts the tenant in the Kafka message (trusted internal bus). | Low | note |
| T-8 | Rule-engine history-service interfaces have no tenant parameter. Fix before implementing them. | Medium | D-20 |
| T-9 | `AUDIT/investor-readiness-2026-09.md:504-507` describes a header fallback that no longer exists. Stale doc that *under*-claims; owner review. | Low | §6 |

---

## 6. Honesty findings — for owner review (nothing changed)

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

## 7. Decisions needed from the owner before Phase 3

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
| Q-3 | **Approved in principle.** A standard FHIR validator; the specific validator is named to the owner before it is added. |
| Q-4 / Q-5 | **Use existing persistence only** for subscriptions and the CRD/DTR stores. If a new persistence path seems necessary, stop and ask. |
| Q-6 | **Approved.** No CHO DTR app; serve conformant questionnaire packages for external and EHR DTR apps. |
| Q-7 | **Approved.** In-house X12 278 parser and encoder, following the 837/835 pattern. |
| Q-8 | **Approved.** The auto-approve threshold becomes per-tenant and is off by default. |
| Q-9 | **Approved.** Pend the claim when the auth lookup is unavailable. Never pay or deny on a failed lookup. |
| Q-10 | Add nullable drug fields (benefit type, days supply, refills, code system) **only if small**; otherwise design-only in this matrix. |

**Owner's sequencing:**
1. Step 0: overclaims PR, plus pausing the evidence auto-publish.
2. Owner's "wave 1": safety and isolation, covering G-02 (D-13), G-10 (D-12, D-16), G-11 (D-14, D-15) and G-05 (D-21). Each starts with a failing test.
3. Stop and report, then wait for go-ahead.

G-12, G-22 and G-23 from §8 Wave 0 follow in later waves.

---

## 8. Gap register and recommended fix order

Fix order is grouped into waves. Each wave is independently shippable; within a
wave, items are in recommended order. **Deps** lists other gaps or owner
decisions that must come first.

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
| 10 | G-04 | Watchdog iterates tenants; tested against a real repository (D-07). | Critical | S | — |
| 11 | G-07 | Line-level decisions (A1 / A2 / A3 / A4 per line, approved units, reasons) in model and API (D-06). | High | M | G-01 |
| 12 | G-08 | Denial reason required for every full and partial denial (D-10). | High | S | G-07 |
| 13 | G-24 | Authorization state machine, history on every change, expiry job (D-23). | Medium | M | G-07 |
| 14 | G-25 | 837 REF*G1 at claim and line level (D-05). Claims check auth beyond inpatient Medicaid, configurable per plan. | High | S–M | G-12 |
| 15 | G-26 | Cosmos defects: enum serialisation (D-18), rule ListAsync (D-17), with emulator tests. | Medium | S | — |
| 16 | G-27 | Bind the PAS caller to token claims (D-26). Fix the portal submit (D-24). | Medium | S | G-01 |

### Wave 2 — CMS-0057-F operational (already in force)

| Order | ID | Gap | Sev | Effort | Deps |
|---|---|---|---|---|---|
| 17 | G-18 | Persisted due date (72 h / 7 calendar days, reusing the appeals deadline policy). No RFI restart (D-09). Extension tracking. | Critical | M | G-03 |
| 18 | G-19 | Breach and at-risk alerts as events to the UM queue. | High | M | G-04, G-18 |
| 19 | G-20 | CMS metrics: every required measure, standard/expedited split, median, approved-after-appeal (needs an appeal→auth link), % extended. Annual extract. Public list of items requiring PA (D-27). | Critical | L | G-01, G-07, G-18 |
| 20 | G-17 | Denial reasons in PAS `reviewAction.reasonCode` (X12 HCR03 code set); stop putting ad-hoc codes in the HL7 adjudication-error system. | High | S–M | G-08, G-13 |
| 21 | G-21 | Provider and member decision notices (letters), using correspondence/document infrastructure. | High | L | G-08 |
| 22 | G-09 | Provider Access opt-out semantics (D-11). | High | S | — |

### Wave 3 — PAS conformance

| Order | ID | Gap | Sev | Effort | Deps |
|---|---|---|---|---|---|
| 23 | G-13 | PAS STU2 response: `reviewAction` on `item.adjudication`, item-level responses on `$submit`, a complete response Bundle, `Claim.use` check. | High | M | G-07 |
| 24 | G-14 | PAS / CRD / DTR / PDex profile validation in tests and CI (D-30 relief). | High | M | Q-3 |
| 25 | G-15 | R5-backport topic-based Subscriptions for pended → final (rest-hook). | High | L | Q-4, G-01 |
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
| 42 | G-53 | Durable Questionnaire / QR storage (D-22). Answer validation. Canonical references. | Medium | S–M | Q-5 |
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
| PAS-01 | PARTIAL | §4.2, D-21, G-05, G-44, G-46 | CRD order-select/order-sign answer with free-text cards only; no coverage-information extension; context is hard-coded TX/Medicaid/STAR and an unknown code fails open. |
| PAS-02 | PARTIAL | §4.3, D-14, D-22, G-53, G-54, G-55 | Questionnaires are served from a largely in-memory store; `$questionnaire-package` returns the Questionnaire only (no CQL Library or ValueSets); search crosses tenants. |
| PAS-03 | GAP | §2.2, §4.1, D-01, D-02, D-03, G-01 | `$submit` adjudicates and answers, but no durable authorization record is created: the hand-off fails validation and the error is swallowed. |
| PAS-04 | PARTIAL | §4.1, D-01, G-01 | `$inquire` logic is implemented and tested, but it cannot find authorization numbers issued by PAS `$submit`. |
| PAS-05 | PARTIAL | §4.4, D-02, D-10, G-08, G-17 | A denial reason is stored when supplied via REST but is not required, is lost for PAS, and is not carried in `reviewAction`. |
| PAS-06 | GAP | §4.4, §6.1, D-07, D-08, G-03, G-04, G-18 | The SLA watchdog throws on every run, PAS drops urgency, and no due date is persisted. The backing test asserts label strings only. |
| PAS-07 | PASSABLE | §2.1, §4.1, D-09, G-16, G-18 | CDex additional-information exchange is implemented and tested (I+T). Limitations: raised only from reviewer decisions, never from PAS pends; when documents arrive, the resume step these tests exercise restarts the decision clock in full (D-09). |
| PAS-08 | PASSABLE | §2.1, §4.4, D-01 | Drug and benefit exclusion from CMS-0057-F medical PA scope is implemented and tested (`DrugExclusionEvaluator`, I+T). Limitations: applies to REST-created authorizations only; PAS `$submit` never persists (D-01), so PAS drug requests are never excluded; the exclusion catalog is empty by default. |
| PROV-01 | GAP | §4.5, §5 T-4, §6.1, G-30, G-33 | Rests on S and M rows: the data source is the mock adapter registered in every environment (§4.5 "Real, tenant-scoped data source", S) and Provider Access PA data is missing (M). The backing test runs on mock data. |
| PROV-02 | PARTIAL | §4.5, §6.1, G-33 | Attribution is enforced and fails closed, but only from static configured panels. The backing test's "not attributed" case is a nonexistent member id. |
| PROV-03 | GAP | §4.5, §6.1, D-11, G-09 | Provider Access requires an opt-in consent; CMS-0057-F requires sharing unless the patient opts out. The backing test asserts `Revoked != Active`. |
| P2P-01 | GAP | §4.5, §5 T-1/T-4, D-13, G-02, G-30, G-35 | Rests on S and M rows plus an open Critical defect: the export reads the mock adapter registered in every environment (S), Payer-to-Payer PA data is missing (M), and patient-context tokens can export another member's data (D-13). |
| P2P-02 | PARTIAL | §4.5, §4.6, G-35, G-42 | Outbound initiation is built behind a transport seam; outbound payer credentials are unconfigured by design and PA data is not exchanged. |
| P2P-03 | PASSABLE | §4.5 | Opt-in consent for Payer-to-Payer is implemented and tested (17 tests, tenant-scoped consent-service). |
| P2P-04 | PARTIAL | §4.5, G-35 | `$member-match` logic is tested, but it runs over mock data for a single configured tenant and is not declared in the CapabilityStatement. |
| PAT-01 | GAP | §4.5, §5 T-4, G-30 | Rests on an S row: the claims/EOB data behind Patient Access comes from the mock adapter registered in every environment, which ignores tenant (§4.5 "Real, tenant-scoped data source", S). |
| PAT-02 | PARTIAL | §4.5, G-14, G-30 | Clinical resources are served only from Payer-to-Payer imports; no US Core profile is claimed or validated. |
| PAT-03 | GAP | §4.5, §6.1, G-31 | No API exposes PA data to patients. The backing test asserts that a supported-types list contains "ClaimResponse". |
| SEC-01 | PARTIAL | §4.6, D-25, G-40, G-41, G-42 | Auth code + PKCE, refresh, scope enforcement and tenant binding are I+T. Launch context in the token response, SMART v2 scopes, `private_key_jwt` and `aud` validation are missing. |
| CONSENT-01 | PARTIAL | §4.5, §6.1, D-11, G-09 | One consent registry serves both purposes, but Provider Access uses opt-in semantics. The backing test is reflection on constructor parameter types. |
| METRICS-01 | GAP | §4.4, §6.1, D-27, G-20 | No CMS metric set is computed: only a counts/averages summary exists, with no median, split or annual extract. The backing test checks date arithmetic. |
<!-- cms0057-evidence-status:end -->

**Totals:** 3 PASSABLE, 10 PARTIAL, 8 GAP, 0 N/A (21 scenarios). The generator
reports these itself; update this line when a row changes.
