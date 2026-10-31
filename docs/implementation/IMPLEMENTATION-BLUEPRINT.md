# Cloud Health Office Implementation Blueprint

This blueprint explains what implementing Cloud Health Office (CHO) involves for a health plan: the paths you can take, what happens in each phase, what your teams do, how decisions are made, and how risk is controlled. It is written for the CIO, the claims operations leader, the compliance lead and the project sponsor evaluating CHO.

It follows the engagement layers in [POSITIONING.md](../POSITIONING.md), which is the source of truth for what CHO claims. Use it with the [Core Administration Adapter QRE](../features/questionnaires/qre-core-admin-adapter-questions.md), which collects your plan's specifics. The blueprint describes the general shape of the work; your QRE answers size it.

> **Status and estimates.** CHO has no production implementation yet. Durations and staffing figures in this blueprint are estimates based on the founder's QNXT implementation experience (TriZetto, Molina, Cognizant), and are marked as estimates. They will be replaced with measured figures after the first pilot. Capability status is labeled the same way as in the [diligence binder](../diligence/README.md): what ships today, what is in development, and what is built per engagement.

## How CHO Keeps Implementation Low-Risk

- **No big-bang cutover.** You can start with interoperability over your existing core and never replace anything. If you do replace, it happens one claim type and line of business at a time.
- **Your core stays authoritative until you decide otherwise.** In Augment mode CHO runs in shadow and its results are compared with your core's. Nothing changes for members or providers until you approve a cutover.
- **Every gate can be reversed.** Each Replace cutover has a rollback to Augment or Legacy only.
- **Evidence is labeled.** Every FHIR response carries adapter headers (`X-CHO-Adapter-Mode`, `X-CHO-Data-Class`) and `/fhir/r4/adapter-status` reports exactly which resources are demo, hybrid or live. A reviewer can always see what is real.
- **You keep what you build.** CHO is source-available. Configuration, adapters, runbooks and knowledge built during the implementation belong to your plan, and your team is trained to run them. You are not locked in to an implementation partner to operate your own system.

## Choose an Entry Layer

Cloud Health Office engages at the three layers defined in [POSITIONING.md](../POSITIONING.md). Each layer is a complete offering on its own, and each contains the previous one. Most established payers start at Layer 1. Layers 2 and 3 are options, not requirements.

| | Layer 1: Foothold | Layer 2: Domain cutover | Layer 3: Core |
|---|---|---|---|
| What it does | CMS-0057-F surface (Patient Access, Provider Directory, Payer-to-Payer, prior authorization through CRD, DTR and PAS) deployed alongside your existing core | Moves one domain at a time to CHO, using Augment (shadow) then Replace. Appeals is the first domain ready for this | CHO is the core administration platform |
| What stays the same | Your core stays the system of record for every claim, authorization and payment | Your core stays the system of record for every domain not yet moved | No legacy core (new plan or new line of business), or the legacy core is retired after Layer 2 |
| Phases | 0–2 | 0–4 per domain (5 optional) | 0, 1 (data conversion), 3 (parallel test), 4 |
| Starting commitment | Founding-partner CMS-0057-F Compliance Accelerator: 6–8 weeks, one line of business | Layer 1 first, then one domain (typically appeals) | Discovery and a data conversion plan |
| Estimated duration | 6–8 weeks for the accelerator; production with live data for one line of business about 3–6 months *(estimate)* | About 4–6 months per domain after Layer 1 *(estimate)*; claims domains take longer than appeals | About 6–9 months for a new entrant *(estimate)* |
| Main dependencies | Read access to core data; identity provider for SMART on FHIR; UM and EHR partners for prior authorization | Everything in Layer 1, plus agreed ownership of accumulators, claim history and payments while two systems run | Data conversion, configuration build, state and CMS readiness |

## Phases

Each phase lists its objective, the work, who delivers what, the exit gate, and how to roll back.

