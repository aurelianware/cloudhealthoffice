# Cloud Health Office: A Claims Director's Review of the Portal and Platform

**Reviewer perspective:** Director of Claims Operations at a 250K–750K-life plan running Medicaid managed care, CHIP and Medicare Advantage / D-SNP. Background in QNXT, Facets and HealthRules.
**Basis:** I read the code at commit `00cc64c` (2 Oct 2026). Nothing was run. I judged what the code does and set aside what the README, marketing site or architecture docs say it does.
**Status key:** **Implemented** = real logic that is saved, reachable from the UI or API, and tested or clearly on the runtime path. **Partial** = real logic with key paths missing. **Stub** = scaffold, TODO, mock data or a fixed return. **Missing** = no code found.

**Path shorthand used throughout**

| Shorthand | Full path |
|---|---|
| `portal/` | `src/portal/CloudHealthOffice.Portal/` |
| `claims/` | `src/services/claims-service/` |
| `pay/` | `src/services/payment-service/` |
| `bps/` | `src/services/benefit-plan-service/` |
| `shared/` | `src/services/shared/CloudHealthOffice.Infrastructure/` |
| `engines/` | `src/engines/` |

---

## 1. Executive summary

**Would I sign off on a pilot today? No.** I would sign off on a *shadow* pilot once a short list of money and security defects is fixed. In a shadow pilot, CHO adjudicates a copy of production claims next to the incumbent system, and no dollars or member notices go out from CHO. The platform already has a mode for running alongside an incumbent ("Augment", `engines/CloudHealthOffice.OperatingMode`), but the side-by-side comparison inside it is not built yet.

The architecture is modern and the engineering discipline is real: a staged pipeline, versioned claim events, idempotent messaging, and a candid one-million-claim benchmark. But the path that would actually run my claims has defects that would cost money on day one:

- **Claims are not priced.** The live pipeline sends no allowed amounts to the benefit engine, so the allowed amount defaults to billed charges (`claims/Services/Adjudication/Stages/BenefitCalculationStage.cs:408`; `engines/CloudHealthOffice.BenefitEngine/Services/BenefitCalculationEngine.cs:528`). The fee schedule engine is only used by a second, separate adjudication path.
- **The payment run would pay billed charges.** It reads an `ApprovedAmount` field that the claim record doesn't have, and falls back to total charges (`pay/Services/PaymentRunService.cs:419,456`).
- **Multi-unit lines are overstated.** The 837 parser stores the SV102 line total as a *per-unit* charge, and billed is later computed as charge × units (`claims/EDI/Inbound/X12837Parser.cs:323`; `claims/Models/Claim.cs:482-488`; `BenefitCalculationStage.cs:460`).
- **A member who can't be found passes eligibility.** That covers member not found, member service down, and timeouts (`BenefitCalculationStage.cs:210-247`).
- **There is no duplicate-claim edit and no real timely-filing edit.** Inbound replacement (frequency 7) and void (frequency 8) claims are adjudicated as new claims.

**What it would take to start a shadow pilot (about one quarter of focused work):**
1. Put pricing into the live pipeline.
2. Fix the payment-to-claims contract.
3. Fix unit math and the eligibility fail-open.
4. Add duplicate, timely-filing and frequency 7/8 logic.
5. Require authentication on internal services and stop trusting the tenant header.
6. Make work-queue assignment, notes and reversal real.

**What it would take to go live:** a Medicaid and MA content and compliance build-out measured in quarters, not weeks. See §6 and §7.

### Top 5 strengths (in claims-ops terms)

1. **The adjudication design is right.** The steps run in a visible order: scrub, then provider exclusion check, then network and credentialing, then benefits and accumulators, then NCCI/MUE, then COB, then AI review, then save. Messages are idempotent and every claim version is recorded as an event (`claims/Program.cs:384-391`, `claims/Services/Adjudication/ClaimAdjudicationOrchestrator.cs`).
2. **Benefit configuration is effective-dated and versioned, and accumulators are sound.** Plans go through draft, publish and supersede. The cost-share waterfall covers deductible, copay, coinsurance, out-of-pocket max, HDHP, embedded vs. aggregate, and the ACA cap. Accumulators are event-sourced and idempotent per claim (`bps/Models/PlanVersionState.cs`, `src/services/accumulator-service`).
3. **The claim detail screen is transparent.** Examiners see line-level CARC adjustments, NCCI/MUE results, the fee-schedule and benefit breakdown, accumulator progress, a change history and the AI advisory (`portal/Pages/ClaimDetailsNew.razor:431-930`). This is a better examiner view than incumbents give out of the box. Caveat: the trace is rebuilt when you open the claim, not stored at adjudication time (§8).
4. **The scale evidence is real and honestly disclosed.** About 1M synthetic claims were adjudicated on a workstation Kubernetes cluster at 124–156 claims/sec, P95 under 1 second, with a published list of failures (`docs/benchmarks/README.md`, `docs/million-claim-challenge/`).
5. **Interoperability and openness.** CMS-0057-F prior-auth APIs (PAS, CRD) and Patient Access are protected with SMART authorization (`src/services/fhir-service`). The source code is available. Capitation payment is the most finished money path: NACHA file, hold, void, retro adjustments. The AI examiner is advisory-only and has a kill switch.

### Top 5 blockers (in claims-ops terms)

1. **Payment accuracy.**
   - No pricing in the live pipeline. Allowed equals billed.
   - The payment run pays billed charges.
   - The status and line-of-business codes don't match between the claims and payment services. As a result, approved claims can go out as CLP02=3 (denied) on the 835, and choosing "Medicaid" selects Medicare claims (`pay/Models/PaymentRun.cs:223-246` vs. `claims/Models/Claim.cs:711-773`).
   - There is no lesser-of-billed logic and no DRG grouper, APC packaging, FQHC PPS or CMS PRICER parity.
2. **Controls and security an auditor would fail.**
   - 29 of 36 backend services require no login.
   - The tenant comes from a request header the caller can set (`shared/Middleware/TenantMiddleware.cs:70-98`).
   - Roles are enforced only in the portal screens, and fall back to full admin (TenantAdmin) when the role lookup fails (`portal/Services/UserContextService.cs:144-158`).
   - The examiner's identity is whatever the caller types (`claims/Controllers/ClaimsController.cs:1480`).
   - There are no dollar approval limits and no dual approval.
3. **Examiners can't run a desk on it.**
   - "Assign" is a no-op (`ClaimsController.cs:1392-1404`), and the examiner list is four hardcoded names (`portal/Pages/WorkQueues.razor:103-107`).
   - "Add Note" and "Reversal" show a success message and save nothing (`ClaimDetailsNew.razor:1262-1293`).
   - There is no pend action, no claim edit, and no CARC-coded deny reason.
   - Releasing a pend does not re-adjudicate the claim.
4. **No operational reporting or monitoring.**
   - All five Reports tabs call backend endpoints that don't exist.
   - Two of the three Grafana dashboards query metrics nothing produces.
   - Nothing detects stuck claims.
   - There are no TA1 or 999 acknowledgments for 837s, and the 277CA uses invalid status codes.
5. **Government-program depth is thin.**
   - No Medicaid payer-of-last-resort or TPL cost avoidance.
   - No state dimension in adjudication.
   - Encounters go out as **JSON**, Florida only, with no plan-paid loops.
   - No MA EDS, RAPS or MAO-002/004.
   - Appeal timelines are wrong for Medicaid and MA.
   - No denial-notice or EOB letter generation.

---

## 2. Pages my team will use

Only the portal's Claims list page has a permission check on the page itself. Every other page relies on `PermissionGate`, which hides content in the screen. The backend APIs don't check roles (§8.9). "No gate" below means any signed-in user can open the page.

### 2.1 Examiners and pend resolvers

