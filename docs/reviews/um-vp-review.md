# Cloud Health Office: Review from the VP of Utilization Management's Chair

**Reviewer perspective:** VP of Utilization Management at a plan with 250K–750K lives across Medicaid managed care, CHIP and Medicare Advantage / D-SNP. I am accountable for CMS-0057-F.

**Basis:** A read of the code at commit `00cc64c`, as of 2 October 2026. Nothing was executed, so all findings are static. I judged what the code does, not what the README, website or architecture documents say it does.

**Status key**

| Status | Meaning |
|---|---|
| **Implemented** | Real logic that is saved and reachable, and is tested or clearly on the runtime path |
| **Partial** | Real logic, but key paths are missing |
| **Stub** | Scaffold, TODO, mock data or a fixed return |
| **Missing** | No code found |

**Path shorthand**

| Short form | Full path |
|---|---|
| `fhir/` | `src/services/fhir-service/` |
| `auth/` | `src/services/authorization-service/` |
| `portal/` | `src/portal/CloudHealthOffice.Portal/` |
| `pa-engine/` | `src/engines/CloudHealthOffice.PriorAuthRuleEngine/` |

**Regulatory note**

- Two sets of CMS-0057-F dates matter:
  - The "operational" prior-authorization provisions took effect **1 January 2026**. These are the decision timeframes, the specific denial reason, and public metrics, with the first metrics posting due **31 March 2026**.
  - The API provisions are due **1 January 2027** (or the rating period, plan year or rate year starting on or after that date).
- Both the January 2026 obligations and the 31 March 2026 metrics deadline have **already passed** as of this review.
- Where I'm not certain of a detail, the text says **"verify against rule text."**

---

## 1. Executive summary

### Can this plan meet CMS-0057-F on CHO?

**Not today, and not by 1 January 2027 if CHO is the only path.**

**What CHO genuinely has:**
- a real FHIR prior-authorization submission endpoint (PAS `$submit`) and status inquiry (`$inquire`);
- an authorization record shaped like an X12 278, with 72-hour and 7-day clock calculations;
- a "request more information" loop, where a pend opens a document request;
- well-engineered Payer-to-Payer and SMART security scaffolding.

**Provision by provision:**

| Provision | Status |
|---|---|
| Prior Authorization API (CRD/DTR/PAS) | **Partial**. PAS works in basic form. Coverage discovery (CRD) is thin and hard-codes Texas Medicaid STAR for every member. Documentation templates (DTR) is five static questionnaires with no CQL logic, no prepopulation and no SMART app. |
| Decision timeframes (in force since 1 Jan 2026) | **Partial / non-compliant**. The clock monitor fails in production, requests that came in through PAS are all timed as standard, and a "pend for information" restarts the clock. |
| Specific denial reason (in force since 1 Jan 2026) | **Partial**. Codes are invented strings, and there are no notices. |
| Public PA metrics (first posting was due 31 Mar 2026) | **Missing** |
| Patient Access: PA data to members | **Missing**. Patient, Coverage, Claim and Encounter are served from in-memory **mock data**. |
| Provider Access | **Partial**. Built as **opt-in**; the rule is **opt-out**. Bulk export returns empty files. |
| Payer-to-Payer | **Partial**. Substantial code, but it uses a proprietary format and runs on mock data. |

### Would I pilot it?

**Not as a UM system of record.** Three findings would stop me on their own:

1. **Automated decisions with no clinician in the loop.** The PAS engine can issue final **denials** with no clinician. It also **auto-approves any request whose submitter-stated cost is under $500** (`fhir/Services/PasAutoAdjudicator.cs:149-151`). It auto-approves any request under a "gold card" test that actually uses the **plan-wide** approval rate (`:211-227`; the summary endpoint ignores provider NPI, `auth/Controllers/AuthorizationsController.cs:542-557`).
2. **PAS records are saved with placeholder data.** The member is saved as "PAS Patient", line of business as "Commercial", and the decision status is overwritten to "Submitted". Save failures are silently ignored (`fhir/Controllers/PasController.cs:345-378`; `AuthorizationsController.cs:110`). A provider can be told "approved" while the plan's record says "pending", or holds no record at all.
3. **There is no UM workbench.** No reviewer screen, no work queue, no criteria, no medical director escalation, no peer-to-peer and no letters. The portal's authorization pages can't even display real records, because of a data-format mismatch (§2).

I would consider a **narrow technical pilot** of CHO's PAS and CRD front door feeding an existing UM platform. That would come after the auto-decision paths are made configurable to "approve or pend only" and the PAS-to-UM handoff is fixed.

### Top 5 strengths (in UM terms)

1. **Real PAS submit and inquire, with a working "need more information" loop.** A pend raises a document request, and receipt of documents moves the case back to review (`auth/Services/PendedAuthorizationRfaiCoordinator.cs:172-221`; `auth/Consumers/RfaiDocsReceivedConsumer.cs:178-198`).
2. **An authorization record shaped like the 278.** It has service lines, requested and approved units, A1–A4 decisions, append-only status history, 72-hour and 7-day clock math (`auth/Services/SlaWatchdogService.cs:14-22`), and a safe retention purge.
3. **The pieces are native and connected.** The PA rule engine, the benefit plans and the claims pipeline sit in one FHIR-native platform, so PA data *can* reach claims, members and other payers without vendor hops once it is wired.
4. **The Payer-to-Payer and Provider Access consent layers are thoughtfully built.** They have consent gates, coverage selection and durable data ingest. The SMART trust layer is fail-closed, with multiple issuers and tenant-bound tokens (`fhir/Services/Identity/*`, `fhir/Middleware/TenantMiddleware.cs:129-167`).
5. **The repo is candid about its gaps in places,** and the code is available to inspect. Examples:
   - `src/site/cms-0057f-compliance.html:1420`: "formal server-side profile validation (Inferno) has not yet been run";
   - `docs/interop/davinci.md` §13: external tests ran with CHO as the *client*, not the payer.

### Top 5 blockers (in UM terms)

1. **No human accountability for adverse decisions.** There are four automated denial paths. The API that records a denial accepts any valid token: no nurse or physician role, no credential captured, and the reviewer's name is free text.
2. **Clocks aren't compliance-grade.**
   - The background monitor throws an error every cycle in production, because it looks up cases without a tenant (`SlaWatchdogService.cs:58`; `auth/Repositories/AuthorizationRepositoryMongo.cs:26-34`).
   - Urgency from PAS is never saved.
   - A pend for information **restarts** the full clock.
   - There is no extension handling.
   - Stricter state clocks are configured but never read.
3. **No medical-necessity criteria and no governance.** There is no InterQual, MCG or NCD/LCD content. PA rules have no effective dates, no versions and no business-user screen. The only rules API is a seed endpoint.
4. **No notices, letters or public metrics.** There is no denial notice, Integrated Denial Notice or Medicaid notice of action, and no appeal-rights language. The Correspondence page calls an API that doesn't exist.
5. **The patient-facing and provider-facing APIs aren't real yet.**
   - Patient Access serves mock data and no PA data.
   - Provider Access uses the wrong consent model, and its bulk export is a stub.
   - Coverage discovery gives Texas Medicaid answers to MA and CHIP members.
   - No Inferno or Touchstone suite has ever run against CHO as the payer.
   - The public site says it has (§8).

---

## 2. UM features CHO has today

### 2.1 Intake