### Phase 0: Discovery (about 3–4 weeks, estimate)
- **Objective:** agree on scope, the path, and the domain mode matrix.
- **Work:** complete the QRE; review vendor contract terms, licensing and hosting-provider access; confirm the BAA; identify state or CMS notice requirements; inventory downstream consumers of the core.
- **Plan delivers:** QRE answers, core documentation, contract review, named team.
- **CHO delivers:** gap analysis, adapter design for each domain, project plan, risk register, staffing plan.
- **Exit gate:** signed scope, domain mode matrix and project plan.
- **Rollback:** none needed. Nothing is connected yet.

### Phase 1: Connect and Read (about 6–10 weeks, estimate)
- **Objective:** CHO holds an accurate, synchronized copy of the core data in scope.
- **Work:** establish connectivity; build read adapters for members, benefit plans, providers, claims and authorizations over the agreed access method (vendor APIs such as QXI or FXI, database views or replica, extracts, or EDI); load history; set up ongoing sync; map identifiers.
- **Plan delivers:** network access, credentials, test environment, data subject-matter experts for reconciliation.
- **CHO delivers:** adapters, sync jobs, identifier mapping, reconciliation reports.
- **Vendor dependencies:** API licensing and enablement; hosting-provider access requests.
- **Exit gate:** reconciliation reports show CHO's copy matches the core within agreed tolerances.
- **Rollback:** disconnect the adapters. The core is never written to in this phase.

### Phase 2: Interoperability Layer (about 8–12 weeks, estimate; overlaps Phase 1)
- **Objective:** CMS-0057-F APIs and the agreed CRD, DTR and PAS responsibilities are live over the existing core.
- **Work:** configure SMART on FHIR with your identity provider; turn on Patient Access, Provider Access, Payer-to-Payer and Provider Directory; load payer rules and questionnaires; map PAS to X12 278 where needed; onboard first EHR partners. Layer 1 is read-mostly: if authorization decisions must be written back into your UM system, that is scoped as an added integration.
- **Plan delivers:** UM rules and criteria sources, payer-specific questionnaires, EHR partner contacts, compliance sign-off.
- **CHO delivers:** API configuration, rule loading, conformance and interoperability test results, partner onboarding support.
- **Exit gate:** conformance tests pass; first providers live; compliance owner signs off. CHO does not attest compliance on the plan's behalf.
- **Rollback:** turn off individual APIs. The core is unaffected.

**Layer 1 ends here.** Many plans will stop at Phase 2 and run CHO as their CMS-0057-F layer indefinitely.

### Phase 3: Augment (Shadow) (about 2–4 months per domain or claim type, estimate)
- **Objective:** prove CHO produces the same results as the core before it becomes authoritative.
- **Work:** build the feed that supplies the core's results to CHO for comparison; configure benefits, pricing and edits in CHO; run in shadow; review discrepancies and fix configuration; agree on which system owns accumulators, claim history, payments and correspondence during the transition.
- **Plan delivers:** configuration subject-matter experts, discrepancy review, claims operations sign-off.
- **CHO delivers:** legacy-result feed (part of the per-engagement adapter), configuration build, discrepancy reporting and analysis, transition ownership design.
- **Exit gate:** discrepancy rate below the agreed threshold for the agreed period (default guideline under 1% for 30 days); transition ownership agreed.
- **Rollback:** set the domain back to Legacy only. Members and providers were never affected.

### Phase 4: Replace (about 1–2 months per domain or claim type, estimate)
- **Objective:** CHO becomes authoritative for the domain or claim type.
- **Work:** cutover runbook and rehearsal; accumulator and claim history sync with the core; payment and 835 consolidation; downstream feed changes; examiner training; hypercare.
- **Plan delivers:** change approval, operations readiness, state notice if required, trained staff.
- **CHO delivers:** cutover runbook, monitoring, hypercare support, rollback plan.
- **Exit gate:** CHO authoritative with accumulators, payments and downstream feeds reconciled and no regressions through hypercare.
- **Rollback:** switch back to Augment. The core resumes as authoritative.

### Phase 5: Decommission (optional)
- **Objective:** retire the legacy core for lines of business fully on CHO.
- **Work:** final data conversion; historical data retention; 835 and encounter submission handoff; vendor contract wind-down.
- **Exit gate:** core retired for those lines of business, with retention and audit requirements met.