| Page | Path | Primary user | What it actually does | Status | Evidence |
|---|---|---|---|---|---|
| Claims search | `/claims` | Examiner, supervisor | Searches claims by number, member, provider, auth number, type, status and dates, with a results grid. No bulk actions, no export. | **Implemented** | `portal/Pages/Claims.razor:40-353`; `claims/Controllers/ClaimsController.cs:381` |
| Claim detail | `/claims/{id}` | Examiner | **Real:** line detail with CARC adjustments, an "Adjudication Pipeline" tab (NCCI/MUE results), a "Benefit Breakdown" tab, change history, AI advisory panel, and export of the EOB as FHIR JSON. **Approve/Deny** appear only on pended claims. Deny reasons are 8 free-text options, not CARC/RARC. **Fake:** "Add Note" (`// TODO`, but shows a success toast) and "Reversal" (no API call). **Absent:** attachments, related-claim chain (only a single adjustment block), pend action, line override. No permission gate. | **Partial** | `ClaimDetailsNew.razor:67-99,194-243,431-930,1186-1293`; `portal/Dialogs/DenyClaimDialog.razor:15-22`; `portal/Dialogs/ReversalDialog.razor` |
| Work queues | `/work-queues` | Examiner, supervisor | Lists pended claims bucketed by pend code (NCCI, AUTH, out-of-network, COB, medical review). "Days in queue" is measured from the last update. "Override" approves with a canned reason. The summary is capped at 1,000 claims. The filter misses some pend codes (NOAUTH, MEDREVIEW and others). | **Partial** | `WorkQueues.razor:103-122,206-211,316-341`; `ClaimsController.cs:1306-1422` |
| Submit claim | `/claims/submit` | Examiner (paper keying) | "Being finalized" placeholder text. | **Stub** | `portal/Pages/ClaimsSubmit.razor:14-18` |
| Eligibility check | `/eligibility` | Examiner, member services | Real eligibility inquiry. | **Implemented** | `portal/Pages/Eligibility.razor` |
| Members | `/members` | Examiner, member services | 14-tab member view: coverage, accumulators, claims, 834 history and more. The member Claims tab URL appears to double `/api`. | **Implemented** (one defect) | `portal/Services/ServiceImplementations.cs:344-362` |
| Providers | `/providers` | Examiner, provider relations | Real search and detail. The verification link is disabled. | **Implemented** | `portal/Pages/ProviderDetailsDialog.razor:151-163` (verification link) |
| Provider verification | `/providers/verification` | Credentialing | Shows only "Coming soon." | **Stub** | `portal/Pages/ProviderVerification.razor:5` |
| Authorizations | `/authorizations` | UM, examiner | Search and submit work. Detail is read-only: no approve or deny, although the backend has a status endpoint. Attachment paths don't match the attachment service. | **Partial** | `portal/Pages/Authorizations.razor` |
| PA rule explorer | `/compliance/pa-rules` | UM, compliance | Read-only search over Texas TMPPM rules pulled out of PDFs. | **Implemented** (read-only) | `portal/Pages/Compliance/PaRuleExplorer.razor` |

### 2.2 Adjustments, appeals, disputes, correspondence

| Page | Path | Primary user | What it actually does | Status | Evidence |
|---|---|---|---|---|---|
| Appeals | `/appeals` | Disputes, appeals | Real summary, search and detail with due dates and compliance columns. No intake, assignment, decision or notes in the UI, although the backend supports them. | **Partial** (read-only) | `portal/Pages/Appeals.razor`; `src/services/appeals-service/Controllers/AppealsController.cs:217-466` |
| Correspondence | `/correspondence` | Correspondence, RFAI | Calls `/correspondence/*`, which does not exist in any backend. Templates are hardcoded. "Response received" only removes the row on screen. | **Stub** (UI only) | `portal/Pages/Correspondence.razor:268-330,400-405` |
| Mass adjudication runs | `/mass-adjudication-runs` | Supervisor, director | A read-only viewer of **benchmark** runs: throughput, P95, scenario matches. It cannot create, preview or approve a mass adjustment. | **Implemented** as a benchmark viewer; **Missing** as a mass-adjustment tool | `portal/Pages/MassAdjudicationRuns.razor:17,590-656`; `claims/Controllers/MassAdjudicationRunsController.cs:22-94` |
| Single-claim adjustment, void | none | Adjustments | The backend has an adjustment API with idempotency and a version chain (MongoDB only). **No portal page calls it.** | **Partial** (API only) | `claims/Controllers/ClaimAdjustmentsController.cs:43-195`; `ClaimsController.cs:1092` |

### 2.3 Payment, finance and capitation

| Page | Path | Primary user | What it actually does | Status | Evidence |
|---|---|---|---|---|---|
| Payment runs | `/payment-runs` | Payment, finance | Lists runs. The Create request doesn't match what the backend expects, so the selection criteria are dropped. Nothing in the UI executes a run, yet a toast says "queued". "Download ERA" calls a route that doesn't exist. There is a working Cancel. No check, EFT, hold, void or approval. | **Partial / broken** | `portal/Pages/PaymentRuns.razor:80-92,341-368`; `portal/Services/IServices.cs:2353-2363` vs `pay/Controllers/PaymentRunsController.cs:145-150` |
| Provider contracts | `/finance/contracts` | Provider relations, finance | Contract CRUD and lifecycle actions (activate, suspend, terminate). **These contracts do not drive pricing**: the pricing engine reads a different collection with a different schema. | **Implemented** (admin only) | `portal/Pages/ProviderContracts.razor`; `engines/CloudHealthOffice.FeeScheduleEngine/Persistence/FeeScheduleRepositoryMongo.cs:36-37` |
| Capitation rate config, runs, statements | `/capitation/*`, `/finance/contracts/statements` | Finance | Rate config; runs; statements with approve, void, hold and disbursement. The UI's Cancel calls a route that doesn't exist. | **Implemented** | `portal/Pages/CapitationRuns.razor`, `portal/Pages/CapitationStatements.razor` |
| AR (GL accounts, balances, cash posting, adjustments, batch rules) | `/finance/ar/*` | Finance | Premium receivables, not claims payables. The pages require a `finance:read` permission that **no role holds** except admins. | **Implemented** (premium side) | `portal/Pages/Ar*.razor`; `UserContextService.cs:360-365` |
| AR aging | `/finance/ar/aging` | Finance | The grid is always empty (`// TODO`). | **Stub** | `portal/Pages/ArAging.razor:5,189-193` |
| Premium billing | `/premium-billing` | Finance | Configured host name doesn't match the Kubernetes service name. | **Partial** | `portal/appsettings.json`; `portal/k8s` |
| Pricing API dashboard | `/pricing-api` | Admin | API keys, fee schedule CSV upload for the stand-alone pricing product, and a "Seed Demo Data" button. | **Implemented** (not used by adjudication) | `portal/Pages/PricingApiDashboard.razor:147-243` |

### 2.4 EDI, encounters and intake operations

| Page | Path | Primary user | What it actually does | Status | Evidence |
|---|---|---|---|---|---|
| EDI transactions | `/edi-transactions` | EDI ops | 834 import runs and 837 import transaction log. | **Implemented** | `portal/Pages/EdiTransactions.razor` |
| EDI operations | `/edi-operations` | EDI ops, encounters | 834 batch accept/reject, 277CA, 835 and history tabs. All four lists call routes that don't exist. Only single-claim 277CA and 835 downloads work. | **Stub** (UI only) | `portal/Pages/EdiOperations.razor:183-185` |
| Enrollment operations | `/enrollment-ops` | Enrollment | Backend routes don't exist. The trend chart is random numbers (`new Random(42)`). | **Stub** | `portal/Pages/EnrollmentOperations.razor:273-288` |
| Trading partners | `/trading-partners` | EDI ops | Two hardcoded Availity rows. Add, edit and delete are TODOs that still show success messages. | **Stub** | `portal/Pages/TradingPartners.razor:210-316` |
| Workflows | `/workflows` | IT, EDI ops | Argo workflow list. The live progress feed never receives events. | **Partial** | `portal/Pages/Workflows.razor:345` |
| Encounters | none | Encounters team | **No portal page.** | **Missing** | none |

