# Oregon Medicaid qualification and remediation backlog

Assessment: October 24, 2026, America/Phoenix. Platform: **CloudHealthOffice**,
`aurelianware/cloudhealthoffice`, native core in a plan-operated Azure or GCP
environment, approximately 250,000 members. Source baseline:
`e7d633930222050184e98ddb851bf09fb3ef9495` (fetched `origin/main`). Assessment
branch: `assessment/oregon-medicaid-1543`. Initial checkout was clean at
`39c1e09cb88a9242d146690c3d9a2e74638ba142` on
`assessment/texas-medicaid-1542`; that branch remains intact. The existing clean
checkout was reused; no unrelated work was overwritten. No root/ancestor
AGENTS.md was found; the worker-specific AGENTS.md does not cover these changes.
CONTRIBUTING, security guidance, documentation index, capability ledger, acceptance
workstream, active gap matrices, ADRs 006/010/016/017/024/026/027/030 and enrollment,
PCP, encounter, correspondence, PA evidence and banking/GL material were consulted.

The [canonical capability ledger](../CAPABILITY_RECONCILIATION.md) owns status.
This workstream supplies Oregon evidence and acceptance contracts, not a second
status authority. All source paths below are repository-relative and pinned to
the baseline above, plus the explicitly described characterization tests.

## Assessment result and related PRs

**Launch readiness is unverified.** The user confirmed CHO is the platform under
assessment, but did not identify the prospective OHA-contracted CCO or delegated
administrator, executed contract, counties, benefit allocation or launch date.
These facts cannot be inferred from a software repository or PR #1542.