## What Your Teams Do

Estimated share of each role's time by phase. These are starting estimates to be confirmed for your plan; "—" means not involved.

| Plan role | 0. Discovery | 1. Connect and read | 2. Interoperability | 3. Augment | 4. Replace |
|---|---|---|---|---|---|
| Executive sponsor | 10% | 5% | 5% | 5% | 10% |
| Project manager | 50% | 50% | 50% | 50% | 75% |
| Core platform owner or configuration lead | 25% | 25% | 10% | 50% | 50% |
| Integration engineer or DBA | 25% | 50% | 25% | 25% | 25% |
| Information security | 25% | 25% | 25% | 10% | 10% |
| Claims operations subject-matter expert | 10% | 10% | — | 50% | 75% |
| Utilization management lead | 10% | — | 50% | 10% | 10% |
| Compliance or interoperability lead | 25% | 10% | 50% | 10% | 10% |
| Finance (payments, general ledger) | — | — | — | 10% | 25% |
| Downstream system owners (warehouse, reporting) | 10% | 10% | 10% | 10% | 25% |

## Who Does What

R = responsible, A = accountable, C = consulted, I = informed.

| Activity | Plan | CHO | Core vendor or host |
|---|---|---|---|
| Scope and path decision | A | R | I |
| Vendor contract and access approvals | A/R | C | R |
| BAA and security review | A | R | C |
| Connectivity and credentials | A/R | C | R |
| Adapter build and data sync | C | A/R | C |
| Reconciliation sign-off | A | R | I |
| CMS-0057-F API configuration | C | A/R | — |
| UM rules, questionnaires and clinical criteria | A/R | R | C |
| Compliance attestation | A/R | C | — |
| Benefit, pricing and edit configuration in CHO | C | A/R | — |
| Discrepancy review and cutover approval | A | R | I |
| State or CMS notification | A/R | C | — |
| Training and operations readiness | A | R | — |
| Downstream feed changes | A/R | C | C |

## Decision Gates

| Gate | Decision | Criteria | Decided by |
|---|---|---|---|
| G0 | Proceed past discovery | Scope, mode matrix and plan signed; contract path clear | Executive sponsor |
| G1 | Data is trusted | Reconciliation within tolerance | Data owners and project manager |
| G2 | Interoperability go-live | Conformance tests pass; compliance sign-off | Compliance lead |
| G3 | Ready to cut over a domain or claim type | Discrepancy threshold met; transition ownership agreed | Claims operations leader |
| G4 | Hypercare complete | No regressions; feeds reconciled | Executive sponsor |

## Key Risks and Mitigations

| Risk | Mitigation | Owner |
|---|---|---|
| Vendor contract restricts database or API access | Review in Phase 0; choose the access method the contract allows; escalate early | Plan |
| Hosting-provider or vendor lead times delay connectivity | Submit requests in Phase 0; track as critical path | Plan |
| Source data quality issues surface during sync | Reconciliation reports in Phase 1; data owners fix at source | Plan and CHO |
| Accumulators drift between systems during split operations | Single owner per accumulator; sync frequency agreed; daily reconciliation | CHO |
| Duplicate or cross-claim edits miss history held in the other system | Shared claim history sync before cutover | CHO |
| Providers receive two payments or 835s per cycle | One payment owner during transition | Plan and CHO |
| Downstream feeds break at cutover | Inventory in Phase 0; feed changes tested before G3 | Plan |
| State readiness review extends the timeline | Identify requirements in Phase 0; schedule early | Plan |
| Prior authorization partners or EHRs onboard slowly | Start with one EHR partner; keep existing 278 and portal channels | Plan and CHO |
| Key staff unavailable for discrepancy review | Named, time-allocated subject-matter experts in the project plan | Plan |

## Worked Example (Fictional)