| Feature | Path | Primary user | What it does | Status | Evidence |
|---|---|---|---|---|---|
| FHIR PAS submit | `POST /fhir/r4/Claim/$submit` | Provider / EHR | Synchronous decision within a 12-second budget, or a pend (A4). Structural checks on the bundle only; no profile validation. Each submit creates a new authorization number. | **Implemented** (structural) | `fhir/Controllers/PasController.cs:56-169,431-503` |
| X12 278 inbound | Argo `x12-278-ingest` | EDI / intake | Parses a few header fields, then posts to a **placeholder URL** (`https://backend.api.local/claims/process-278`) with claim-shaped fields. No 278 response. | **Stub** | `infrastructure/argo-workflows/x12-278-ingest.yaml:66`; `containers/x12-parser/parse_x12.py:422-429` |
| Provider portal entry | `/authorizations` → Submit dialog | Provider, intake | Sends a flat payload with no service lines and missing required fields. The backend returns **400 on every submit**. | **Stub** (broken) | `portal/Services/IServices.cs:1402-1413`; `auth/Controllers/AuthorizationsController.cs:104-111` |
| Fax / OCR | none | Intake | Only a transmission-code field exists. | **Missing** | `auth/Models/Authorization.cs:508-513` |
| Phone entry | none | Intake | No intake-source field. The received time is forced to the server's current time, so it can't be back-dated. | **Missing** | `AuthorizationsController.cs:111` |
| Auth-required lookup | CRD; PA rule engine | Provider, intake | CRD calls the rule engine, but with **Texas / Medicaid / STAR hard-coded**. authorization-service references the engine but never calls it. | **Partial** | `fhir/Services/CrdService.cs:99-111` |
| Duplicate detection | none | Intake | None. | **Missing** | `AuthorizationsController.cs:97-124` |
| Eligibility and provider validation | PAS enrollment gate; exclusion check | Intake | PAS checks enrollment and OIG exclusion only when the NPI is supplied as an identifier (often it isn't). Manual intake does neither. | **Partial** | `PasAutoAdjudicator.cs:76-104`; `PasController.cs:88-114` |

### 2.2 Clinical review

| Feature | Path | Primary user | What it does | Status | Evidence |
|---|---|---|---|---|---|
| Authorizations list | `/authorizations` | UM coordinator | Search grid. The backend sends status as text; the portal expects a number, so real responses **fail to load and the page silently shows an empty list**. Detail lookups use the wrong ID and return 404. | **Partial** (broken against the real backend) | `portal/Pages/Authorizations.razor:257-296`; `IServices.cs:1357,1365` |
| Authorization detail | Dialog | UM nurse | Read-only tabs. **No approve, deny, modify or pend buttons.** "Print Approval", Download and Preview do nothing. | **Partial** | `portal/Pages/AuthorizationDetailsDialog.razor:39-258` |
| Decision API | `PUT /{id}/status`, `POST /{id}/response` | Backend only | Records A1–A4 with history. **Any valid token can deny.** Permission policies are defined but never applied, and any status change is allowed (a denied case can be reopened). | **Partial** | `AuthorizationsController.cs:362-496`; `auth/Program.cs:36-51` |
| Clinical attachments | attachment-service | UM nurse | Files are stored in blob storage with a hash. **The portal's attachment routes don't match the service**, and the tenant is taken from the form body. | **Partial** | `src/services/attachment-service/Controllers/AttachmentsController.cs:36-251` |
| Pend for information | rfai-service | Intake, UM nurse | Pend → document request → documents received → back to review. Idempotent. rfai-service requires **no login**. | **Implemented** (backend) | `auth/Services/PendedAuthorizationRfaiCoordinator.cs`; `src/services/rfai-service/Program.cs:107-111` |
| Criteria (InterQual, MCG, internal) | none | UM nurse | Zero hits. The rule engine decides whether PA is *required*, not whether the service is medically necessary. | **Missing** | |
| Nurse → medical director escalation; peer-to-peer | none | UM nurse, medical director | No roles, statuses or scheduling. | **Missing** | |
| Partial approvals and modified units/dates | API | UM nurse | An A2 (modified) status exists, but approved units and dates are header-level only. Line status is never set, so the claims-side check rejects procedure-specific matches. | **Partial** | `AuthorizationsController.cs:213-214,450-470` |
| Reviewer roles | Portal | All | One role, `UMCoordinator`. No nurse, medical director, intake or supervisor roles. `authorizations:decide` is defined but never enforced. | **Partial** | `portal/Services/UserContextService.cs:346-353` |

### 2.3 Case types

| Case type | Status | Notes |
|---|---|---|
| Outpatient and ancillary | **Partial** | The generic record works. |
| Inpatient admission and concurrent review | **Stub** | A "Concurrent" type value exists. There is no length of stay, next review date, level of care or discharge field (`auth/Models/Authorization.cs:547-573`). |
| Post-acute (SNF, IRF, LTACH, home health) | **Missing** | No case-type or level-of-care model. |
| DME | **Partial** | Texas seed rules pend all codes starting K, A or E. That sweeps in ambulance and supplies, and the "cost > $500" condition is not implemented (`pa-engine/SeedRules/TxMedicaidSeedRules.cs:148-166`). |
| Pharmacy-on-medical / buy-and-bill drugs | **Partial** | NDC field exists. Drug-exclusion auto-deny exists (`auth/Backends/ChoAuthorizationBackend.cs:54-69`). No drug criteria. |
| Behavioral health | **Missing** | No BH-specific handling. |
| LTSS / HCBS | **Missing** | Only in seed descriptions. |

### 2.4 Decisions and notices

| Feature | Status | Evidence |
|---|---|---|
| Approve, deny or pend with reasons | **Partial** | A denial reason code (max 10 characters, not validated against any code set) plus free text. PAS uses invented codes such as `NOT_COVERED` (`fhir/Services/PasResponseBuilder.cs:45-65`). |
| Specific denial reason in the PAS response | **Partial** | Present on denials, but there is no X12 review-action or reason code, and no criteria citation. |
| Member and provider notice generation | **Missing** | The Correspondence page calls `/correspondence/*`, which doesn't exist. Its templates are hard-coded HTML (`portal/Pages/Correspondence.razor:268-339`). |
| Appeal-rights language, translations, taglines | **Missing** | |
| Extension handling | **Missing** | No fields or endpoint. |

### 2.5 Auth-to-claim

| Feature | Status | Evidence |
|---|---|---|
| Auth matching in the main claims pipeline | **Partial / fails open** | Checked only for **institutional + Medicaid + inpatient (POS 21)**, using the first line's code. A missing authorization (404), an error or a timeout all count as a **pass**. A **made-up authorization number gets through.** `src/services/claims-service/Services/Adjudication/Stages/BenefitCalculationStage.cs:326-359`; `src/services/claims-service/Services/Resolution/HttpAuthorizationValidationClient.cs:69-96` |
| Auth matching in the second adjudication path | **Partial** | Denies only when the authorization number is **empty**, and never validates it. `src/services/benefit-plan-service/Controllers/AdjudicationController.cs:441-442` |
| Validate endpoint | **Partial** | No login required. No member check. Checks the expiration date but not the approved through-date. A blank NPI passes. `AuthorizationsController.cs:170-246` |
| Unit and date consumption | **Missing** | Approved units are returned but never decremented. |

### 2.6 Appeals and reconsiderations

| Feature | Status | Evidence |
|---|---|---|
| Link from a PA denial to an appeal | **Missing** | An appeal **requires a ClaimId** and has no authorization field (`src/services/appeals-service/Controllers/AppealsController.cs:887-891`; `Models/Appeal.cs:46-48`). Pre-service appeals can't be recorded. |
| Appeal clock | **Incorrect** | 30 days if urgent, otherwise 60 (`AppealsController.cs:93`). Expedited appeals should be 72 hours, and Medicaid and MA standard pre-service appeals 30 days. The portal displays the correct table; the backend doesn't follow it (`portal/Pages/Appeals.razor:242-269`). |
| Effectuation of overturns | **Missing** | The appeal status event is published, but **nothing listens to it**. |

### 2.7 Delegated UM

| Feature | Status | Evidence |
|---|---|---|
| Ingest decisions from delegated vendors (eviCore, Carelon, etc.) | **Missing** | No code. A vendor could post to the API, but status would be forced to "Submitted". |
| Delegation oversight reporting and file audits | **Missing** | |
| External core-system adapter (QNXT) | **Stub** | Throws `NotImplementedException` (`auth/Backends/QnxtAuthorizationBackend.cs:42-54`). |

### 2.8 AI-assisted review

| Question | Finding |
|---|---|
| Is AI used in the PA path? | **No.** The PAS adjudicator is purely rules-based. The only AI (Claude) is the claims examiner, which reviews NCCI claim pends and is advisory only. The TMPPM tool's README claims "Claude API" parsing, but the files don't exist; extraction is regex. |
| Are adverse decisions automated? | **Yes, by rules.** The rule set is: provider exclusion, enrollment gate, configured auto-deny list, rule-engine deny, and drug exclusion. All return final denials synchronously with no human (`PasController.cs:88-114`; `PasAutoAdjudicator.cs:76-125,159-199`; `ChoAuthorizationBackend.cs:54-67`). |
| Are approvals automated? | **Yes.** Auto-approve list; plan-wide "gold card"; **any request with a stated cost under $500**; and a STARKids rule that auto-approves **every service for members 21 and under** when a date of birth is present (`pa-engine/SeedRules/TxMedicaidSeedRules.cs:226-243`; `pa-engine/Rules/Platform/MemberAgeLimitRule.cs:49-53`). |
| Is the decision explainable after the fact? | **No.** The rule engine's firing rule and rule-set key are discarded when the case is saved (`PasController.cs:345-365`), and rules are unversioned. |

Most of these automated denials are administrative (not enrolled, excluded provider, not covered) rather than medical-necessity denials. Even so, there is no reviewer of record and no notice. The deny-capable rule path also has **no setting that limits the engine to "approve or pend only"**.

---

## 3. Needed but not built

| Area | Gap | Status |
|---|---|---|
| **Work queues** | UM work queues with clinical priority and **routing based on the regulatory clock**, assignment, SLA timers, aging, staffing and productivity views | **Missing**. The only queue is the claims queue, and the authorization record has no assigned-to field. |
| **Clock monitoring** | At-risk and breached case alerts and screens | **Partial**. The `sla/at-risk` API works per request, but **it accepts a `tenantId` query parameter that lets any caller read another plan's open cases** (`AuthorizationsController.cs:563-570`). The background monitor fails. The alternative Argo watchdog shares files across pods incorrectly, and nothing consumes its topic (`infrastructure/argo-workflows/sla-deadline-watchdog.yaml`). |
| **PA requirement configuration** | Which services need PA by plan, product and line of business, effective-dated, maintained by business users | **Stub**. The engine resolves by state, line of business and program only, with no plan or product. There are no effective dates and no versions. The only API is `POST seed-platform`, with no login (`src/services/benefit-plan-service/Controllers/PriorAuthRulesController.cs:9-61`). A tenant override **replaces** all platform rules instead of merging (`pa-engine/Services/PriorAuthRuleEngineService.cs:218-232`). Benefit-plan "PA required" flags are display-only. CRD code lists live in a 6-hour memory cache. |
| **Gold carding / PA exemptions** | State-mandated programs (e.g., Texas HB 3459) | **Partial / incorrect**. The engine rule **never fires**, because no provider-history service is registered. It is provider-wide rather than per service and uses a rolling 180 days rather than fixed evaluation periods. It cites the wrong bill ("HB 3229"). There are no notices or rescission process. The PAS gold card uses the plan-wide rate. Whether HB 3459 applies to Texas Medicaid/CHIP MCOs needs **confirmation by counsel**. |
| **Continuity of care** | MA 90-day new-enrollee transition (42 CFR 422.112(b)(8)); Medicaid transition of care (42 CFR 438.62); honoring a prior payer's active authorizations | **Missing**. Payer-to-Payer stores incoming PA records but never projects them into UM. |
| **Criteria governance** | Annual UM committee review, criteria versioning, public posting of MA internal coverage criteria (42 CFR 422.101(b)(6)) | **Missing** |
| **Letters** | Template management with state- and line-of-business-specific language, translations, taglines, appeal rights; MA Integrated Denial Notice; Medicaid notice of action | **Missing** |
| **Line of business** | CHIP, D-SNP and MMP values; state and plan fields on the authorization | **Missing** (`auth/Models/Authorization.cs:639-648`) |
| **Delegated UM oversight** | Vendor decision intake, delegated-entity attribution, oversight reporting, file sampling | **Missing** |
| **Reviewer credentialing** | Reviewer ID, license and specialty saved with each decision; physician-only adverse determinations | **Missing** |
| **Concurrent review** | Length of stay, level of care, next review date, discharge planning | **Missing** |

---

## 4. CMS-0057-F, provision by provision

**Impacted payers** (abbreviations used in this section):
- **MA**: Medicare Advantage organizations
- **Medicaid FFS**: state Medicaid fee-for-service programs
- **Medicaid MC**: Medicaid managed care plans
- **CHIP FFS** and **CHIP MC**: CHIP fee-for-service programs and CHIP managed care entities
- **QHP**: Qualified Health Plan issuers on the Federally-facilitated Exchanges

### 4a. Prior Authorization API (Da Vinci CRD, DTR, PAS)

**Rule requirement:** a FHIR API that tells providers whether PA is required and what documentation is needed, and that supports submitting the request and receiving the decision. The decision must be approved, denied (with a specific reason) or more information needed, with the related date or circumstance.

**Applies to:** all impacted payers.

**Compliance date:** 1 January 2027 (MA: calendar year; Medicaid MC and CHIP MC: rating periods beginning on or after; QHP: plan years beginning on or after).

| Component | What CHO has | Status | Evidence | What remains for the plan |
|---|---|---|---|---|
| **CRD discovery** | `GET /cds-services` | **Implemented**, but probably unreachable without a tenant header in the full pipeline. No CRD version extension. | `fhir/Controllers/CrdController.cs:14,73-78`; `fhir/Middleware/TenantMiddleware.cs:109-120` | Confirm EHR reachability |
| **CRD hooks** | `order-select`, `order-sign` | **Partial** (2 hooks). Missing `appointment-book`, `encounter-start`, `encounter-discharge`, `order-dispatch`. | `CrdController.cs:22-57` | Add hooks; vendor/EHR testing |
| **CRD cards** | Auth-required, doc-required, no-auth | **Partial / non-conformant**. **No coverage-information extension** (the core CRD STU2 response). Invented topic codes. The DTR launch link points to a page that doesn't exist and carries no context. Prefetch data is ignored. | `fhir/Services/CrdService.cs:288-380` | Product build |
| **CRD coverage logic** | Calls the rule engine | **Partial**. **Hard-codes Texas / Medicaid / STAR / $0 cost / no diagnoses** for every member, and never checks eligibility. | `CrdService.cs:99-111` | Product fix plus rule content per line of business and state |
| **DTR package** | `$questionnaire-package` | **Partial**. Wrong operation signature. Returns the Questionnaire only, with no Library or ValueSet. Not advertised in the server's capability statement (CapabilityStatement). | `fhir/Controllers/DtrController.cs:185-218`; `fhir/Services/DtrService.cs:256-278` | Product build |
| **DTR content** | 5 demo questionnaires | **Stub**. No canonical URLs, CQL, launch context or prepopulation expressions. | `DtrService.cs:323-356` | Plan or criteria-vendor content |
| **DTR CQL and prepopulation** | none | **Missing** | | Product build or vendor |
| **DTR SMART app / native EHR support** | none | **Missing** | | Product build or vendor |
| **QuestionnaireResponse saved** | `POST` | **Partial**. Search and update read memory only. Not linked to the PA. | `DtrService.cs:153-252` | Product fix |
| **PAS `$submit`** | Synchronous decision or pend | **Implemented** (structural only). **No profile validation** (no FHIR validator package). Header says PAS 2.1.0; docs say 2.2.x. | `PasController.cs:16,56-169`; `fhir/fhir-service.csproj:41-55` | Profile validation; IG version decision |
| **PAS ClaimResponse** | Approved, denied, pended (A4) | **Partial**. No A1 or A3 review action on approve or deny. Review action is at the wrong level. **No line-level decisions** (no partial approval). The response bundle lacks the required supporting resources. Approval period is always today to +1 year. | `fhir/Services/PasResponseBuilder.cs:23-98`; `PasAutoAdjudicator.cs:357-366` | Product fix |
| **PAS `$inquire`** | Live lookup | **Implemented**. Looks up the authorization number in the wrong field first; operation name mismatch (`Claim-inquire` vs `Claim-inquiry`). | `fhir/Services/PriorAuthorizationInquiryService.cs:137-217` | Minor fixes |
| **PAS pended-response notification** | Provider must poll | **Missing**. No FHIR Subscriptions. | | Product build |
| **PAS update / cancel** | none | **Missing**. Every submit mints a new authorization. | `PasController.cs:135,393-394` | Product build |
| **PAS → UM record** | POST to authorization-service | **Partial / likely broken**. Placeholder demographics. Line of business "Commercial". Urgency not mapped. No bearer token on a service that requires one (likely a 401). Field-length checks will reject longer codes. **Decision overwritten to "Submitted".** Failures swallowed. The acceptance test stubs this step as unreachable. | `PasController.cs:342-379`; `AuthorizationsController.cs:110`; `tests/Cms0057Acceptance.Tests/Scenarios/PasSubmitTests.cs:27-30` | **Product fix — top priority** |
| **X12 278 mapping** | Legacy TypeScript only | **Missing** on the runtime path. | `src/fhir/mapping/x12-to-fhir.ts` (not wired) | See 4h |
| **Conformance evidence** | Internal xUnit suite (21 scenarios, 362 passed, mocked); 3 external scenarios with **CHO as client** against the HL7 reference payer | **No payer-side conformance evidence.** No Inferno run; no Touchstone artifacts. | `docs/interop/davinci.md` §3, §13; `src/site/insights/cms-0057-f/cms0057-public-evidence.json` | Run Da Vinci CRD/DTR/PAS test kits against CHO as the server |

### 4b. Decision timeframes (operational, in force since 1 January 2026)

**Rule requirement:**
- **72 hours** for expedited (urgent) requests and **7 calendar days** for standard requests.
- Applies to **MA, Medicaid FFS, Medicaid MC, CHIP FFS and CHIP MC**.
- **QHP issuers on the FFEs are not subject to the new timeframes.**
- Medicaid permits an extension of up to 14 days in defined circumstances. *Verify the exact extension conditions per program against rule text.*
- Stricter state timeframes still apply.

| Requirement | CHO status | Evidence |
|---|---|---|
| Correct durations | **Implemented** (hard-coded 72h / 168h) | `SlaWatchdogService.cs:14-22` |
| Clock starts at receipt | **Partial**. Starts at the server receipt time; can't be back-dated for fax or phone. | `AuthorizationsController.cs:111` |
| Urgency captured | **Partial**. A one-character field, **never set by PAS**, so every PAS request runs on the 7-day clock. | `PasController.cs:346-371` |
| Pends and requests for information | **Non-compliant**. The clock keeps running while pended, then **restarts the full window** when documents arrive. CMS timeframes have no "stop the clock" for information requests; only a formal extension with notice is allowed (verify per program). | `auth/Consumers/RfaiDocsReceivedConsumer.cs:181`; `auth/Models/AuthorizationsSummaryCalculator.cs:12-17` |
| Extensions | **Missing** | |
| Stricter state rules | **Not wired**. State config fields exist (e.g., Florida 72h / 5 days) but authorization-service never reads them. | `src/services/reference-data-service/Models/StateComplianceConfig.cs:42-53,86-87` |
| Alerts before breach | **Partial / non-functional**. The background monitor fails in production (tenant lookup throws). No notification or screen. | `SlaWatchdogService.cs:58`; `AuthorizationRepositoryMongo.cs:26-34` |

**What the plan would have to do:** run clocks elsewhere until CHO's are fixed. The plan has been exposed since 1 January 2026.

### 4c. Specific reason for denial (operational, in force since 1 January 2026)

**Rule requirement:** a specific reason for any PA denial, whatever channel the request came through. **Applies to all impacted payers**; *verify QHP applicability and notice mechanics against rule text*. The reason travels through the PA API and the payer's existing notice processes.

**CHO status: Partial.**
- A reason code and free text are stored.
- PAS returns invented codes in an unrelated code system.
- Manual denials carry free text only, with no validated code set and no criteria citation.
- **No notices are generated**, so the reason never reaches the member or provider by letter.

**What remains for the plan:** a denial-reason code set, structured capture at decision time, notice generation, and mapping into the PAS response.

### 4d. Public reporting of PA metrics (first posting was due 31 March 2026, annually after)

**Rule requirement:** post annually on the payer's website:
- the list of items and services that require PA;
- percent of standard requests approved, denied, and approved after appeal;
- percent for which the timeframe was extended and then approved;
- percent of expedited requests approved and denied;
- average and median time from submission to decision, for standard and expedited requests separately.

Reported at the contract level (MA), state level (Medicaid FFS and CHIP FFS), plan level (Medicaid MC and CHIP MC) and issuer level (QHP). **Applies to all impacted payers.** *Verify the drug exclusion and level-of-aggregation details against rule text.*

**CHO status: Missing.**
- A basic summary exists: counts, approval rate and average review days (`auth/Models/AuthorizationDtos.cs:121-132`). On MongoDB it omits even average turnaround.
- No standard vs. expedited split, no median, no join to appeals, no extension flag, no PA-required list, no annual job and no publication path.
- `docs/compliance/CMS-0057-F-PRIOR-AUTH-METRICS-TEMPLATE.md` is blank.
- Yet the acceptance manifest marks METRICS-01 "PASSABLE" on the strength of one turnaround calculation.

**What the plan would have to do:** produce the 2025 metrics outside CHO (they were due 31 March 2026). CHO needs a metrics extract built on corrected clock data.

### 4e. Patient Access API: PA information

**Rule requirement:** make PA information available to members through the Patient Access API: status, dates, decision and denial reason, excluding drugs.
- Available **no later than one business day** after the request is received or the status changes.
- Kept available while the authorization is active and for **at least one year** after the last status change.

**Applies to:** MA, Medicaid FFS and MC, CHIP FFS and MC, and QHP. **Due 1 January 2027.**

**CHO status: Missing.**
- **No member-facing PA read** (no PDex prior-authorization resource).
- Patient, Coverage, Claim and Encounter are **hard-wired to in-memory mock data** (`fhir/Program.cs:109-116`). Only ExplanationOfBenefit is live, and it has no CARIN Blue Button profile.
- `$inquire` is provider-oriented.
- Retention of PA records (6-year default, 1-year floor) **is** implemented (`auth/Services/Retention/PriorAuthorizationRetentionPolicy.cs:54-64`).
- **Production member login does not work.** smart-auth-service returns `false` outside dev mode and uses development signing certificates (`src/services/smart-auth-service/Controllers/AccountController.cs:110-121`; `src/services/smart-auth-service/Program.cs:111-114`).
- Patient binding is enforced only on reads of the Patient resource itself. **A member token can read another member's Coverage, Claim or EOB by ID** (`fhir/Middleware/SmartScopeEnforcementMiddleware.cs:136-140`).

### 4f. Provider Access API

**Rule requirement:**
- In-network providers can retrieve, for their attributed patients: claims and encounters (without cost-sharing or remittance), USCDI clinical data, and PA information.
- **Members may opt out; the default is that data is shared.**
- Data must be available within one business day of receipt.
- Bulk access is required.
- Claims back to dates of service on or after 1 January 2016; *verify against rule text*.

**Applies to:** MA, Medicaid FFS and MC, CHIP FFS and MC, and QHP. **Due 1 January 2027.**

| Element | CHO status | Evidence |
|---|---|---|
| Attribution | **Partial**. Static panels from a configuration file. No Group resource or `$davinci-data-export`. | `fhir/Services/ProviderAccess/ProviderAttribution.cs:28-84` |
| Opt-out | **Built backwards**. Data is shared only when an active opt-in consent exists. There is no opt-out record type. The acceptance test only checks that a revoked consent is inactive. | `fhir/Services/ProviderAccess/ProviderAccessAuthorizationService.cs:177-188`; `fhir/Services/Consent/ConsentEvaluator.cs:75-92` |
| Provider authentication and network check | **Partial**. Any token with a user/system scope counts as a provider. No NPI or network check. | `fhir/Services/ProviderAccess/ProviderAccessAuthorizationFilter.cs:172-201` |
| Bulk `$export` | **Stub**. Marks the job complete with 0 records and links to files that don't exist. Accepts any group ID. | `fhir/Services/BulkExportService.cs:24-70` |
| PA data included | **Missing** | |

### 4g. Payer-to-Payer API

**Rule requirement:**
- With the member's **opt-in**, exchange claims and encounters (without remittance or cost-sharing), USCDI data and PA information (excluding drugs; *verify which PA statuses are included*) for **5 years** with a new payer.
- Request data within **one week** of the start of coverage.
- Exchange with concurrent payers **at least quarterly**.

**Applies to:** MA, Medicaid MC, CHIP MC and QHP. *Medicaid FFS and CHIP FFS have specific provisions; verify against rule text.* **Due 1 January 2027.**

| Element | CHO status | Evidence |
|---|---|---|
| Opt-in consent | **Partial**. Server-side gate. **The consent service requires no login.** No member-facing capture or one-week workflow. | `src/services/consent-service/Program.cs:167-169` |
| Member match | **Partial / privacy risk**. Proprietary JSON, not the HRex Parameters format. **No consent check.** Last name plus date of birth counts as a strong match. Returns the full Patient and Coverage. Reachable with a *patient* token, so one member could look up another. | `fhir/Controllers/PayerToPayerMemberMatchController.cs:19-107`; `fhir/Services/PayerToPayer/MemberMatchPolicy.cs:77-81` |
| Outbound and inbound exchange | **Partial**. Proprietary operations, so it **can only talk to another CHO instance**. The outbound credential provider isn't configured. Exchange state is held in memory. | `fhir/Controllers/PayerToPayerOutboundController.cs`; `fhir/Program.cs:211-219` |
| Content | **Partial**. Patient, Coverage and EOBs built from **mock payments**, including payment amounts that the rule excludes. **No PA data.** The 5-year filter uses payment date rather than service date. | `fhir/Services/PayerToPayer/PayerToPayerExportBuilder.cs:23-60`; `fhir/Services/PayerToPayer/PayerToPayerExportPolicy.cs:28-45` |
| Concurrent quarterly exchange | **Missing** | |
| Use of received data | **Partial**. Clinical data is promoted. Claims and PA from the prior payer are **not** made available to members or providers, and not to UM. | `fhir/Program.cs:230-261` |

### 4h. Cross-cutting requirements

| Item | CHO status | Notes |
|---|---|---|
| SMART on FHIR / OAuth 2.0 (resource server) | **Implemented** | Multi-issuer, fail-closed. SMART v1 scopes only; v2 scopes would be denied. |
| SMART authorization server | **Stub** for production | Development certificates; dev-mode login; seeded client secrets; an unauthenticated `/launch` endpoint. |
| UDAP | **Missing** | |
| IG versions | **Inconsistent** | FHIR R4 4.0.1. PAS 2.1.0 in code vs 2.2.1 in interop pins. CRD and DTR 2.2.x in pins only. **No US Core profile claimed** for clinical data. CARIN BB, PDex and HRex not cited. Plan-Net 1.1.0 / US Core 6.1.0 in provider projections. *Confirm the required versions against the rule and current CMS guidance (verify against rule text).* |
| Public Provider Directory | **Partial** | Requires a login and a tenant, but CMS requires public access. Location data comes from NPPES. |
| API usage metrics to CMS | **Missing** | Annual Patient Access usage reporting. *Applies to MA, Medicaid and CHIP; verify QHP applicability against rule text.* |
| X12 278 relationship | **Missing** on the runtime path | CMS announced **enforcement discretion** allowing a FHIR-only PA API without the X12 278 (*verify current status against CMS guidance*). Plans that still receive 278s through clearinghouses need a 278 path, which CHO lacks. |
| Tenant isolation | **Partial — audit risk** | fhir-service: the token wins and a conflicting header returns 403 (good). **authorization-service, rfai-service, consent-service and others fall back to the `X-Tenant-ID` header**, in authorization-service even when authenticated. **`sla/at-risk?tenantId=` reads across tenants.** The PAS rule engine's tenant comes from a tag inside the submitted bundle. |
| Access audit logging | **Partial** | Log lines only; no FHIR AuditEvent store. |
| Rate limiting | **Missing** | |

### 4i. Coexistence with third-party vendors

| Scenario | Support |
|---|---|
| Plan uses a vendor for DTR/PAS and CHO supplies the UM decision engine | **Partial**. Seams exist inside CHO (`IPasAutoAdjudicator`, `IPriorAuthRuleEngine`), but there is no external API contract for "decide this case" apart from posting to `api/authorizations`. |
| CHO's PAS front door feeding the plan's existing UM platform (e.g., QNXT, GuidingCare) | **Stub**. `QnxtAuthorizationBackend` throws NotImplemented. The site's QNXT scoping page is honestly worded as statement-of-work-dependent (`src/site/services/qnxt-cms-0057-f-adapter.html:79-94`). |
| CHO supplying metrics reporting only | **Missing** (no metrics). |
| CHO supplying Provider Access / Payer-to-Payer only | **Partial**. Plausible once the adapters are real and consent is corrected. Payer-to-Payer interoperates only with CHO today. |
| CDS Hooks proxy or fan-out to a vendor CRD | **Missing** |

### 4j. Compliance matrix

| Provision | Applies to | Compliance date | CHO component | Status | Plan's remaining work |
|---|---|---|---|---|---|
| PA API: CRD | All impacted payers | 1 Jan 2027* | `fhir/Services/CrdService.cs`, `CrdController.cs` | **Partial** | Coverage-information extension, more hooks, per-line-of-business rules (remove the Texas hard-code), DTR launch, conformance testing |
| PA API: DTR | All impacted payers | 1 Jan 2027* | `fhir/Services/DtrService.cs` | **Partial / Stub** | CQL, prepopulation, SMART app or vendor, questionnaire content |
| PA API: PAS | All impacted payers | 1 Jan 2027* | `fhir/Controllers/PasController.cs` | **Partial** | Fix the UM handoff, profile validation, line decisions, subscriptions, update/cancel, remove ungated auto-deny, conformance testing |
| Decision timeframes (72h / 7 days) | MA, Medicaid FFS and MC, CHIP FFS and MC (not QHP) | **1 Jan 2026 (in force)** | `auth/Services/SlaWatchdogService.cs` | **Partial / non-compliant** | Fix the monitor, map urgency, correct pend handling, add extensions and state overrides, alerting |
| Specific denial reason | All impacted payers (verify QHP) | **1 Jan 2026 (in force)** | Authorization record; `PasResponseBuilder.cs` | **Partial** | Coded reasons, notices, criteria citation |
| Public PA metrics | All impacted payers | **First posting 31 Mar 2026 (past due)**, annually after | Authorization summary endpoint | **Missing** | Build the metrics extract and publication |
| Patient Access: PA data | MA, Medicaid, CHIP, QHP | 1 Jan 2027* | none | **Missing** | PA resource, real adapters, production login, patient binding |
| Patient Access API usage reporting to CMS | MA, Medicaid, CHIP (verify QHP) | Annual (verify first reporting year against rule text) | none | **Missing** | Usage metrics |
| Provider Access API | MA, Medicaid FFS and MC, CHIP FFS and MC, QHP | 1 Jan 2027* | `fhir/Services/ProviderAccess/*`, `BulkExportService.cs` | **Partial** | Opt-out model, attribution feed, real bulk export, PA data, provider verification |
| Payer-to-Payer API | MA, Medicaid MC, CHIP MC, QHP (FFS: verify) | 1 Jan 2027* | `fhir/Controllers/PayerToPayer*` | **Partial** | HRex/PDex conformance, consent capture, PA data, concurrent quarterly exchange, real data |
| Provider Directory (public) | MA, Medicaid, CHIP (existing rule) | In force | `fhir/Controllers/ProviderDirectoryController.cs` | **Partial** | Make public; Plan-Net completeness |
| Security (SMART / OAuth) | All APIs | 1 Jan 2027* | `fhir/Services/Identity/*`, `src/services/smart-auth-service` | **Partial** | Production IdP, patient binding on all reads, UDAP for payer-to-payer (verify requirement) |

\* Or the rating period, plan year or rate year starting on or after that date.

---

## 5. End-to-end PA trace and monitoring

| Hop | What happens | Saved? | Timestamped? | Visible in portal? | Alerted? | Replayable? |
|---|---|---|---|---|---|---|
| 1. EHR order → **CRD** | `order-select` / `order-sign` card answered with **Texas STAR rules** regardless of member | No | No | No | No | No |
| 2. **DTR** | Static questionnaire fetched; provider completes it; response saved (memory or MongoDB) | Partly | Yes | No | No | No |
| 3. **PAS `$submit`** | Synchronous rules decision or pend; latency histogram recorded | Response only | Yes | No | No | No |
| 4. **Intake into UM record** | Fire-and-forget POST with placeholder data. Likely rejected (no token). If accepted, status forced to "Submitted". | **Unreliable** | Received time = server time | Portal can't load records (format mismatch) | No | No |
| 5. **Auto-decision or clinical review** | Auto-decision already returned to the provider. Clinical review only by direct API call; no workbench. | Status history (actor unverified) | Yes | No | No | No |
| 6. **Pend for information** | Document request raised; received documents move the case to review and **restart the clock** | Yes | Yes | No (Correspondence page is a mock) | No | Idempotent |
| 7. **Decision → response / notification** | Provider must poll `$inquire`. No Subscriptions, no 278 response, **no letters**. | Yes | Yes | Read-only | No | n/a |
| 8. **Member access via Patient Access** | **Not available** | — | — | — | — | — |
| 9. **Authorization used in claims** | Checked only for Medicaid inpatient; fails open. No unit consumption. | No | No | Claims queue "Missing auth" pend only | No | No |
| 10. **Appeal if denied** | **Cannot record a PA appeal** (ClaimId required) | — | — | — | — | — |

### The UM control tower a VP needs

| Panel | Content | Exists today | Build |
|---|---|---|---|
| **Open requests by clock status** | On track / due within 24h / at risk / breached; by urgency, line of business, state, case type | `sla/at-risk` API (unsafe tenant parameter); no screen | A stored due date per case; fixed monitor; screen. **M** |
| **Breach forecast** | Cases that will breach at current staffing | None | Queue and staffing model. **M** |
| **Volume by channel** | PAS, 278, portal, fax, phone; auto vs. manual | None (no intake-source field) | Intake-source field; metrics. **S** |
| **Auto-decision rates** | Auto-approve and auto-deny by rule, with **reasons** | None (firing rule discarded) | Save the rule decision. **S** |
| **Denial and overturn rates** | By service, reason, reviewer type; appeal overturns | Approval rate only | Coded reasons; appeal linkage. **M** |
| **Turnaround by reviewer type** | Intake / nurse / medical director; mean and median | None (no reviewer identity) | Roles, assignment, identity. **M** |
| **CMS metrics view** | The public-reporting set, ready to publish | None | Metrics extract. **M** |

---

## 6. Other audit and regulatory readiness for UM

| Area | Requirement | Status | Likely audit finding if live today |
|---|---|---|---|
| **MA coverage criteria** (CMS-4201-F; 42 CFR 422.101(b)(6)) | Follow Traditional Medicare NCDs/LCDs; internal criteria must be evidence-based and publicly accessible | **Missing**: no criteria content or posting | Coverage criteria not shown to comply with NCD/LCD; internal criteria not posted |
| **UM committee** (42 CFR 422.137) | Annual review of policies and criteria | **Missing**: no governance records; rules unversioned | No evidence of UM committee oversight |
| **Physician review of adverse decisions** (42 CFR 422.566(d); Medicaid 438.210(b)(3)) | A qualified professional makes adverse medical-necessity decisions | **Missing**: no role or credential gate; any token can deny; automated denials exist | Adverse determinations issued without appropriate review |
| **AI and algorithms in MA decisions** (CMS FAQ, Feb 2024) | Decisions rest on the individual's circumstances | **Partial**: no AI in PA, but rule-based auto-decisions (e.g., $500 threshold, plan-wide gold card, age 21 and under) don't consider the individual's clinical situation | Automated approvals and denials not individualized; unreviewable logic |
| **MA transition of care** (422.112(b)(8)) | 90-day transition for new enrollees in an active course of treatment | **Missing** | Transition protections not applied |
| **Medicaid timeframes and notices** (42 CFR 438.210(d), 438.404) | 72h / 7 days (verify state contract), notice of adverse benefit determination with required content | **Partial / Missing** | Late decisions; missing or deficient notices |
| **Medicaid appeals** (438.408, 438.420) | 30-day standard, 72-hour expedited, State Fair Hearing, continuation of benefits | **Incorrect / Missing** | Appeal clocks wrong; no State Fair Hearing or continuation-of-benefits handling |
| **NCQA UM standards** (if accredited) | Criteria, timeliness, denial file review, staff qualifications, delegation oversight | **Missing** for most elements | Denial-file review failures; no delegation oversight |
| **CMS program audit (ODAG)** | Organization determination universes, timeliness evidence, case files on a short deadline | **Missing**: no universe extract; clock data unreliable; no notices to sample | Universe not produced or fails validation; timeliness can't be shown |
| **EQRO / state reporting** | State-specific PA and timeliness reports | **Missing** | Reports produced by hand, or not at all |
| **Decision reconstruction** | Criteria version, documents, reviewer credentials and AI involvement at decision time | **Partial**: documents attach; **rule decision discarded**; rules unversioned; reviewer is free text; no criteria | Cannot show the basis for sampled decisions |
| **Immutable audit trail and retention** | Tamper-evident; 10 years for MA (422.504(d)); Medicaid per 438.3(u) | **Partial**: history lives inside a mutable document; cancel writes no history; 6-year default; no legal hold | Audit trail not reliable; retention below requirement |
| **Security** | RBAC, tenant isolation | **Partial**: header-based tenant fallback; cross-tenant `sla/at-risk`; unauthenticated rfai and consent services; patient token can read other members' resources by ID | Access-control findings; potential reportable PHI exposure |

**Verdict: could the plan pass a CMS program audit and a state UM audit on CHO today? No.**

**Prioritized remediation:**
1. **Close security gaps.** Remove the `tenantId` override on `sla/at-risk`. Require login on rfai and consent services. Enforce patient binding on every read. Take the tenant only from validated tokens.
2. **Put a qualified human behind every adverse decision.** Add nurse, medical director and supervisor roles; enforce `authorizations:decide` and physician-only denial in the backend; take reviewer identity and credentials from the token. Add an "approve or pend only" setting for automation, and make it the default.
3. **Fix the PAS-to-UM handoff.** Real member data, line of business, urgency and decision status; an authenticated call; failures surfaced and retried.
4. **Make clocks compliance-grade.** Fix the monitor's tenant context, store the due date, stop restarting on pend, add extensions with notice, apply state overrides, and alert before breach.
5. **Version decisions so they can be reconstructed.** Version and effective-date PA rules; save the firing rule, rule version and criteria reference on each case; move history to an append-only store with 10-year retention and legal hold.
6. **Notices.** Template engine with state and line-of-business language, translations, appeal rights, MA Integrated Denial Notice and Medicaid notice of action.
7. **Appeals.** Link appeals to authorizations, correct the clocks, add State Fair Hearing, continuation of benefits and IRE handling, and make overturns trigger effectuation.
8. **Universes and metrics.** ODAG universe extract, CMS-0057-F metrics, state reports.
9. **Criteria.** InterQual or MCG integration plus NCD/LCD mapping for MA; UM committee governance; public posting.
10. **Fix seed-rule logic errors** (STARKids auto-approve, inverted wheelchair diagnosis logic, the gold card) before any use.

---

## 7. What would make CHO rival incumbent UM platforms

### Where CHO is differentiated

- **One platform, native connections.** PAS, the authorization record, claims and the member APIs share one platform. Incumbents bolt Da Vinci onto a UM system and a claims system with vendor glue. Once the wiring is fixed, a PA decision can flow to claims payment, Patient Access and Payer-to-Payer without interfaces.
- **Source available.** Plans and auditors can inspect decision logic. No incumbent offers that.
- **A good foundation for automated decisions.** The deterministic rule engine with an explicit trace object (`PaRuleDecision`) is the right base for explainable automation, *if* the trace is saved and adverse outcomes are gated.
- **The "more information needed" loop** (pend → document request → resume) is better integrated than many vendor stacks.

### Where incumbents are ahead

- **Criteria integration.** InterQual and MCG embedded in the review screen, with criteria versions captured.
- **Clinical workflow depth.** Inpatient concurrent review, post-acute, BH and LTSS case types, medical director queues, peer-to-peer scheduling, fax intake with OCR.
- **Letters.** Mature template libraries with state and line-of-business language, translations, regulatory notice formats and print/mail vendors.
- **Delegated vendor networks.** Established feeds to eviCore, Carelon and others, with oversight reporting.
- **Audit history.** Years of passing ODAG and NCQA file reviews.

### Prioritized roadmap

**Must-have for 1 January 2027 API compliance** (about 13 weeks away; the 2026 operational items are already overdue)

| # | Item | Size |
|---|---|---|
| J1 | Fix the PAS → UM record handoff (data, token, status, urgency, error handling) | S |
| J2 | CRD: coverage-information extension, per-member line of business and state context, remaining hooks, working DTR launch | M |
| J3 | DTR: adopt an open-source DTR SMART app and CQL engine (or partner), questionnaire packages with prepopulation | L |
| J4 | PAS: profile validation, line-level decisions, Subscriptions, update/cancel, correct review-action placement | M |
| J5 | Patient Access: replace mock adapters with real member, coverage and claims projections; add PA data; production login; patient binding on all reads | M |
| J6 | Provider Access: opt-out consent model, attribution feed, real bulk export with PA data | M |
| J7 | Payer-to-Payer: HRex `$member-match` with consent, PDex export including PA, quarterly concurrent exchange | M |
| J8 | Run the Da Vinci CRD/DTR/PAS, PDex and US Core test kits against CHO as the server; publish results | S |
| J9 | Metrics extract for public reporting (backfill the late 2025 report) | M |

Realistically, J3 alone puts the 1 January 2027 date at risk. **A plan that needs to comply on that date should plan for a DTR vendor and run CHO's PAS and APIs in parallel, not depend on CHO alone.**

**Must-have to go live as a UM platform**

| # | Item | Size |
|---|---|---|
| G1 | UM workbench: queues with clock-based routing, assignment, decision screen, partial approval, escalation, peer-to-peer | L |
| G2 | Roles and controls: nurse, medical director, supervisor; backend enforcement; reviewer credentials | M |
| G3 | Compliance-grade clocks with extensions, state overrides and alerts | M |
| G4 | Criteria integration (InterQual/MCG) and NCD/LCD support; criteria versioning; UM committee records | L |
| G5 | Letters and notices engine (state/line of business, translations, IDN, notice of action) | L |
| G6 | PA-linked appeals with correct clocks, State Fair Hearing, IRE, effectuation | M |
| G7 | Case types: inpatient concurrent review, post-acute, DME, BH, LTSS | L |
| G8 | Configuration screen for PA requirements with effective dating, versions, plan and product dimension, and merging of platform and tenant rules | M |
| G9 | Fax/OCR and phone intake; X12 278 in and out; duplicate detection; eligibility checks at intake | M |
| G10 | Auth-to-claim: match on member, provider, service and dates in every claim path; consume units; fail closed | M |
| G11 | Delegated UM intake and oversight reporting | M |
| G12 | Tamper-evident audit store, 10-year retention, legal hold; ODAG universe extracts | M |

**Differentiators to win deals**

| # | Item | Size |
|---|---|---|
| D1 | "Explain this decision" view backed by a saved, versioned rule-and-criteria trace, shared with the provider and member | S (after G4 and G12) |
| D2 | AI-assisted clinical summarization of DTR and attached records for nurses, with **approve-only** recommendations, saved inputs and model version, and clinician sign-off for any adverse outcome | M |
| D3 | Real gold carding: per-service, per-provider history, statutory evaluation periods, notices | M |
| D4 | Closed loop: PA → claims → member EOB → Payer-to-Payer, with one data model and one audit trail | M |
| D5 | Live CMS-0057-F metrics dashboard, publishable to the plan's website in one click | S (after J9) |

---

## 8. Overclaims

| Claim | Where stated | What the code shows | Suggested honest wording |
|---|---|---|---|
| "Cloud Health Office's implementations have passed Touchstone and Inferno testing suites and are production-deployed." | `src/site/docs/prior-auth-guide.html:563` | No Inferno suite has run, and there are no Touchstone artifacts. External tests ran with CHO as the *client* (`docs/interop/davinci.md` §3, §13). No production customers. | "CRD, DTR and PAS endpoints are implemented and tested internally; server-side conformance testing is planned." |
| "Automated prior auth with 72-hour urgent and 7-day standard response tracking" | `src/site/release-notes.html:603` | The monitor fails in production; PAS urgency isn't saved. | "Calculates 72-hour and 7-day due dates; automated monitoring in progress." |
| "72-Hour Urgent Response ✅ Automated" / "7-Day Standard ✅ Automated" | `src/site/cms-0057f-compliance.html:1322-1323` | Same as above. | "Due-date calculation implemented; alerting in progress." |
| "Cloud Health Office implements all four [APIs] as an implemented technical capability" | `src/site/cms-0057f-compliance.html:100,563` | Patient Access serves mock data; bulk export is a stub (the same page admits this at `:1320`); Provider Access is opt-in. | "Implements PAS and Payer-to-Payer foundations; Patient Access and Provider Access data integration in progress." |
| "Real-Time Prior Authorization … automated claims adjudication systems correlation" | `src/site/platform.html:415-419` | Auth-to-claim is checked only for Medicaid inpatient and fails open. | "Authorization validation available to claims; full matching in development." |
| "State-specific PA decision rules, gold card exemptions" | `src/site/platform.html:1014` | The gold card rule never fires; the PAS gold card is plan-wide. | "Texas Medicaid PA rule examples; gold card logic in development." |
| "TxGoldCardExemptionRule … 90%+ approval rate for the specific service category over the past 6 months; enrolled 2+ years; no recent adverse actions; re-evaluated quarterly" | `src/site/docs/prior-auth-guide.html:367-375` | Provider-wide, not per service. No tenure or sanctions check. No evaluation job. Code cites "HB 3229". | Remove until implemented. |
| "CHO ships with 14 platform-level Texas Medicaid seed rules" | `src/site/docs/prior-auth-guide.html:378` | Ten rules (`pa-engine/SeedRules/TxMedicaidSeedRules.cs`). | "Ten sample Texas Medicaid rules." |
| "Auto-Approve — Request meets clinical criteria … 40–60%" | `src/site/docs/prior-auth-guide.html:284,314` | There are no clinical criteria; the percentage is unsupported. | "Rule-based auto-approval for configured services." |
| "HHSC UMCM prior authorization rules for STAR, STAR+PLUS, STAR Kids, and CHIP" | `src/site/docs/texas-compliance.html:22` | No CHIP rules or CHIP line of business. | Drop "CHIP". |
| "LlmAssistedParser (Claude API) … Claude API backfills CPT codes" | `tools/CloudHealthOffice.TmppmIngestionService/README.md:19,211-212,251` | The files don't exist; extraction is regex; no review gate. | "Regex-based TMPPM extraction; human review step planned." |
| "Avg decision time: <2 minutes" / "auto-adjudicated within 2 minutes" | `portal/Pages/Authorizations.razor:17`; `portal/Pages/SubmitAuthorizationDialog.razor:15` | Hard-coded copy; portal submit fails. | Remove. |
| "Appeals — … regulatory deadline monitoring (MA 30-day standard, 72-hour expedited)" | `CHANGELOG.md:1312` | Backend uses 30 days urgent / 60 days standard. | "Appeal due-date tracking (clock rules being corrected)." |
| "CMS-0057-F Compliant: Meets all regulatory requirements" | `docs/features/PRIOR-AUTHORIZATION-API.md:43` | Contradicted by §4. | Remove; point to the readiness matrix. |
| "Full Prior Authorization Final Rule implementation" | `docs/features/FHIR-INTEGRATION.md:69` | Contradicted by §4. | Remove. |
| "v3.0.0 is production-ready … CMS-0057-F compliance" / "Complete CMS-0057-F compliance with production-ready FHIR R4 APIs" | `CHANGELOG.md:1466,1628` | Contradicted by later changelog entries and the readiness matrix. | Annotate as superseded. |
| "deploys alongside any existing core admin system (QNXT, HealthEdge, Facets…) … CRD → DTR → PAS pipeline implemented" | `docs/POSITIONING.md:177-183,195` | QNXT backend is a NotImplemented stub; CRD hard-codes Texas STAR. | "Designed to deploy alongside an existing core; adapters are per-plan work." |
| CRD "Implemented"; PAS "auto-decision … persistence path exist"; Bulk export "Implemented / integration required" | `docs/compliance/CMS-0057-F-READINESS-MATRIX.md:60-61,68` | See 4a and 4f. The matrix also contradicts the acceptance manifest on metrics (Phase 2 vs PASSABLE). | Update the matrix from this review. |
| Acceptance manifest: all 21 scenarios "PASSABLE" | `src/site/insights/cms-0057-f/cms0057-public-evidence.json` | Several rest on trivial assertions: METRICS-01, PROV-03 opt-out, PAT-01, PAS-06 (tested on a ServiceRequest, not PAS). | "Internal unit-test coverage of 21 scenarios; not regulatory conformance." |

The repo has honest statements too, worth preserving: `src/site/cms-0057f-compliance.html:1420`, `src/site/assessment.html:150` ("We do not claim certified compliance"), and the QNXT adapter scoping page.

---

## 9. Assumptions and open questions

### Assumptions

1. I reviewed code at commit `00cc64c` by reading it, without running it. Items marked "likely" were inferred, not observed. The main one is that the PAS-to-authorization-service call returns 401 because no bearer token is sent.
2. I treated `fhir-service` plus `authorization-service` as the production UM path, and the portal's `/authorizations` pages as the UM user interface.
3. Regulatory dates and applicability are stated from my understanding of CMS-0057-F (published February 2024) and related MA and Medicaid rules. Items marked **"verify against rule text"** need confirmation by compliance counsel, especially:
   - extension rules per program;
   - QHP applicability of the denial-reason and usage-reporting provisions;
   - Payer-to-Payer applicability to Medicaid and CHIP FFS;
   - the status of X12 278 enforcement discretion;
   - required IG versions.
4. Whether Texas HB 3459 gold carding applies to Medicaid and CHIP MCOs is flagged for counsel. It is not treated as settled.
5. Per the brief, vendor adapters (QNXT, criteria vendors, DTR/PAS vendors) are treated as implementation work. They are scored as product gaps only where the core contract is inadequate: there is no external decision-engine API and no CDS Hooks proxy.
6. "Missing" means I searched and found nothing. Capabilities in other repositories are out of scope.

### Questions a UM VP and compliance officer would ask in a pilot evaluation

1. Can every automated **denial** path be switched off, so automation can only approve or pend? Who is the reviewer of record for each automated decision today?
2. Show a PAS request end to end, with real member data, landing in the UM record with the same decision the provider received. What happens when that handoff fails?
3. How will you meet 1 January 2027 for DTR (CQL, prepopulation, SMART app)? Build, open source or partner, and on what date?
4. When will Patient Access serve real member, coverage, claims **and PA** data? When will production member login work?
5. Why is Provider Access opt-in? When will it be opt-out?
6. Show your clock monitor running in a multi-tenant production deployment. How do you treat a pend for information, and how do you handle extensions and stricter state rules?
7. Can you produce our CMS-0057-F public metrics (including the late 2025 report) and an ODAG universe from CHO data? Who validated the calculation?
8. Which criteria product do you integrate with, and how is the criteria version captured on each decision? How do we post MA internal coverage criteria?
9. What letters do you generate for MA (Integrated Denial Notice) and Medicaid (notice of adverse benefit determination), in which languages, and who maintains the templates?
10. How are delegated vendor decisions brought in and overseen?
11. When will you run the Da Vinci, PDex and US Core test kits against CHO as the payer, and will you remove the website claim that this has already happened?
12. How is tenant isolation enforced in authorization-service, rfai-service and consent-service? Why does `sla/at-risk` accept a tenant parameter?
13. What is your retention design for 10-year MA records, and how is legal hold applied during appeals and litigation?
14. Given there are no production customers yet, what implementation team, SLA and regulatory-change commitments come with a contract?