- [#1542](https://github.com/aurelianware/cloudhealthoffice/pull/1542) merged as
  `9cea3b614ae006c96d0a9aafdad5937ad4f8510c`: reusable native PA evidence and annual
  drafts are credited. This is not an Oregon-only or California-only feature,
  a PA appeal adjudication system, delivered notice, or approved public report.
- [#1543](https://github.com/aurelianware/cloudhealthoffice/pull/1543) merged as
  the baseline above: documentation-only Texas qualification. Its tests and
  findings are historical evidence for that assessment, not newly executed
  Oregon acceptance. Shared implementation was inspected and selected checks
  rerun here. Texas PEMS, UMCM, STAR and gold-card policies do not establish
  Oregon applicability.

Confirmed inspected limitations: FL-only automated encounter routing/batching;
no Oregon provider-enrollment source in the registered package; missing
service-specific deadline selection; capitation child synchronization returning
501; reproducible PCP capacity race; incomplete native notice fulfillment/PA
appeals; bounded financial events rather than an integrated close. Scope matters:
a controlled partner/manual workflow can mitigate some limitations, but requires
explicit owner, reconciliation, capacity and acceptance evidence.

## Oregon applicability and responsibility matrix — OR-00

**Unverified; P0; configuration, external evidence and governance.** Compliance and
the prospective operator's executive sponsor own this matrix. No particular CCO,
county, award or launch is asserted. Every finding inherits this unresolved scope.
For an OHA-contracted CCO, that CCO retains applicable contractual accountability;
for a delegated administrator, establish its signed delegated duties and the
CCO-retained decisions before assigning an obligation to it. CHO is the software,
not the OHA contractor. The plan operates calls, claims and payments as assumed.

| Required scope decision | Present assessment boundary | Owner / evidence to close |
| --- | --- | --- |
| Legal entity / OHA contract / delegation | Unknown CCO versus delegated plan or administrator | Executive + legal: executed OHA agreement, subcontract, authority and retained/delegated responsibility schedule |
| Programs, counties/service areas, populations, enrollment and launch | OHP Medicaid managed care baseline only; no medical-only exclusion | Compliance + enrollment: awards, package/CCO/delegated identifiers, population and effective-date inventory, launch/rating period and transition instructions |
| Physical, behavioral, SUD, dental, pharmacy, NEMT and other assigned benefits | Each must be assigned explicitly; a subcontractor does not remove responsibility | Benefits + operations: benefit/carve-out/delegation matrix naming payment, authorization, care coordination, notice and encounter owners for each benefit |
| OHP Bridge, non-Medicaid OHP/Healthier Oregon, Medicare/duals | Conditional; no blanket launch blocker or exclusion | Compliance: actual contracts/populations; dual coordination/COB may apply without operating a Medicare plan |
| LTSS, HCBS, EVV and state-retained services | Conditional payment responsibility; coordination can still apply | Clinical + ODHS liaison: OHA/ODHS/APD/ODDS responsibility, MOU, transitions, service-plan and EVV boundaries |
| HRSN, health-related services, SHARE, ILOS and reentry/waiver duties | Separate benefit, funding and reporting categories; applicability unresolved | Clinical + finance: executed amendments, approved protocols, assigned populations/services, start dates, authorizations, reporting and delegated-provider contracts |
| Binding authority and versions | Public 2026 Medicaid template inspected; executed contract absent | Legal: signed contract/amendments, exhibits B/C/D/G/I/L/M and incorporated guidance, waiver/protocol and State Plan approvals effective at launch and date of service |
| Interface destinations/operators | OHA as authority; Medicaid MMIS as encounter system; ODHS/ONE eligibility roles distinct | EDI/enrollment: inventory every sender, receiver/operator, CCO/delegate/clearinghouse, permitted use and source of truth; access to ONE is not an enrollment feed |
| State/partner/bank/subcontractor acceptance | No agreements, credentials or acceptance supplied | Integration owners: trading-partner agreements, implementation guides, IDs, credentials, test cases, transport/transaction/state acceptance and reconciliation records |
| Deployment and operating targets | Azure/GCP choice and 250k membership do not define workload | CTO/operations: chosen cloud/database topology, volume forecast, batch/call concurrency, history/retention, SLO/RTO/RPO and launch-readiness evidence |

Acceptance for OR-00: an approved, versioned matrix linking every requirement to
its legal entity, population, county, benefit, date and accountable operator;
explicit exclusions and unresolved approvals; source/partner inventory and launch
schedule. Do not delay procurement of guides/test access until code is complete.

## Authority register and access limits

Official sources accessed October 24, 2026. These establish applicability questions
and scoped requirements, not that this unnamed plan has accepted any contract.
Use law, executed contract, incorporated policy and effective date to resolve
conflicts; proposed rules and public templates are not executed obligations.

| Ref | Source / version / use |
| --- | --- |
| A1 | [OHA CCO contract forms/deliverables](https://www.oregon.gov/oha/hsd/ohp/pages/cco-contract-forms.aspx), live index, and [2026 Medicaid contract template, 10/31/2025 filename](https://www.oregon.gov/oha/HSD/OHP/SiteAssets/Pages/CCO-Contract-Forms/final%202026%20M%20CCO%20contract%20template%2010312025.pdf). Public template only. Enrollment reconciliation is in Exhibit B Part 3 §11; network, appeals, financial and benefit deliverables require signed-version confirmation. |
| A2 | [Secretary of State, OAR Chapter 410 Division 141](https://secure.sos.state.or.us/oard/displayDivisionRules.action?selectedDivision=1728), current compilation: encounter §3570 (history includes DMAP 94-2025 effective 1/1/2026), service authorization §3835, transition §3850, network §3515, delegation §3505 and rights §§3880–3915. Direct individual-rule requests sometimes failed; the official division text was accessible. |
| A3 | [OHA EDI resources](https://www.oregon.gov/oha/ohp/providers/pages/edi-resources.aspx), [official Chapter 943 rules index](https://secure.sos.state.or.us/oard/displayChapterRules.action?selectedChapter=188), [encounter resources](https://www.oregon.gov/oha/HSD/OHP/Pages/Encounter-Data.aspx), and [encounter guidelines](https://www.oregon.gov/oha/HSD/OHP/Tools/Encounter%20Data%20Submission%20Guidelines%20July%202024.pdf). The PDF filename says July 2024 but its cover/body say March 2020: do not represent it as a verified 2026 companion guide. |
| A4 | [OHA provider enrollment](https://www.oregon.gov/oha/HSD/OHP/pages/provider-enroll.aspx) and [official OAR 410-120-1260](https://secure.sos.state.or.us/oard/view.action?ruleNumber=410-120-1260). State enrollment, non-payable/ordering/referring/prescribing roles and CCO credentialing differ. Public FFS active-provider file expressly omits CCO-only providers; it cannot establish complete CCO enrollment. |
| A5 | [Official OAR 410-200-0135](https://secure.sos.state.or.us/oard/view.action?ruleNumber=410-200-0135), amended effective 9/1/2025: age/program-specific CE, exceptions and protected eligibility. [OHA federal-change implementation information](https://www.oregon.gov/oha/hsd/ohp/pages/federal-changes.aspx) announces 2027 transitions. Do not replace state eligibility determinations with locally inferred renewal dates. |
| A6 | [OHA Benefit Update Project](https://www.oregon.gov/oha/HSD/Medicaid-Policy/Pages/Benefit-Update.aspx), [EPSDT guidance](https://www.oregon.gov/oha/HSD/OHP/CCO/EPSDT-Guidance.pdf), [2022–2027 waiver overview](https://www.oregon.gov/oha/HSD/Medicaid-Policy/Pages/Waiver-Renewal.aspx), [STCs amended 10/16/2024](https://www.oregon.gov/oha/HSD/Medicaid-Policy/Documents/2022-2027-OHP-STCs101824.pdf), and [State Plan index](https://www.oregon.gov/oha/HSD/Medicaid-Policy/Pages/State-Plans.aspx). The State Plan index warns its posted copies are informational and date-limited; obtain controlling approved SPAs/protocols and later amendments. |
| A7 | [OHA PA timeframe/reporting guidance updated 3/31/2026](https://www.oregon.gov/oha/HSD/OHP/CCO/Guidance%20for%20Prior%20authorization%20processing%20timeframe%20and%20reporting%20requirements-Updated%2003-31-2026.pdf): standard non-outpatient-drug PA 7 days effective 1/1/2026; annual public metrics due March 31, with source supplements needed for 2025. This is CCO guidance, not a generic FFS billing rule. |
| A8 | [Oregon Legislature ORS Chapter 414, 2025 edition with 2026 session-law notices](https://www.oregonlegislature.gov/bills_laws/ors/ors414.html), particularly §§414.074, .572 and .773. §414.074 requires CRT repair PA determinations within 72 hours. Launch legal review must also check the linked 2026 amendments, operative dates and uncodified provisions; no claim of exhaustive statutory review is made. |
| A9 | [Current 42 CFR Part 438](https://www.ecfr.gov/current/title-42/chapter-IV/subchapter-C/part-438), displayed current through 10/22/2026; [CMS-0057-F rule resources](https://www.cms.gov/initiatives/burden-reduction/overview/interoperability/policies-regulations/cms-interoperability-prior-authorization-final-rule-cms-0057-f). Entity/rating-period applicability matters; reporting/process duties generally begin 2026 and API provisions generally by the applicable rating period beginning on/after 1/1/2027. |
| A10 | [OHA HRSN guidance, September 2026, posted 10/22/2026](https://www.oregon.gov/oha/HSD/OHP/CCO/CCO%20HRSN%20Guidance%20Document%20September%202026.pdf), especially pp. 79–80: MMIS encounters plus separate financial/service-coordination reporting. [2026 DSN protocol](https://www.oregon.gov/oha/HSD/OHP/CCO/OR2026_DSN_Protocol_F1.pdf) and [IHCP reimbursement guidance updated 1/22/2026](https://www.oregon.gov/oha/HSD/OHP/CCO/2026%20CCO%20IHCP%20Reimbursement%20guidance.pdf) require contract/population-specific implementation. |
| A11 | [Official Oregon appeals rule 410-141-3890](https://secure.sos.state.or.us/oard/view.action?ruleNumber=410-141-3890), current text: standard resolution/notice within 16 days, conditional extensions, oral intake, state-hearing rights and reversal implementation. Do not substitute the federal maximum for this tighter Oregon requirement. |

No executed contract or proprietary MMIS transaction package was supplied. The
EDI page's dynamic technical-specification listing did not expose the current
834/837/response files in the retrieved page. No exact element layout or response
code semantics are inferred. The redesigned contract page returned 401; the older
official contract index and linked template were accessible. An ODHS January 2026
OPEN notebook search result was located but its PDF could not be fetched; its
contents are not accepted as verified. No universal public OHA enrollment API is
assumed. DCBS commercial guidance is not imported into CCO policy; A2 expressly
references Oregon electronic-exchange companion standards, whose applicable
versions still need acquisition. No plan-facing rate/coverage rule is adopted
merely because it appears in FFS guidance.

## Material findings and implementation acceptance

P0 = resolve/accept before the affected launch workflow; P1 = parallel work,
becoming P0 when assigned at launch. All findings concern the baseline OHP Medicaid
population in **unresolved counties/service areas**. Benefit-specific conditions
are stated below. Legal responsibility follows OR-00; role names are accountable
functions to be assigned by the prospective operator. No credentials, proprietary
guides, compliance approval, partner certification or operational acceptance are
implied by a code test. An accepted external workflow may close the operating
requirement while the native product limitation remains documented.

### OR-01 — Enrollment, identity and downstream reconciliation

**Unverified Oregon acceptance; P0; configuration + external integration + operations.**
A1/A3/A5 require a versioned state-to-CCO enrollment/reconciliation contract.
`src/services/enrollment-import-service/Program.cs` registers authenticated intake,
raw-file storage, streaming 834 parser, Mongo run/unit/event repositories,
`EnrollmentImportRunWorker` and member/coverage clients. `EnrollmentController`,
`Raw834Intake`, `EnrollmentImportRunProcessor.ProcessAsync` and
`EnrollmentImportService` implement import, identifiers, coverage-line changes,
termination/reinstatement; `EnrollmentEventPublisher.PublishAsync` persists
idempotent evidence. The 133 selected subscriber/dependent behavioral cases passed.
This is meaningful generic functionality, not proof of accepted Oregon semantics.

Remaining: Oregon Medicaid ID/crosswalk and collision policy; CCO versus delegated
assignment; benefit-package/physical/behavioral/dental mapping; source chronology,
retroactive changes and member-month/premium reconciliation. A 271 or ONE access
is not a CCO enrollment roster. State CE is distinct from membership in this CCO:
record state-approved periods and exceptions rather than independently terminating
on income/renewal inference. A5's current CE policy and announced 2027 changes
need launch-effective legal/interface confirmation; do not hard-code an indefinite
two-year rule or automatically implement announced work requirements locally.

Enrollment lead acceptance: accepted initial/full/delta/control/reconciliation
feeds, authoritative ID matching and duplicates, add/change/term/reinstate,
newborn/dependent, package/county/CCO/delegate moves, CE exceptions and retroactivity;
duplicate/out-of-order replay; prove final coverage and claims/PA/PCP/letter/
capitation effects and separate OHA premium reconciliation. Requires actual
guides, source agreements/credentials, state/partner and plan acceptance. Impact:
incorrect access, payment and premium attribution if mappings are wrong.

### OR-02 — Oregon encounter submission and final state acceptance

**Confirmed gap in registered automated route; P0; product + external integration.**
A2 §3570/A3 require reportable encounters beyond paid claims, including delegated
and capitated services. `encounter-submission-service/Program.cs` registers
`EncounterSubmissionServiceImpl`, optional Kafka `AdjudicationCompletedConsumer`
and `EncounterSubmissionWorker`. `ProcessMessageAsync` admits FL Medicaid only,
after an Approved/Paid status gate. `CreateSubmissionRecordAsync` assigns FL;
`BuildFmmisSubmissionBatchAsync` rejects every non-FL batch and emits FMMIS JSON.
Worker `RunCycleAsync` calls that builder and stages its output to Azure Blob.
There is no Oregon alternative in this registered chain.

Executed reproductions: `OregonMedicaid_CurrentlyCreatesNoEncounterRecord` passes
an OR Medicaid paid message and verifies no record; the OR row of
`WrongTenantOrState_IsRefusedBeforeFetchingOrWriting` proves refusal before
claim fetch or writes. These passing characterization tests demonstrate the gap,
not acceptable launch behavior. The 999 syntax-only and complete-batch fixes remain
credited. `encounter-service/Controllers/EncountersController` has generic 837
export, correction and status APIs backed by native repositories/EncounterEngine;
these are not an accepted OHA transport/response/reconciliation pipeline.

EDI lead acceptance: actual receiver/operator and partner boundary, accepted
837P/I/D and assigned pharmacy/HRSN reporting contracts; durable reservation/outbox,
replay, acknowledgments, rejects, correction/replacement/void, per-encounter MMIS
acceptance and completeness against adjudication/delegates, including unpaid and
non-FFS activity. Distinguish transport/clearinghouse/transaction/state acceptance.
Use A2's applicable timing/correction rules, not the Florida 60-day timer, and
confirm newer directives with the encounter liaison. Requires guides, trading-
partner agreement, credentials, partner testing and state acceptance. Impact:
missing/inaccurate state reporting and revenue/risk/quality reconciliation.

### OR-03 — Oregon provider enrollment and network evidence

**Confirmed missing Oregon source in inspected package; P0; product + integration.**
A4/A2/A10 distinguish Medicaid enrollment/revalidation from credentialing,
contracting, directory and network adequacy. Both `benefit-plan-service/Program.cs`
and `fhir-service/Program.cs` call `AddProviderEnrollmentService(...)
.UseMongoRepositories().WithTenantConfigCache().WithTexasSource()`.
`ProviderEnrollmentServiceRegistration` contains TX/CA/FL/NY/CAQH source builders,
no OR builder/source. `MultiStateEnrollmentAggregator.GetEnrollmentForStateAsync`
returns null for an unregistered state; the gate passes missing/disabled tenant
configuration and can reject unavailable OR records under enforcement. A named
multi-state gate is not an operating Oregon connection.

`StateEnrollmentGate.EvaluateAsync` receives a service date but its aggregator
requests today's record; `EnrollmentRepositoryMongo.UpsertAsync` replaces by
NPI/state with no historical interval/location key. Thus simply adding an HTTP
class cannot establish retroactive status. Existing aggregator/gate behavioral
checks passed; the unregistered-state case uses FL, not a live Oregon source.
No Oregon provider API, encounter-only enrollment acceptance or production
refresh is inferred from the public FFS file (which excludes CCO-only providers).

Provider lead acceptance: OHA-authorized source/import/manual verification,
provider type and non-NPI atypical identity, revalidation, relevant role/location/
NPI/taxonomy/effective intervals, freshness and unavailable policy; host composition,
retroactivity and scheduled source reconciliation. Establish encounter-only and
ordering/referring/prescribing rules with OHA. Reconcile plan credentialing,
contracts, directory, delegated networks and DSN capacity/access attributes;
274 remains conditional on an actual specification. Include IHCP access and
participation. Requires source agreement/guides/credentials, OHA/partner tests,
network/clinical compliance acceptance. Impact: inappropriate denial/payment and
encounter rejection; never mass-deny unavailable verification as proven unenrollment.

### OR-04 — Coverage policy, EPSDT and date-of-service transition

**Unverified Oregon policy/content; P0; configuration + clinical acceptance.**
A6 governs population/date-specific policy and the planned 1/1/2027 benefit
structure transition. EPSDT and assigned YSHCN exceptions preclude a universal
Prioritized List funding-line denial. Approved SPAs, STCs, protocols and transition
versions remain required; a proposed rule or old list is not sufficient authority.

`benefit-plan-service` registers `ChoBenefitPlanProvider`; claims executes
`BenefitCalculationStage`. Both PA hosts register the generic rule engine with
platform seeding. `PriorAuthRuleEngineService.ResolveRulesAsync` retains state in
all four specificity candidates; `PaRuleRepositoryMongo.GetRulesAsync` filters
state/LOB/program/tenant. No Oregon rule/coverage-policy implementation was located
by the scoped source search. Missing rules pend; do not equate this with a correct
Oregon benefit decision or with no generic benefits implementation. Texas gold-card
and STARKids seeds are not an Oregon policy. TX-04 remains Texas remediation;
no evidence here demonstrates that correctly mapped OR requests select TX seeds.

Clinical/benefits acceptance: approved effective-dated policy corpus, EPSDT/YSHCN,
mandatory benefits/exceptions, emergency/urgent access, continuation/transition,
service-code/source-version provenance, 2026-to-2027 date boundaries and denial
reasons. Prove OR routing never selects another state's rule and compare expected
clinical/dollar/notice outcomes. Include all assigned benefits/delegates. Requires
signed scope, approved State Plan/waiver material, clinical/legal sign-off; no
external credentials needed for rule tests. Impact: inappropriate coverage denials.

### OR-05 — PA service-specific clocks and notice deadlines

**Partial, confirmed selector limitation; P0 for affected services; product + policy.**
A7's ordinary 7-day medical target and expedited 72 hours align with the existing
receipt arithmetic; do not copy TX-05's three-business-day finding to Oregon.
However A8 §414.074 requires CRT repair decisions within 72 hours even when not
marked expedited. Drugs/HRSN and notice obligations require their own scenarios.

`AuthorizationRepositoryMongo.CreateAsync` initializes
`AuthorizationDeadlinePolicy`; `SlaWatchdogService` uses it. `DueAt` takes only
receipt/urgency and adds 168/72 hours. Reproduce with a nonurgent CRT repair received
2026-10-19 12:00 UTC: `DueAt(receipt, "E")` is 2026-10-26 12:00, while the repair
scenario needs 2026-10-22 12:00. Existing executed deadline tests prove that exact
168-hour branch, not legal compliance. No service selector or lawful-extension
workflow is established by #1542's attested reporting facts.

Clinical/engineering acceptance: approved program/service/urgency/calendar/version
policy and original receipt across channels; tighter applicable health-condition
response, CRT, assigned drug/HRSN paths, urgency changes, lawful extensions,
notice completion, outage/replay and no document-arrival clock reset. A mitigation
must identify/route affected services and prove staffing/timeliness. Requires
launch-effective rules/contract and clinical/compliance acceptance; partner
fulfillment testing under OR-08. Impact: late decisions/notices and member rights.

### OR-06 — Delegation, contracts and three capitation boundaries

**Partial with confirmed child-sync gap; P1/P0 when used; product + configuration.**
A1/A2 delegation obligations require effective responsibility and oversight.
`provider-contracts-service/Controllers/ProviderContractsController.SyncChildren`
loads the contract then returns 501; it never calls capitation children. Reproduce
with an authorized PUT to an existing contract's `sync-children` route. FFS terms
already reside on the parent under ADR 016; containerizing legacy ffs-service
would not repair this limitation.

Contracting/CFO acceptance: versioned benefit/financial/delegate assignments;
amendment/termination child propagation with audit and replay; retroactive
member-month changes, recoupments/overpayments and subcontractor reconciliation.
Keep OHA-to-CCO premium, CCO-to-delegate funding and provider capitation distinct.
A manual amendment control needs full inventory, dual review and accepted volume.
Requires contracts/rates, delegate testing, finance/clinical approval. Impact:
stale provider funding and incorrect responsibility across benefits. Coordination
with OHA/ODHS retained services does not imply plan claims-payment responsibility.

### OR-07 — PCP panels, assignment consistency and reconciliation

**Partial, reproduced capacity defect; P1/P0 for native launch assignment.**
`coverage-service/Program.cs` registers `PcpAssignmentService` and scoped
`PcpPanelReconciliationJob`, with no hosted scheduler for that panel scan.
`AssignAsync` validates coverage/provider/participation, reads capacity, ends old
assignments, inserts and updates medical coverage denormalizations in separate
operations. Executed `ConcurrentMembers_CurrentlyBothTakeTheLastPanelSlot`
barriers both reads and proves two persisted assignments for a one-slot panel.
The test characterizes the defect; no concurrency repair is included.
`HttpPanelCounter` unavailable behavior is fixed and covered by executed tests.

Network/engineering acceptance: authoritative panel scope, atomic reservation,
idempotency/rollback and concurrent/future/retroactive assignments; scheduled
reconciliation and member choice/reassignment/communications. Apply A1/A2 primary
care home, special-population, IHCP and transitions requirements after scope
approval. Dental provider, behavioral relationships and care-coordinator assignments
are separate. Requires plan policies, network/delegate tests and operations
acceptance; no state credentials needed for atomicity repair. Impact: overfilled
panels, inconsistent assignments and access failures.

### OR-08 — Fulfilled notices, grievances, PA appeals and hearings

**Partial; P0; product + approved content + fulfillment integration.**
A2/A11 require Oregon member-rights workflows; Oregon standard appeal resolution
is 16 days, not the generic federal 30-day maximum. Expedited, extension,
continuation/reinstatement and hearing conditions need distinct configured paths.
Also establish the CCO-retained adjudication boundary in A2 §3880 before delegating
appeal decisions. A provider payment dispute is not a member appeal.

`correspondence-service/Program.cs` registers `LetterComposer`, renderer/resolvers,
Mongo repository and document client. `CorrespondenceController` composes/renders;
this host registers no decision-trigger/channel fulfillment worker.
`appeals-service/Program.cs` registers durable repository/event outbox/dispatchers;
`AppealsController.CreateAppealRequest` requires ClaimId/ClaimNumber. Available
Medicaid rule-pack files contain no OR pack. Native claim appeal infrastructure
and #1542 attested PA outcomes do not establish an end-to-end Oregon PA appeal.
Reproduction/trace: a rendered correspondence document has no registered delivery
completion consumer in this host; the PA evidence endpoint neither opens nor
resolves a member appeal or sends an NOABD.

Member services/compliance acceptance: OHA-approved versioned templates and
NOABD/resolution reasons, all-channel triggers/intake, language/accessibility,
delivery/retries/returned mail and timely evidence; urgent handling, state hearing
handoff and continued benefits; reversal updates PA/claims/encounters promptly.
Exercise Oregon clocks, extensions and failure recovery. Requires content/legal
approval, print/electronic partner tests, CCO/delegate ownership and operational
acceptance. Impact: lost appeal/access rights even when clinical calculation is right.

### OR-09 — Adjudication, COB and actual reimbursement terms

**Implemented scoped native logic; Oregon correctness unverified; P0 acceptance.**
Claims host registers `InpatientGroupingStage` before `PricingStage`, followed by
benefits, edits, `CoordinationOfBenefitsStage` and persistence. Grouping uses plan
settings and feeds pricing; required grouping failures pend. Secondary/tertiary
COB with sufficient prior-payer data exists; 68 selected grouping/COB tests passed.
MS-DRG implementation is not proof of every Oregon hospital contract or licensed
alternative grouper. Legacy ffs-service and external-core stubs are not native gaps.

Claims/contracting acceptance: A1/A8 actual methodologies/versions, benefit and
network rules, provider status, COB/TPL and recoveries, authorized rates/reference
licenses; expected-dollar corpus including retroactivity, inpatient/outpatient,
FQHC/RHC, IHCP, same-day behavioral/physical care and assigned special payments.
A10's IHCP policy requires contract/population-specific rates and retroactive rate
updates; verify applicable MMIS identifiers rather than inferring Indian status
from race. Determine who owns supplemental/wrap/reconciliation payments before
coding them. Requires signed terms, reference content/licenses and clinical/finance
approval; external source tests where used. Impact: incorrect member/provider dollars.

### OR-10 — Payments, returns, aging, GL/ERP and required reports

**Partial; P0 for affected finance workflows; product + operations + integration.**
`payment-service/Program.cs` registers transmission/reconciliation and optional GL
dispatch; `NachaSettlementMatcher` persists `PaymentFileSettled` in the insert-only
settlement with outbox. `ar-service/Gl/GlPostingService`/`GlEntryBuilder` consume it.
Whole credits-only bank-confirmed claims settlement is implemented, not a missing
feature. Ten settlement-matcher and 26 GL posting checks passed. In
`shared/CloudHealthOffice.Finance.Contracts/GlSourceEvents.cs`, return/capitation/
premium-cash remain unproduced seams; `ArBalancesController.GetAgingSummary` sums
stored buckets, not due-date open items. This does not prove all ERP functions absent.

CFO acceptance: adjudication-to-payment/835-to-bank returns/reversal/reissue-to-GL/
ERP dollar reconciliation, authoritative due dates, period controls/opening balances,
clean-claim clocks/exceptions/interest and recoveries/disputes. Obtain applicable
A1 financial/expenditure/quality deliverables and executed payment terms; do not
substitute FFS fees or commercial DCBS interest policy. Reconcile OHA revenue,
delegated payments, provider capitation, encounters and claims through two closes.
Requires bank/processor/ERP credentials/certification, accounting policy, state
report definitions, finance/compliance approval. Impact: stranded liability/cash,
incorrect aging and unreconciled reports. Manual seams need controlled ledger and
accepted operating capacity, not silently omitted events.

### OR-11 — Cross-benefit and waiver coordination

**Unverified applicability/end-to-end behavior; P0 when assigned; all five work types.**
A1/A6/A10 require a responsibility matrix before exclusions. September 2026 HRSN
guidance describes MMIS encounters plus separate Exhibit L, denial and service-
coordination reporting; HRSN cannot simply be excluded from OR-02 as nontraditional.
Keep HRSN covered benefits, flexible health-related services, SHARE and ILOS funding
separate. Establish NEMT, pharmacy/PBM, dental and behavioral/SUD intake/authorizations,
claims, care coordination, encounters, directory and member-rights interfaces.

Evidence: generic coverage/enrollment lines, claims and PA hosts described above;
no accepted Oregon cross-benefit/delegate/waiver corpus or end-to-end state-report
package was supplied. A repository-wide Oregon/OHA/OHP search located no Oregon
implementation beyond a portal state selection; this is scoped negative evidence,
not proof that no partner-operated workflow exists.

Clinical/delegation lead acceptance: every assigned benefit has explicit data and
financial owners, effective intervals, source/control totals, closed-loop referral/
transition handling, retained OHA/ODHS coordination and delegated reconciliation.
Scope duals, LTSS/HCBS/EVV, HRSN, reentry and YSHCN by applicable populations and
approved protocols. Requires actual executed materials, partner connections,
clinical/compliance approval and workload acceptance. Impact: benefit or coordination
loss hidden by a medical-only test corpus.

### OR-12 — Production identity, synthetic exclusion and recovery

**Scoped implementations credited; deployment unverified; P0; deployment + operations.**
`shared/CloudHealthOffice.Infrastructure/Middleware/TenantMiddleware.InvokeAsync`
uses validated token tenant and rejects header conflict; `ChoServiceTokenSource`
supports workload-identity token exchange with local keys restricted to development/
testing. FHIR `FhirDataBindings`/router provide native bindings;
`FhirSyntheticSourceRegistration` guards selected mocks, not unconditional startup.
`PasController.ClaimSubmit` requires recorder success; `HttpPasAuthorizationRecorder`
writes authoritative authorization before success, with readback on ambiguous writes.
`Program.cs` registers `MongoDtrResourceStore`. `CoverageLookup.ReadAsync` preserves
unavailable errors; `TemporalAccumulatorRegistration` selects explicit unavailable
outside development/testing. Historical accumulation remains unavailable, not fabricated.

Executed security/gateway, native-source, real-Mongo DTR, PAS boundary and eligibility
checks are listed below. They do not validate all deployed identities/data.
Security/operations acceptance: Azure/GCP workload identity and token grants,
tenant/member negative-access matrix, synthetic disabled/source inventory, audit/
retention, key rotation, backup/restore and incident/replay/on-call evidence. Test
selected managed-Mongo compatibility/topology, watchdog/outbox tenant-scoping
restrictions and dependent services. Requires target-cloud credentials, security
and operations sign-off. Impact: confinement, availability and unrecoverable state.

### OR-13 — 250,000-member workload and launch acceptance

**Unverified; P0 launch gate; deployment + operations + external acceptance.**
No accepted forecast, environment measurements or OHA readiness/transition package
was supplied. Source evidence and historical benchmark totals cannot determine
claims/day, enrollment peaks, PA arrival rate or call concurrency from membership.

Operations lead acceptance: actual utilization/encounter/delegated volumes, peak
rosters, PA/call-center concurrency, retention/history and payment/batch windows;
plan/contract SLO/RTO/RPO. Measure sustained and burst load, duplicate storms,
backlog drain, dependency loss, idempotent replay, reconciliation, recovery and
restores in selected Azure/GCP; retain raw measurements/pass-fail targets. Include
support staffing, runbooks, incident exercises and OHA/partner readiness, transition,
certification and plan sign-off. Requires cloud/test access and agreed contractual
acceptance package. Impact: service failures and unreconciled queues at launch.

### OR-14 — PA annual evidence and separate CMS-0057-F workstream

**Partial overall; implemented evidence foundation; P0/P1 by actual operating dates.**
A7/A9 apply by payer/rating period, not by software label. #1542's
`PaReportingController.RecordFact` requires decision and evidence permissions and
token recorder; `AuthorizationRepositoryMongo` embeds server-owned receipt evidence,
revision-fenced determinations/idempotent facts, and streamed tenant/LOB cohorts.
`AnnualPaReporting` applies effective/knowledge cutoffs and returns
`publicationReady: false`. Tests rerun here include five real-Mongo cases and
seven real-host permission/cohort cases. This is reusable Oregon foundation.

Reporting/compliance acceptance: authoritative appeals/extensions across every
relevant channel/level, source completeness/legacy backfill, original request
linkage, lawful extensions, plan versus tenant/LOB mapping, required-items/services
list, approved cohort/timing/partial-denial/restatement semantics; reconcile OHA
reporting definitions and supplied data. Persist a reconciled freeze and immutable
approved artifact. Precommit timestamps/cursors are not a frozen reporting snapshot;
old full-document writers must not erase evidence. No public reporting is performed.
Requires source agreements, compliance approval, performance/recovery acceptance
and publication procedures. Impact: incomplete annual measures despite correct draft math.

Retain a separate comprehensive CMS workstream for Patient Access usage metrics,
Provider Access attribution/opt-out, P2P consent/lookback and complete clinical/
document exports, PA discovery/documentation/response, API-specific Bulk behavior
and deployed conformance. `BulkExportService` still returns 501; endpoint presence
cannot establish compliance. The CCO retains its obligations; delegated administrator
and interoperability vendor duties require explicit allocation. Do not postpone
already-effective 2026 process/reporting duties to the 2027 API date. Launch/rating
period can move this work ahead of later backlog steps.

## Older findings explicitly reconciled

| Older claim | Current disposition |
| --- | --- |
| Header-derived tenancy / absent service authentication | Scoped token-derived tenancy and service identity are Implemented; OR-12 deployment acceptance remains Unverified. TenantMiddleware and ChoServiceTokenSource were inspected; negative tenancy checks executed. |
| Native FHIR cannot start without synthetic permission | Selected-mock guards and native bindings are Implemented; executed native-source registration tests credit the correction. Deployed source completeness remains Unverified. |
| PAS/DTR persistence absent | Scoped persistence is Implemented; PAS recorder/controller write-before-success and Mongo DTR were inspected; controller-boundary and real-Mongo DTR tests executed. Full exchange/restore acceptance remains Partial. |
| Coverage errors become ineligibility; temporal values fabricated | Explicit unavailable behavior is Implemented and tested; a historical accumulator source remains a Confirmed gap in that selected temporal feature, not invented zero benefits. Actual historical-query launch use remains to be established. |
| COB secondary/tertiary absent / DRG not connected to pricing | Scoped native behavior is Implemented and selected behavioral tests passed. Oregon methodology and dollar correctness remain Unverified under OR-09; no Medicare pricing mandate is inferred. |
| ACH settlement never reaches GL | Scoped bank-confirmed credits-only path is Implemented and tested; broader financial close remains Partial under OR-10. |
| Stedi implementations are stubs | Gateway registration selects real Stedi clients when configured/selected; lifecycle mappings/stores exist for eligibility, claims, acknowledgments, status, attachments and remittance. Selected gateway/configuration checks executed. No Oregon trading-partner connection or certification is inferred; live integration remains Unverified. |
| Legacy ffs-service / vendor stubs block native core | Not applicable under native contract reimbursement ADR 016 and engagement boundary ADR 027. This does not waive OR-06 capitation sync or OR-09 contract acceptance. |
| Oregon enrollment/encounter/provider/PA/reporting already complete | Unverified as a whole; generic workflows are credited individually. Scoped source searches and production bindings establish OR-02/03 limitations, and no accepted Oregon package was supplied. Texas and California implementations do not close Oregon-specific work. |

## Recommended PR sequence and external gates

1. **OR-00 scope/configuration contract**, alongside identity/enrollment OR-01 and
   security OR-12. Acquire state guides, executed materials and test access now.
2. **Provider and policy correctness:** OR-03 authorized Oregon verification,
   history/freshness/registration; OR-04 benefit/EPSDT/date transitions; OR-05
   service deadlines; OR-09 signed reimbursement/COB corpus. Do not transplant TX seeds.
3. **Consistency:** OR-06 effective capitation/delegate propagation; OR-07 atomic
   panel reservation and scheduled reconciliation, replacing the characterization
   test with the desired capacity assertion. Split by durable data boundary.
4. **OR-02 Oregon encounter chain:** accepted source/receiver contract, durable
   handoff and per-record responses, corrections/voids, completeness and state
   acceptance. Implement assigned HRSN/delegated reporting with OR-11.
5. **Member rights and finance:** OR-08 delivered notices/PA appeals/hearings;
   OR-10 return/accounting/open-item/ERP close and required reports; OR-14
   authoritative reporting sources, freeze and approved publication artifacts.
   Rights/finance design can run in parallel and must precede affected launch.
6. **OR-11 assigned cross-benefit/waiver workflows; OR-13 acceptance:** partner/state
   certification, target-cloud load/replay/restore, close simulations, call-center
   acceptance and readiness sign-off. Bring CMS APIs forward if launch requires them.

Each repair PR needs its finding ID, failing regression or replay before repair,
production binding/persistence/consumer evidence, migration/rollback plan and exact
validation. Larger financial/exchange changes need a scoped ADR per CONTRIBUTING.
External acceptance gates are separate from code PR completion. QNXT/Facets/
HealthEdge adapters are **Not applicable** to the selected native-core architecture
under ADR 027 unless an actual engagement changes that architecture. Medicare,
commercial, Bridge, pharmacy POS and LTSS absence is not an unconditional Medicaid
launch blocker; assigned benefits and dual coordination cannot be excluded by default.

## Validation record

SDK **8.0.426**, standard **VSTest 17.11.1 arm64**. Synthetic data only. Baseline
application code unchanged; three test files add the OR consumer case, OR batch
row and deterministic PCP concurrency characterization. Green reproductions are
evidence of current limitations, not fixes or Oregon conformance.

Common command: `dotnet test <project> --no-restore -m:1 --filter '<filter>'
--verbosity quiet -p:UseSharedCompilation=false -p:UseAppHost=false`.
Filters below OR-combine each name prefixed `FullyQualifiedName~`.
Restore-enabled reruns omitted `--no-restore` and added `-p:NuGetAudit=false`.

| Project | Executed filter classes | Passed |
| --- | --- | --- |
| `src/services/enrollment-import-service/EnrollmentImportService.Tests/EnrollmentImportService.Tests.csproj` | EnrollmentImportServiceTests, EnrollmentImportServiceDependentTests | 133 |
| `tests/CloudHealthOffice.ProviderEnrollmentService.Tests/CloudHealthOffice.ProviderEnrollmentService.Tests.csproj` | StateEnrollmentGateTests, MultiStateEnrollmentAggregatorTests | 27 |
| `tests/CloudHealthOffice.EncounterSubmissionService.Tests/CloudHealthOffice.EncounterSubmissionService.Tests.csproj` | BatchIntegrityTests, AdjudicationConsumerTenantTests | 15 |
| `src/services/coverage-service/CoverageService.Tests/CoverageService.Tests.csproj` | PcpAssignmentServiceTests, HttpPanelCounterTests | 28 |
| `src/services/authorization-service/AuthorizationService.Tests/AuthorizationService.Tests.csproj` | AnnualPaReportingTests, PaReportingEvidenceTests, AuthorizationDeadlinePolicyTests, PaReportingMongoTests | 34 |
| `tests/CloudHealthOffice.AuthorizationService.Tests/CloudHealthOffice.AuthorizationService.Tests.csproj` | PaReportingAuthenticationTests | 7 |
| `tests/CloudHealthOffice.EligibilityService.Tests/CloudHealthOffice.EligibilityService.Tests.csproj` | CoverageLookupFailureTests, TemporalAccumulatorRegistrationTests | 25 |
| `tests/CloudHealthOffice.ClaimsService.Tests/CloudHealthOffice.ClaimsService.Tests.csproj` | InpatientGroupingStageTests, CoordinationOfBenefitsStageTests, CoordinationOfBenefitsDecisionTableTests | 68 |
| `tests/CloudHealthOffice.FhirService.Tests/CloudHealthOffice.FhirService.Tests.csproj` | FhirSyntheticSourceRegistrationTests, MongoDtrResourceStoreTests; the additional PasAuthorizationRecorder substring matched no tests | 51 |
| `tests/CloudHealthOffice.PaymentService.Tests/CloudHealthOffice.PaymentService.Tests.csproj` | NachaSettlementReconciliationTests | 10 |
| `src/services/ar-service/ArService.Tests/ArService.Tests.csproj` | GlPostingServiceTests | 26 |
| `src/services/shared/CloudHealthOffice.Infrastructure.Tests/CloudHealthOffice.Infrastructure.Tests.csproj` | TenantMiddlewareTests, StediHealthcareGatewayTests, StediConfigurationTests | 37 |

Additional selection: the same FHIR project with `PasSubmitRecordingTests`: **26 passed**.
These use a fake recorder at the controller boundary; they do not establish live
authorization-service/network acceptance. Total across the selections: **487 passed,
zero failed, zero skipped**. Repeated earlier encounter runs are not added.
Five PA Mongo test bodies and the selected DTR Mongo bodies executed against real
ephemeral Mongo; this is not managed-cloud restore/compatibility acceptance.

Initial sandbox VSTest invocations aborted before test execution due to denied
socket binding; authorized outside-sandbox reruns completed. Missing assets for
enrollment/claims and stale FHIR project references required restore. The initial
FHIR build's missing PlanConfiguration types resolved after restore; it was not
an application repair. No tests from aborted/build-failed invocations are counted.
No skip is used to hide these attempts. No full solution build/full regression,
live Stedi/OHA/bank/delegate test, Oregon contract corpus, actual source enrollment,
capitation amendment replay, full financial close, conformance certification,
managed-cloud capacity/restore or regulatory/operational sign-off was performed.
No merge, deployment, state/bank submission or public publication is authorized
or performed by this assessment.

Changed-document relative destinations and `git diff --check` passed.