*Example Regional MCO* is a fictional Texas Medicaid plan on QNXT, hosted by its vendor.
- **Months 1–2 (Layer 1 accelerator):** discovery and QRE; 6–8 week labeled pilot on synthetic data; G0 approved.
- **Months 2–6 (Layer 1 production):** BAA signed; read adapters over the vendor APIs; Patient Access and Provider Directory live; PAS live with the first EHR partner while the UM system stays the system of record; G1 and G2 approved.
- **Months 6–10 (Layer 2, appeals):** appeals runs in CHO in Augment, then cuts over to Replace; QNXT keeps claims, authorizations and payments; overturned appeals flow back to QNXT for reprocessing.
- **Later (Layer 2, claims):** STAR professional claims run in Augment with QNXT owning accumulators and CHO receiving daily accumulator updates. Cutover to Replace only after the discrepancy rate stays below the agreed threshold and payment ownership is agreed.
- **Next:** further domains follow the same pattern, or the plan stays at this point for as long as it chooses.

## What Ships Today and What Is Built Per Engagement

| Capability | Status | Reference |
|---|---|---|
| Core administration services (enrollment, premium billing, provider contracts and pricing, capitation, encounters, appeals, claims adjudication) | Built in CHO | `docs/architecture/` |
| Augment, Replace and Legacy-only routing per tenant, claim type and line of business; discrepancy comparison logic | Built in CHO | `docs/architecture/OPERATING-MODE.md`, `src/engines/CloudHealthOffice.OperatingMode/` |
| Supplying the legacy core's results for Augment comparison | Built per engagement, as part of the core adapter. Not wired today, so Augment comparison runs only once this exists | `src/services/benefit-plan-service/Controllers/AdjudicationController.cs` |
| Discrepancy dashboard | Not built yet; discrepancies are returned in adjudication responses and logs | `AdjudicationResponse.Discrepancies` |
| Appeals as a complete CHO domain (first Layer 2 domain) | Built in CHO | `docs/POSITIONING.md` (Layer 2) |
| Correspondence and letter generation | Not built yet | `docs/POSITIONING.md` (Layer 3 gaps) |
| Real-time eligibility (X12 270/271) | In production through a clearinghouse, serving Cloud Dental Office | `src/services/provider-eligibility-api/`, `src/services/shared/CloudHealthOffice.Infrastructure/Gateways/` |
| CMS-0057-F APIs and CRD, DTR and PAS | In development; tested against HL7 Da Vinci reference implementations; not yet conformant | `docs/compliance/CMS-0057-F-READINESS-MATRIX.md`, `docs/interop/davinci.md` |
| Adapter labeling and status reporting | Built in CHO | `docs/diligence/ADAPTER-STATUS.md` |
| QNXT, Facets and HealthEdge adapters | Built per engagement from the QRE; interfaces and stubs exist | `docs/architecture/claim-adapter-pattern.md` |
| Payer-specific rules, questionnaires, configuration | Per engagement | This blueprint and the QRE |

## Measuring Success

- **Data:** reconciliation match rates by domain (Phase 1).
- **Interoperability:** conformance and Da Vinci interoperability test results; API availability and response times (Phase 2).
- **Adjudication:** discrepancy rate against the core by claim type (Phase 3); Million Claim Challenge accuracy and throughput benchmarks.
- **Operations:** claims turnaround, pend rates and examiner productivity before and after cutover (Phase 4).
- **Compliance:** prior authorization decision timeliness and metrics reporting.

## Related Documents
- [Implementation guide: loading a plan's data](../integration/LOADING-A-PLANS-DATA.md): the loads in order, with endpoints, dry runs and the daily drop
- [Positioning and engagement layers](../POSITIONING.md)
- [Core Administration Adapter QRE](../features/questionnaires/qre-core-admin-adapter-questions.md)
- [Diligence binder](../diligence/README.md) and [sample pilot checklist](../diligence/SAMPLE-PILOT-CHECKLIST.md)
- [Adapter status](../diligence/ADAPTER-STATUS.md)
- [Operating modes](../architecture/OPERATING-MODE.md)
- [CMS-0057-F readiness matrix](../compliance/CMS-0057-F-READINESS-MATRIX.md)