### 2.5 Supervisor, director and configuration

| Page | Path | Primary user | What it actually does | Status | Evidence |
|---|---|---|---|---|---|
| Dashboard | `/dashboard` | Supervisor, director | Real: claim counts and amounts, recent claims, work-queue summary. The "Approval Rate" tile is approved ÷ total, not first-pass auto-adjudication. Trend is hardcoded to 0. Processing time is days converted to minutes but labelled "ms". Alerts and EDI volume call endpoints that don't exist. | **Partial** | `portal/Pages/Dashboard.razor:318-339,697-794`; `ServiceImplementations.cs:1801-1866` |
| Reports | `/reports` | Director, finance, compliance | Five tabs (claims summary, payment summary, eligibility, prior auth, provider performance). **All five backend endpoints are missing.** | **Stub** (UI only) | `portal/Pages/Reports.razor`; `ServiceImplementations.cs:3177-3260` |
| Benefit plans | `/benefit-plans` | Configuration analyst | Plan CRUD, benefits, network tiers, exclusions, validation. Versioning exists in the backend, but there is no version list, diff or draft/publish screen. | **Partial / Implemented** | `portal/Pages/BenefitPlans.razor`; `portal/Pages/BenefitPlanDetailsDialog.razor:916` |
| Reference data, terminology crosswalk | `/reference-data`, `/terminology/crosswalk` | Configuration analyst | Read-only code search. The crosswalk page needs a permission no role has. | **Implemented** (read-only) | `portal/Pages/ReferenceData.razor`, `portal/Pages/TerminologyCrosswalk.razor` |
| Settings | `/settings` | Admin | Operating mode tab is real. The billing tab is mock data (`cus_test123`, card 4242). | **Partial** | `portal/Pages/Settings.razor:266-337,386-405` |
| Users, tenants | `/settings/users`, `/platform/tenants` | Admin | Real user role assignment and tenant admin. No access-review workflow. | **Implemented** | `portal/Pages/UserManagement.razor`; `portal/Pages/PlatformTenants.razor` |
| Demo wrappers | `/demo/*` | Prospects | Anonymous access that wraps the real pages. `/demo/claims/{id}` has no gate. | (Risk) | `portal/Pages/DemoWrappers/*` |

**Live updates are not wired.** The portal has three SignalR hubs (claims, workflows, payment runs). They have no `[Authorize]`, any connected client can broadcast to all users, and no server code pushes to them (`portal/Hubs/SignalRHubs.cs`). Progress bars that depend on them will never move.

---

## 3. Pages and capabilities needed but not built

| Area | Need | Status | What exists, and the gap |
|---|---|---|---|
| **Work management** | Pend and work queues with routing rules | **Partial** | Queues are derived from the pend code. There are no routing rules, no skill-based routing, and no queue entity (`ClaimsController.cs:1306-1387`). |
| | Assignment | **Stub** | The backend logs the request and returns OK. The UI lists four hardcoded names. |
| | SLA timers and aging buckets | **Missing** | Only "days since last update". No received-date clock, no due dates, no regulatory timers. |
| | Supervisor rebalancing, examiner productivity | **Missing** | No bulk reassign. No per-examiner counts or throughput. |
| **Claim workspace** | Full history | **Partial** | Version-event timeline (MongoDB only; empty on Cosmos, `claims/Program.cs:93`). |
| | Edit or override with reason codes | **Missing** | Approve/Deny only. Deny is free text with no CARC. Override is a canned approve. |
| | Line-level adjudication detail and rationale | **Partial** | Displayed, but the trace is rebuilt at read time (`claims/Services/AdjudicationTransparencyBuilder.cs`). Line CARCs from the engine are thrown away by the live pipeline (`BenefitCalculationStage.cs:530-538`). |
| | Attachments | **Missing** in the claim UI | attachment-service links files to claims (`src/services/attachment-service/Models/Attachment.cs:27-42`), but the claim page doesn't show them. |
| | Notes | **Stub** | Add Note is fake. |
| | Related claims (duplicates, adjustments, voids, replacements) | **Partial** | The internal adjustment chain exists (MongoDB). There is no duplicate detection and no inbound frequency 7/8 handling. |
| **Adjustments & reprocessing** | Single adjustment | **Partial** | API only, MongoDB only, no approval step. |
| | Mass reprocessing (retro fee schedule, contract, eligibility) with preview and approval | **Missing** | "Mass adjudication runs" is a benchmark results store. |
| **Edits** | NCCI PTP and MUE | **Partial** | The engine logic is real. It ships 27 PTP pairs and 23 MUEs (`engines/CloudHealthOffice.NcciEngine/Data/NcciSeedData.cs`). There is no CMS file loader and no separate Medicare and Medicaid tables. Anatomic modifiers are ignored. One claim-level date is used for all lines. |
| | Clinical edits (age/sex, diagnosis-to-procedure, obsolete codes) | **Missing** | The flags exist in config but no code uses them (`engines/CloudHealthOffice.ClaimsScrubEngine/Data/DefaultStandardRules.cs:30-65`). |
| | Duplicate logic | **Missing** | CARC 18 is misused for "no benefit mapping" (`BenefitCalculationEngine.cs:537-541`). |
| | Timely filing | **Stub** | Warning only, 365 days, measured against today (`ValidationRuleEngine.cs:461-474`). |
| | Authorization matching | **Partial** | The live pipeline checks only Medicaid inpatient (POS 21), using the first line's code, and passes when the auth service is down (`BenefitCalculationStage.cs:326-359`). No unit consumption, no member match. |
| | Plan-specific configurable edits | **Stub** | Edits can only be skipped per request. |
| **Pricing** | Fee schedules, RBRVS, % of Medicare | **Partial** | Present in FeeScheduleEngine (`RateResolutionService.cs`), but **not called by the live pipeline**. Multiple-procedure reduction is over-applied. Commercial "% of Medicare" is coded as billed × rate. |
| | DRG / APR-DRG, APC / EAPG, CMS PRICER parity | **Missing** | DRG is base × weight with the DRG supplied by the caller. No grouper. APC is a stored rate. |
| | FQHC/RHC PPS and wrap, nursing facility per diem, case rates, carve-outs, outliers | **Missing** | Zero hits. |
| | Lesser-of-billed | **Missing** | No logic found. |
| **COB/TPL** | Other coverage | **Partial** | One other-insurance record per coverage. `IsMedicare` is hardcoded false (`src/services/coverage-service/Controllers/CoverageController.cs:182-183`). |
| | Paying as secondary | **Missing** in the live pipeline | Claims where CHO is secondary are pended "not supported phase 1" (`CoordinationOfBenefitsStage.cs:12-21`). |
| | Medicare crossover, payer of last resort, cost avoidance vs. pay-and-chase, subrogation recovery | **Missing** | Subrogation is a pend only. |
| **Payment** | Check run | **Partial / defective** | See §1 and §4. |
| | EFT (NACHA) for claims, check print, positive pay | **Missing** | NACHA exists only for capitation and premium. |
| | 835 | **Partial** | Missing N3/N4, PER, REF, LQ (RARC) and others. CAS amounts are $0 placeholders, so the 835 won't balance. |
| | Holds, voids/reissue, stale-dated checks and escheatment | **Missing** | |
| | Recoupment and offsets (PLB) | **Stub** | The model exists; nothing fills it. |
| | Prompt-pay interest | **Missing** | Config only (`src/services/reference-data-service/Models/StateComplianceConfig.cs:14-32`). |
| | 1099 | **Missing** | |
| **Provider-facing** | 276/277 responder | **Missing** | Only a 276 parser with no consumer. |
| | Disputes and reconsiderations | **Partial** | appeals-service handles cases. An overturn doesn't reprocess the claim. |
| **Encounters** | State encounter 837 | **Stub** | The live path writes JSON, Florida only. |
| | Response reconciliation | **Partial** | 999 at batch level only. No 277CA or state responses. |
| | MA EDS, RAPS, MAO-002/004 | **Missing** | |
| **Controls** | Role-based approval limits, dual approval | **Missing** | |
| | Full audit trail | **Partial** | Not tamper-evident; actor can be spoofed. |
| | Examiner QA sampling | **Missing** | |
| **Configuration** | Benefit plans | **Partial** | Versioned backend; thin UI. |
| | Contracts | **Partial** | Admin record only, not linked to pricing, no versions. |
| | Fee schedules for adjudication | **Missing** UI | |
| | Edit, NCCI and PA rule maintenance | **Missing** UI | |

---

## 4. End-to-end monitoring: ingestion to adjudication to payment

### 4.1 How a claim actually moves

There are **two adjudication engines**, and the SFTP intake path triggers both for the same claim:

- **Path A (the platform's own).** `POST /api/v1/claims` → Azure Service Bus → `ClaimAdjudicationOrchestrator`, which runs 8 stages.
- **Path B (Argo workflow).** Kafka `claims-adjudication` → Argo `claims-adjudication-workflow.yaml` → `bps/Controllers/AdjudicationController.cs` `/adjudicate` → results written back.

Path B is the only one that prices. Path A is the one the platform's own pipeline and portal are built around. The code includes race guards so the two writers don't overwrite each other (`ClaimsController.cs:585-604`). The one-million-claim benchmark used a third arrangement for its strict baseline: the test harness itself called `/adjudicate` and wrote the result back. A claims director needs one engine of record. Today there isn't one.

### 4.2 Stage-by-stage assessment

| # | Stage | What's tracked | Can I find the claim and its state in the portal? | Alerting / dashboards | Stuck-claim detection | Reprocess / replay | Idempotency |
|---|---|---|---|---|---|---|---|
| 1 | **Intake** (837 over SFTP, raw 837 upload, JSON API; FHIR Claim and paper are **Missing**) | The raw 837 upload logs each claim as Accepted or Rejected (`claims/Controllers/ClaimsV1Controller.cs:203-212`). `SubmittedDate` is set to processing time, not the receipt date (`claims/EDI/Inbound/X12837ClaimMapper.cs:72`). Line of business is hardcoded to Commercial on 837 intake (`:53`). | Partly: the EDI Transactions page shows the 837 import log. | None. The EDI metric exists but is never recorded. | None | None. The SFTP job **re-fetches the same files every 5 minutes**, because archiving copies files but doesn't remove them (`infrastructure/argo-workflows/x12-837-ingest.yaml:255-285`). | **Not idempotent** at file or claim level. There is no unique key on claim number. |
| 2 | **Acknowledgments** (TA1, 999, 277CA) | TA1 and 999 for 837: **Missing**. 277CA is generated on demand for one claim, uses invalid STC01 category codes (`claims/Services/ClaimAcknowledgmentService.cs:135-148`), and is not saved or sent. | Single-claim 277CA download only. | None | None | None | Control numbers are built from timestamps. |
| 3 | **Front-end edits and rejections** | The scrub result is held only in memory; nothing is saved. A front-end reject is saved as **Denied** because there is no Rejected status (`claims/Services/Adjudication/Stages/PersistenceStage.cs:111-118`). | Only as a denied claim. | None | None | None | n/a |
| 4 | **Member and provider match** | Member is matched on exact ID only, and an unknown member **passes**. Provider exclusion check is real. Network and credentialing are real, but a provider-service outage **denies** the claim (`claims/Services/Adjudication/Stages/NetworkCredentialingStage.cs:240-287`). The matched network tier is thrown away. | Partly, through denial reason or pend code. | None | None | None | Cached lookups |
| 5 | **Adjudication stages** (Path A) | Only the final status and result are saved. `AdjudicatedDate` is never set by Path A. Per-stage results and spans are not saved or exported: the trace source name `"ClaimsService.Adjudication"` isn't registered (`shared/Observability/ObservabilityExtensions.cs:83-96`). Accumulators post **before** NCCI and COB run. | Yes: the claim detail page, rebuilt at read time. | Outcome and latency metrics exist only on Path B, and errors are counted as "denied" (`bps/Controllers/AdjudicationController.cs:659-684`). | None | No re-adjudicate endpoint. | Strong. Service Bus message IDs and duplicate detection; the orchestrator skips claims already adjudicated. |
| 6 | **Pend to resolution** | A single pend-details slot that the last writer overwrites, so a COB pend erases the NCCI pend details (`claims/Services/Adjudication/Stages/CoordinationOfBenefitsStage.cs:326`). Release sets Approved or Denied **without re-running adjudication or recalculating amounts** (`ClaimsController.cs:1436-1510`). | Yes: the work queues page. | None | Aging is measured from last update only. | None | Resolve is guarded by status. |
| 7 | **Finalization, EOB/EOP** | Paid transition is idempotent on check number (`claims/Services/ClaimFinalizationService.cs:213-224`). FHIR EOB is built on read. **No member EOB or provider EOP document** is produced. Path A's "adjudicated" event has **no subscriber** (`infrastructure/azure/main.bicep:315-330`). | EOB as JSON. | None | None | None | Yes, for Paid. |
| 8 | **Payment** | Payment run records. Claims whose trading partner can't be resolved are marked Posted but stay Approved, so **they will be paid again** on the next run (`pay/Services/PaymentRunService.cs:155-174`). Two runs can issue duplicate check-number ranges. No GL entry and no bank reconciliation. | Payment runs list. | None (the SignalR progress feed is dead). | None | Reversal run exists. | Partial. Finalize is idempotent; the run itself is not. |
| 9 | **Encounters** | The consumer listens on Kafka `adjudication-completed`, but **nothing publishes to that topic** (`src/services/encounter-submission-service/KafkaConsumers/AdjudicationCompletedConsumer.cs:95`). Batches only within 48 hours of the deadline. Output is JSON. | No portal page. | Deadline-warning API only. | Deadline warnings | Manual resubmission | Batch-level |

**Plumbing risks on top of the stages:**
- If the Service Bus connection string is missing, the system quietly falls back to an in-memory bus. Messages are then lost on restart, and each of the three replicas has its own private bus (`shared/Messaging/MessagingServiceCollectionExtensions.cs:55-67`; `claims/k8s/claims-service-deployment.yaml:107-112`).
- Nobody reads the Service Bus dead-letter queue, and no alert watches it.
- Readiness health checks don't include Service Bus or Kafka.

### 4.3 The control tower a director needs

| Panel | What it shows | What exists | What must be built |
|---|---|---|---|
| **Live inventory by stage** | Received → acknowledged → in adjudication → pended (by reason) → finalized → paid → encounter accepted | Dashboard status counts; work-queue summary (capped at 1,000) | A saved status history with a timestamp per transition; a Received status and clean-claim date; a Rejected status; an inventory snapshot table |
| **Throughput** | Claims in and out per hour/day by line of business, form type and channel | Benchmark-only throughput | Business metrics emitted from Path A (`ChoMetrics` counters per stage); line-of-business tags |
| **Aging** | Buckets (0–15, 16–30, 31–45, 46–60, >60 days) from the received date, by pend reason and examiner | "Days in queue" from last update | Received date; pend date; aging computation; examiner assignment |
| **Failure and rejection rates** | 999/277CA rejects, front-end rejects, denials by CARC, dead-letter depth, adjudication errors | None | Acks; Rejected status; CARC counters; dead-letter monitor; stage error metrics |
| **SLA and prompt-pay breaches** | Claims approaching or past the state or CMS clock, interest exposure | Florida prompt-pay config only | Per-state, per-line-of-business clock engine; clean vs. unclean flag; interest accrual |
| **Stuck claims** | Claims with no state change in more than N minutes in an automated stage | None | A sweeper job, a stuck-claims queue, and a replay button |
| **Drill-down** | From any tile to a claim list to the claim workspace | The claim workspace exists | Filter-to-list links; saved views |
| **Payment cycle** | Run status, dollars, exceptions, unfunded runs, 835 delivery | Run list | Approval, funding and exception panels; 835 delivery acknowledgment |

**Size:** L (8–12 engineering weeks, after the status and date model is fixed).

---

## 5. Reporting capabilities

### 5.1 What exists

| Item | Real data? | Evidence |
|---|---|---|
| Dashboard KPI tiles (counts, sums, average days submitted to adjudicated) | **Yes** (with metric bugs) | `claims/Repositories/ClaimRepository.cs:918-990`; `ServiceImplementations.cs:1807-1810` |
| Reports page, 5 tabs | **No.** The backend endpoints don't exist. | `ServiceImplementations.cs:3177-3260` |
| CSV export | Only the claims-summary tab (dead) and the EDI history tab (dead) | `Reports.razor:550-556`; `EdiOperations.razor:802-811` |
| Grafana "Claims Adjudication - Real-Time" | **No.** It queries `claims_*` metrics that nothing produces. | `infrastructure/monitoring/grafana-claims-dashboard.json` |
| Grafana X12 overview | **No.** It queries `x12_*` metrics that nothing produces. | `infrastructure/monitoring/grafana/x12-processing-overview.json` |
| Grafana `cho-adjudication` | Partially. Path B only. The "by step" panel is actually grouped by outcome, and the EDI panel is empty. | `infrastructure/observability/grafana/dashboards/cho-adjudication.json` |
| Prometheus alert rules | **Not loaded**, and they target Kafka and X12 metrics that aren't produced | `infrastructure/monitoring/prometheus-rules.yaml`; `infrastructure/observability/prometheus/prometheus.yml` |
| FHIR bulk `$export` | **Stub.** It marks the job complete at once with 0 records; the output URL doesn't exist. | `src/services/fhir-service/Services/BulkExportService.cs:24-69` |
| Warehouse feed, SQL, BI connector | **Missing** | No Synapse, Databricks, change feed or Power BI integration |
| Encounter submission summary API | Yes (no UI) | `src/services/encounter-submission-service/Controllers/EncounterSubmissionController.cs:52,67` |
| Mass-run benchmark telemetry | Yes | `MassAdjudicationRuns.razor` |

### 5.2 Gap check against standard claims reporting

| Report | Status | Notes |
|---|---|---|
| Inventory and aging; pend reasons | **Partial** | Pended claims only; 1,000 cap; aging from last update; 5 hard-coded buckets |
| Auto-adjudication (first-pass) rate | **Missing** | The dashboard's "Approval Rate" is not first-pass. No field records whether a human touched the claim. |
| Turnaround and state prompt-pay compliance; MA clean-claim timeliness and interest | **Missing** | Configuration with no code using it; no received or clean date |
| Denial rates, top CARC/RARC | **Missing** | Denied count only. Human denials carry no CARC. |
| Payment and financial accuracy; QA results | **Missing** | Benchmark only, not operational |
| Examiner productivity | **Missing** | No assignment, so no attribution |
| Paid claims lag / IBNR triangles | **Missing** | Zero hits |
| Encounter volume, acceptance rate, error trends | **Partial** | Summary API only; rejection reasons are stored but not trended |
| Provider and network payment trends; high-dollar claims | **Missing** | Provider tab is dead; no high-dollar logic |
| Regulatory extracts (state reports, CMS ODAG/CDAG universes) | **Missing** | Zero hits |

**Self-service vs. engineering.** Nothing is self-service. Data lives in operational MongoDB/Cosmos collections. There is no report builder, warehouse or BI connector. Every report on this list is an engineering request today.

The fastest path to useful reporting is a nightly or change-feed export into an analytics store, with a claims fact table (status history, dates, CARC per line, examiner, line of business, state). Pre-built Power BI or Superset reports would then sit on top. **Size: L.**

---

## 6. What would make CHO rival QNXT, Facets and HealthRules

### 6.1 Where CHO is already differentiated

| Differentiator | What the code actually supports |
|---|---|
| **FHIR-native data and CMS-0057-F APIs** | Real PAS, CRD and Patient Access endpoints with SMART scopes and a hardened tenant check (`src/services/fhir-service`). The EOB is projected from claim data. Incumbents bolt this on; CHO has it in the core. Bulk `$export` is a stub and public PA metrics are missing. |
| **Source availability, no implementation lock-in** | Real. The code is readable and the pipeline is understandable. Plans can audit their own adjudication logic, which no incumbent offers. |
| **Cloud-native scale** | Real but early. About 1M claims at 124–156/sec on one workstation with P95 under 1 second. Only 13% of claims were scored for disposition correctness, and only 2% (one clean professional scenario) for payment amount. Runs used MongoDB rather than Cosmos, did not include payments, and were not in a production cloud. |
| **Transparent adjudication view** | A strong examiner experience once the trace is saved at adjudication time rather than rebuilt. |
| **Agentic AI claims examiner** | **What it does:** reads NCCI pends that one of the listed modifiers could fix (59/XE/XS/XP/XU) and calls Claude through a forced tool schema. It writes an *advisory* (Approve, Deny, RequestInfo or Escalate, with confidence, rationale and citations) onto the claim. It **cannot change status or payment** (`claims/Controllers/ClaimsController.cs:745-794`). **Guardrails:** advisory only; a kill switch (`Disabled`/`BestEffort`) that applies to the whole deployment, not per tenant; failures fall back to "escalate to human"; model ID and prompt version are recorded in an AI audit collection. **Weaknesses:** member ID, provider name and NPI, diagnoses and dates are sent **unredacted** (`src/services/claims-examiner-service/Services/Examiner/ExaminerPromptBuilder.cs:119-163`); no BAA for this vendor is on file; prompt and inputs are not saved; temperature is not pinned, so results can't be reproduced; the minimum-confidence setting is never read; the portal's one-click "Override" applies a canned reason, which encourages rubber-stamping. **Verdict:** a promising, correctly scoped design (human decides, AI advises) that needs governance work before any plan's compliance officer would allow it on PHI. |

### 6.2 Where incumbents are decades ahead

- **Configuration depth.** Incumbents have product, benefit, contract, pricing, edit and workflow configuration with effective dating, testing sandboxes, and business-analyst UIs. CHO has versioned benefit plans and little else a business analyst can maintain.
- **Pricing libraries.** Incumbents have certified CMS PRICER modules or vendor integrations (e.g., Optum/3M groupers, EAPG), plus state Medicaid FFS methodologies. CHO has a fee schedule engine that isn't on the live path and has no grouper.
- **Edit content.** Incumbents ship or integrate full NCCI (Medicare and Medicaid), MUE, clinical edit libraries, and duplicate and timely filing logic. CHO has 27 PTP pairs and no duplicate logic.
- **State Medicaid nuance.** Incumbents handle patient liability, spend-down, nursing facility and LTSS billing, EVV, TPL cost avoidance, retro-eligibility mass adjustment and state encounter formats for dozens of states. CHO has some Texas PA rules and a Florida encounter transformer that isn't wired in.
- **Payment operations.** Incumbents have check and EFT, 835, PLB recoupments, interest, 1099, holds, escheatment and GL interfaces. CHO's FFS payment path is a skeleton with money-changing defects.
- **Proven audit history.** Incumbents have years of CMS program audits, state audits and SOC 1/SOC 2 reports. CHO has no SOC 2 or HITRUST report yet (`docs/diligence/SECURITY-ONE-PAGER.md:7`).

### 6.3 Prioritized roadmap

**Must-have to pilot (shadow mode, no live dollars)**

| # | Item | Size |
|---|---|---|
| P1 | Pick **one** adjudication engine of record. Put fee schedule and contract pricing in Path A; pass the matched network tier; add lesser-of-billed | M |
| P2 | Fix 837 unit and charge semantics (SV102 is the line total) | S |
| P3 | Make eligibility fail closed when the member is not found or the member service is down (pend, don't pass) | S |
| P4 | Duplicate-claim edit (exact and suspect); inbound frequency 7/8 with REF*F8 linkage | M |
| P5 | Received date and clean-claim date; timely filing edit by line of business, state and contract, with CARC 29 | M |
| P6 | Authentication on all internal services; tenant taken from a validated token (port the fhir-service middleware); `RequireTenantId=true`; remove the TenantAdmin fallback | M |
| P7 | Backend permission enforcement; actor identity from the token; CARC/RARC required on human denials | M |
| P8 | Real work-queue assignment, notes, pend action, and release-with-re-adjudication | M |
| P9 | Save the adjudication trace per claim and line (stage results, plan version, fee schedule version, NCCI quarter, auth ID) | M |
| P10 | Augment-mode comparison against incumbent results (the shadow-pilot scorecard) | M |

**Must-have to go live**

| # | Item | Size |
|---|---|---|
| G1 | Payment: fix the claims contract; approval with dual control; NACHA/CCD+ and check/positive pay; holds, voids and reissue; compliant 835 with balanced CAS, LQ and PLB recoupment; prompt-pay and MA interest; 1099; GL feed; bank reconciliation | L |
| G2 | Pricing content: MS-DRG/APR-DRG grouper integration, OPPS/APC and EAPG, CMS PRICER parity for MA, FQHC/RHC PPS and wrap, nursing facility per diem, outliers and stop-loss, case rates and carve-outs | L |
| G3 | Full NCCI and MUE quarterly loader (Medicare and Medicaid, practitioner/hospital/DME); clinical and code-validity edits; plan-configurable edit UI | L |
| G4 | COB/TPL: secondary payment, Medicare crossover, Medicaid payer of last resort, cost avoidance vs. pay-and-chase, recovery tracking | L |
| G5 | Encounters: X12 837 output with 2320/2430 plan-paid loops; multi-state companion guide configuration; 999, 277CA and state response ingestion; reconciliation to paid claims. MA: EDS, MAO-002/004, RAPS where applicable | L |
| G6 | Mass adjustment and reprocessing with population definition, preview, approval, before/after snapshots | M |
| G7 | Correspondence: EOB, EOP, denial notices, NABD, IDN, with required content and timing | L |
| G8 | Appeals: correct Medicaid and MA timelines, member-appeal constructs, overturn triggers reprocessing | M |
| G9 | Tamper-evident audit store with 10-year retention; access reviews; tested backup and restore; SOC 2 Type II report | L |
| G10 | Operational reporting: warehouse feed, control tower, prompt-pay, CARC, first-pass, lag/IBNR, encounter acceptance, audit universes | L |
| G11 | Stuck-claim sweeper, dead-letter replay, alerts on the real pipeline | M |

**Differentiators to win deals**

| # | Item | Size |
|---|---|---|
| D1 | Governed AI examiner: redaction, BAA, saved inputs, pinned model and temperature, confidence floor, clinician sign-off for medical necessity, measured agreement rate. Extend to COB, auth and duplicate pends | M |
| D2 | "Explain this payment" view backed by the saved, versioned trace. This is an audit-response superpower | S (after P9) |
| D3 | Published, independently verified benchmark: full corpus correctness with real CMS pricing, on Cosmos, in a cloud region, including payments | M |
| D4 | Augment mode as a product: run beside QNXT or Facets, show variance, and cut over by claim type | M |
| D5 | Self-service analytics on FHIR and a claims fact store, with exports for state and CMS universes | M |

---

## 7. Medicaid and Medicare Advantage readiness

### 7.1 Medicaid managed care (and CHIP)

| Requirement | Status | Evidence / gap |
|---|---|---|
| State-specific prompt-pay rules | **Stub** | Florida config only, nothing uses it (`src/services/reference-data-service/Models/StateComplianceConfig.cs`) |
| Retro-eligibility and mass reprocessing | **Stub** | A RETROELIG pend exists; no mass reprocess |
| Medicaid NCCI | **Missing** | Tables have no program dimension |
| FQHC/RHC PPS and wraparound | **Missing** | |
| LTSS/HCBS and nursing facility billing | **Missing** | |
| EVV linkage | **Missing** | |
| Patient liability / share of cost / spend-down | **Missing** / **Stub** | Spend-down is a pend only |
| TPL and cost avoidance; payer of last resort | **Missing** | The COB engine would assign balance-bill liability to the member |
| State encounter formats and companion guides | **Stub** | Florida FMMIS transformer, unwired; live path is JSON |
| Multi-state configuration | **Missing** | Adjudication has no `StateCode` at all; Texas is the default for PA rules |
| CHIP as a line of business; cost-sharing caps (5%) | **Missing** | No CHIP value in claims or benefit line-of-business enums |
| Appeals (72-hour expedited, 30-day standard, State Fair Hearing, continuation of benefits) | **Partial** | Timelines are 30 and 60 days (`src/services/appeals-service/Controllers/AppealsController.cs:93`) |
| Texas PA rules | **Partial** | 10 seed rules |

**Verdict: Not ready.** What drives it:
- no Medicaid payer-of-last-resort or TPL logic;
- no state dimension in adjudication;
- no Medicaid pricing methods (PPS, per diem, APR-DRG/EAPG);
- encounters can't be submitted;
- no member-liability handling;
- wrong appeal timelines.

### 7.2 Medicare Advantage and D-SNP

| Requirement | Status | Evidence / gap |
|---|---|---|
| CMS PRICER parity (IPPS, OPPS, SNF, HH, ESRD, IPF, IRF, LTCH, hospice) | **Missing** | PricingApi is a lookup tool with no wage index, outliers or packaging, and it isn't on the claim path |
| Medicare/Medicaid crossover; integrated D-SNP claims; QMB protections | **Missing** | |
| MA clean-claim timeliness and interest (42 CFR 422.520) | **Missing** | |
| EDS, MAO-002, MAO-004, RAPS | **Missing** | |
| Risk adjustment | **Stub** | About 49–126 ICD mappings, Community Non-Dual factors only. **D-SNP members would be mis-scored.** |
| Organization determination and payment dispute linkage | **Partial** | appeals-service cases; no IRE auto-forward; payment reconsideration timelines wrong |
| MA / D-SNP as line-of-business values | **Missing** | Only Commercial, Medicare and Medicaid exist |

**Verdict: Not ready.** What drives it: no CMS pricing parity, no encounter (EDS) program, no interest, no dual-eligible handling, and demo-grade risk adjustment.

### 7.3 All lines of business

| Requirement | Status | Notes |
|---|---|---|
| HIPAA X12 5010 coverage | **Partial** | 837 in (parser good; mapping gaps). 834 in. 270/271 outbound. No TA1 or 999 for 837s. 277CA non-conformant. 276/277 responder missing. 835 incomplete and unbalanced. 275 ack always "accepted". 278 via PAS. |
| CARC/RARC usage | **Partial** | The live pipeline drops line CARCs. Human denials carry no CARC. Invalid "999" and misused CARC 18 appear. No CARC/RARC reference table ships. |
| 1099 | **Missing** | |
| Multi-tenant and multi-line-of-business isolation | **Partial — audit risk** | The **header-assertion tenant gap is confirmed** for 29 services. Line-of-business enums disagree between services. |
| Scale evidence | **Partial** | The one-million-claim benchmark is real throughput evidence, but not evidence of pricing or payment correctness. |

---

## 8. Regulatory and audit readiness

The scenario: CMS, the state, an EQRO, OIG or an external auditor sends a data request with a 10-business-day deadline.

| # | Item | Status | Evidence | Likely audit finding if live today |
|---|---|---|---|---|
| 8.1 | **Decision reconstruction** (eligibility, contract, benefit, edits, auth and pricing as of the adjudication date) | **Partial / Stub** | Only the final result is saved (`PersistenceStage.cs:66-78`). Stage results are in memory only. No plan version, contract, fee schedule version or NCCI quarter is stamped on the claim. The detail view is rebuilt at read time (`AdjudicationTransparencyBuilder.cs`). Contracts are overwritten in place (`src/services/provider-contracts-service/Repositories/MongoProviderContractRepository.cs:122-128`). | "The plan could not demonstrate the basis for payment or denial for sampled claims." This is a CDAG/ODAG and state claims-audit finding. |
| 8.2 | **Immutable audit trail** (claims, overrides, adjustments, config, user access) | **Partial** | Claim version events (MongoDB; **a no-op on Cosmos**). Not hash-chained and not write-once. The actor comes from the request body or an `X-User-Id` header. The generic status update writes no event (`ClaimsController.cs:456-530`). No history for contracts or roles. Log retention is 30 days (`infrastructure/azure/azure/aks-main.bicep:35`). The TypeScript HIPAA logger writes to the console (`src/security/hipaaLogger.ts:133-139`). | Audit logs are not protected or attributable, and retention is far below 10 years (42 CFR 422.504(d)). SOC 2 CC7.2/CC8.1 exception. |
| 8.3 | **Audit universes and extracts** (Part C ODAG/CDAG, payment disputes, state extracts) | **Missing** | No universe or extract code. `$export` is a stub. | Universes would be late or built by hand. A universe that fails integrity testing can lead to referral or a civil money penalty. |
| 8.4 | **Timeliness compliance** (prompt-pay and clean-claim clocks, interest, timely filing) | **Missing** | No received or clean date. Prompt-pay config is never used. Timely filing is a warning only. No interest. | Prompt-pay violations with unpaid interest, plus inability to prove the clocks were measured correctly. Both are common state Medicaid and CMS findings. |
| 8.5 | **Denial and notice compliance** (CARC/RARC, specificity, member and provider notices, link to appeals) | **Missing / Partial** | Human denials have free-text reasons only. No notice generation. Appeals link to claims, but an overturn doesn't reprocess the claim. | Denial notices missing or lacking required content. Overturned denials not paid timely. |
| 8.6 | **Encounter data integrity** | **Stub** | JSON output, no plan-paid amounts, no reconciliation, no completeness metrics. | Encounter submission and completeness standards not met. This feeds directly into state rate setting and withholds. MA: no EDS, which puts risk-adjustment revenue and RADV defense at risk. |
| 8.7 | **Payment integrity and overpayments** (detection, recoupment, 60-day rule, FWA referral) | **Missing** | The only FWA control is the OIG/LEIE/SAM exclusion check (`claims/Services/Adjudication/Stages/ProviderIntegrityStage.cs`). The fraud model file is a placeholder (`src/ml/README.md:7-9`). No overpayment ledger, 60-day clock or SIU referral. | Compliance program deficiency under 42 CFR 422.503(b)(4)(vi) and 438.608. 60-day rule exposure. |
| 8.8 | **CMS-0057-F** (PA metrics, payer APIs) | **Partial** | PAS, CRD and Patient Access exist and require SMART authorization. Public PA metrics are missing (self-assessed "Phase 2", `docs/compliance/CMS-0057-F-READINESS-MATRIX.md:65`). Payer-to-Payer controllers exist. | Required PA metrics can't be published. |
| 8.9 | **AI in decisions** | **Partial** | Advisory-only, with model ID and prompt version logged. Inputs are not saved, temperature not pinned, PHI not redacted, no BAA for the vendor, no confidence floor. No rule requires a clinician for medical-necessity denials. | HIPAA minimum-necessary and BAA finding. Can't show that MA coverage decisions rest on the individual's circumstances (CMS Feb-2024 FAQ; 42 CFR 422.566(d)). Exposure under state AI and utilization-management laws. |
| 8.10 | **Security and privacy controls** (RBAC, access logging and review, encryption, tenant isolation, backup) | **Partial / Missing** | UI-only roles that fail open to TenantAdmin. 29 services unauthenticated. **Tenant taken from a request header** (`shared/Middleware/TenantMiddleware.cs:70-98`; the repo itself calls this "open, by design" in `AUDIT/investor-readiness-2026-09.md:499-513`). Customer-managed keys not wired in. Cosmos open to the public network. Single-replica in-cluster MongoDB with no TLS or backup. The identity service uses development certificates. No SOC 2 or HITRUST report; backup/DR "not a completed customer DR test" (`docs/diligence/SECURITY-ONE-PAGER.md`). | Access-control failure under HIPAA §164.312(a)(1) and SOC 2 CC6.1. Tenant data segregation can't be attested to the state. No tested DR. |
| 8.11 | **Reprocessing defensibility** | **Partial** | Single adjustments have reason, idempotency key and version chain, but the actor is spoofable and there is no approval step. Mass reprocessing doesn't exist. | Retro-rate or eligibility mass adjustments can't be evidenced (population, before/after, authorization). |
| 8.12 | **Examiner QA** | **Missing** | Only an "AI agreement" field. | No auditing-and-monitoring evidence for claims accuracy. |

**Verdict: could a plan pass a CMS program audit and a state Medicaid claims audit on CHO today? No.**

**Remediation, in priority order:**
1. Authenticate all services; take the tenant and actor from a validated token; enforce permissions in the backend; add dollar approval limits and dual approval. *(8.10, 8.2)*
2. Save a versioned adjudication trace per claim and line, with config versions. Version contracts and fee schedules. *(8.1)*
3. Add received and clean-claim dates, timely filing, a prompt-pay and MA interest engine, and saved acknowledgments. *(8.4)*
4. Require CARC/RARC on every denial; generate notices with required content and timing; make appeal overturns trigger reprocessing; correct appeal timelines. *(8.5)*
5. Build a tamper-evident audit store with 10-year retention; audit config and role changes; run periodic access reviews. *(8.2)*
6. Govern the AI examiner: BAA, redaction, saved inputs, pinned model and temperature, confidence floor, clinician rule for medical necessity. *(8.9)*
7. Produce X12 encounters with reconciliation; build MA EDS, MAO-002 and MAO-004. *(8.6)*
8. Build the universe and extract builder (ODAG/CDAG, payment disputes, state templates); make `$export` real; publish PA metrics. *(8.3, 8.8)*
9. Add overpayment detection, recoupment, a 60-day tracker and SIU referral. *(8.7)*
10. Add a mass-reprocessing workflow with approvals and before/after snapshots; add examiner QA sampling. *(8.11, 8.12)*
11. Wire in customer-managed keys, private networking, managed backups with a tested restore, and complete SOC 2 Type II. *(8.10)*

---

## 9. Overclaims

| Claim | Where stated | What the code shows | Suggested honest wording |
|---|---|---|---|
| "835 ERAs generated. HIPAA 5010 compliant remittance advice per provider per payment run." | `src/site/platform.html:1564` | Missing N3/N4, PER, REF, LQ. CAS amounts are $0 placeholders. Paid amount falls back to billed. CLP02 can be wrong. | "Generates draft 835 remittance files; 5010 conformance and balancing are in progress." |
| "87.3% auto-adjudication rate… 1.2 second average" | `src/site/platform.html:633,684` (labelled demo) | No first-pass metric exists in the product. The dashboard shows an approval rate and a mis-scaled time. | "Illustrative demo values; first-pass rate reporting is on the roadmap." |
| "ClaimStatusService — 276/277 real-time claim status inquiry" | `src/site/platform.html:903` | No such service. 276 parser only. The status gateway is outbound. | "276 parsing available; payer-side 277 response planned." |
| "Multi-state Medicaid enrollment — TMHP PEMS, CAQH, PAVE, FMMIS, eMedNY" | `src/site/platform.html:1009` | PAVE, eMedNY and FMMIS sources are marked STUB (`engines/CloudHealthOffice.ProviderEnrollmentService/Sources/*`). | "TMHP and CAQH sources; CA, NY and FL in development." |
| "Operations portal … dashboards, work queues" | `README.md:65` | Reports tabs call missing endpoints. Work-queue assignment is a no-op. | "Claims search, claim detail and pend resolution; reporting and queue management in progress." |
| "Pricing and edits — Fee schedules, NCCI/MUE checks, claims scrubbing, COB" | `README.md:63` | The fee schedule engine is not on the live pipeline. NCCI ships 27 sample pairs. COB pends secondary claims. | "Pricing, NCCI/MUE and COB engines with sample content; production content loading and secondary COB in progress." |
| "155.89 claims/sec at 1M; every one of the 1,000,000 claims verified terminal" | `src/site/index.html:433-448`; `src/site/evidence.html:263-271` | Accurate for episode 16 (with 122 timeouts). Doesn't disclose that 13% of claims were scored for disposition and 2% for payment, on synthetic content, MongoDB, a workstation. | Add: "Correctness scored on 130K scenario claims; payment amounts verified on 20K clean professional claims; synthetic fee schedules; local cluster." |
| "Per-tenant multi-tenancy enforced across services, databases… and Kafka topics." | `docs/POSITIONING.md:281` | Tenant comes from a request header on 29 unauthenticated services, falling back to `default-tenant`. | "Tenant-scoped data model; token-enforced isolation in fhir-service, with remaining services in progress." |
| "Argo-orchestrated adjudication workflow … wiring the pipeline end-to-end." | `docs/POSITIONING.md:280` | Two (in the benchmark, three) competing adjudication write paths. Only Argo prices. | "Two adjudication paths are being consolidated into one engine of record." |
| "Three concrete personas — all three are real… already proven CHO in production" | `docs/POSITIONING.md:289,293` | `docs/status/POSITIONING-AUDIT.md:30`: "there is no production customer yet." | "Target personas; no production deployments yet." |
| "Full cloud-native CAPS platform with a working adjudication path" | `docs/POSITIONING.md:297` | The path works but doesn't price, and payment would pay billed. | "Cloud-native adjudication pipeline; pricing integration and payment controls in progress." |
| "Production Readiness: ✅ Multi-Tenant Isolation ✅ Security Hardening ✅ HIPAA Controls" | `CHANGELOG.md:1456` | Contradicted by §8.10. | Remove, or replace with a dated control-status table. |
| "v3.0.0 is production-ready … delivers … CMS-0057-F compliance" | `CHANGELOG.md:1466` | Later entries say "Zero GAPs is still not complete CMS-0057-F compliance". PA metrics missing. | "Implements CMS-0057-F PAS, CRD and Patient Access APIs; PA metrics and Payer-to-Payer in progress." |
| "Claims Scrubbing API … 95%+ first-pass rates" / `GET /scrubbing/v1/analytics/first-pass-rate` | `CHANGELOG.md:1371`; `src/site/portal/index.html:146`; `src/site/portal/api-docs.html:220,242` | Endpoint doesn't exist. No measurement backs the figure. | Remove the figure, or cite a measured study. |
| "6/6 pipeline stages real" (code comment) | `claims/Program.cs:377-383` | No pricing stage; COB secondary is a pend placeholder. | "Stages wired; pricing and secondary COB pending." |
| "Approval vs Denial Rate" / "Top Denial Reasons (CARC)" dashboards | `infrastructure/monitoring/grafana-claims-dashboard.json:39,224` | Metrics aren't produced. "Denial" is computed from technical workflow failures. | Retire the dashboard, or mark it "template — requires metrics instrumentation". |
| Mass Adjudication Runs: "Million Claim Challenge and production mass adjudication evidence" | `portal/Pages/MassAdjudicationRuns.razor:17` | Benchmark results only; no production mass-adjustment capability. | "Benchmark run results." |

The repo has already corrected several earlier overclaims (SOC 2, "HIPAA compliant", uptime SLA; see `docs/CHO-CLAIMS-AUDIT-REPORT.md` and `AUDIT/investor-readiness-2026-09.md`). That culture of self-correction is a real asset and should be applied to the items above.

---

## 10. Assumptions and open questions

### Assumptions
1. I assessed code at commit `00cc64c` by reading it, without running it. Behaviour I inferred but didn't execute is marked as such in the underlying notes. One example: the claims-publisher shell script appears to abort after the first claim under `set -e`.
2. I treated **Path A** (the Service Bus pipeline in claims-service) as the production path. It is the path the platform, portal and work queues are built around, and the path its own docs describe as the pipeline. Where Path B (Argo plus `/adjudicate`) behaves differently, I say so.
3. I treated MongoDB as the primary data store, because the benchmark and most features use it. Where Cosmos behaviour differs (version events are a no-op, adjustments throw, ERA envelopes are in memory), I flagged it.
4. Per the brief, vendor adapters (Availity, QNXT, Facets, HealthEdge, Stedi and others) are treated as project work and not scored. The Augment-mode contract counts as adequate in shape but is not implemented (the legacy result is always null).
5. Marketing pages labelled "demo" were still listed in §9 when the shipped product cannot produce the metric shown.
6. "Missing" means I searched for it and found nothing. It is possible a capability lives in a separate repository (e.g., the `cloudhealthoffice/claims-scrubber` image referenced at `infrastructure/argo-workflows/x12-837-claims-scrubbing.yaml:315`, whose source isn't in this repo). If so, it should be brought into scope for evaluation.

### Questions I'd ask in a pilot evaluation
1. Which adjudication path is the engine of record, and when will the other be retired?
2. Show one claim from 837 receipt to 835, with the dollars at every hop, priced from our real fee schedule. What did the payment run pay?
3. What is the plan for NCCI, MUE, groupers and PRICER content? Who licenses it, who loads it quarterly, and how is each claim stamped with the version used?
4. How do you handle Medicaid as payer of last resort, crossovers and TPL cost avoidance for our states?
5. Which states' encounter companion guides do you support today in X12, and what is your acceptance rate evidence?
6. When will internal services authenticate and take the tenant from a token? Will you show us a pen-test report?
7. Who is accountable for an AI-influenced denial? Show me the stored inputs, the model version and the human who signed off.
8. What is your SOC 2 Type II date, your tested RTO/RPO, and your 10-year retention design?
9. How do we produce a CDAG universe and a state claims extract in 10 business days?
10. How will our examiners be assigned work, measured and QA-sampled, and how do supervisors rebalance?
11. What is your prompt-pay clock definition (received date, clean claim), per state and for MA, and how is interest paid and reported?
12. Will you run in Augment mode beside our incumbent for 90 days and publish the variance by claim type?
13. What implementation, support and SLA commitments come with a production contract, given there are no production customers yet?
