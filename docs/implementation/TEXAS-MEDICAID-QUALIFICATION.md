# Texas Medicaid qualification and remediation backlog

Assessment date: October 24, 2026 (America/Phoenix). Source baseline:
`9cea3b614ae006c96d0a9aafdad5937ad4f8510c` on main, including merged
[PR #1542](https://github.com/aurelianware/cloudhealthoffice/pull/1542).
Assessment branch: `assessment/texas-medicaid-1542`. The original checkout was
clean on `docs/current-capability-refresh` at `bf0311dd1eccde0a677a6a2abddf1292552959be`;
that branch remains available. No unrelated files were changed. No root AGENTS.md
was found; the cloudflare-worker-specific instructions do not cover these files.
CONTRIBUTING, the canonical ledger, acceptance workstream, active gap matrices,
ADRs 006/010/016/017/024/026/027/030 and relevant runbooks were consulted.

The [canonical capability ledger](../CAPABILITY_RECONCILIATION.md) owns status.
This document supplies Texas applicability, evidence details and acceptance work;
it is not a competing capability authority. All source paths below are relative
to the repository and pinned to the baseline above. Scoped implementation is
separate from external certification, deployment acceptance and compliance approval.

## Assessment conclusion

A final launch determination is **unverified**. No executed contract, service-area
award, launch/rating-period date, benefit responsibility matrix, state interface
package, partner acceptance record or target-cloud acceptance corpus was supplied.
STAR medical Medicaid is the assessment baseline, not an assertion about an award.
Confirmed inspected limitations include a misapplied gold-card exemption, generic
PA clocks, the Florida-only encounter submission path, capitation child sync,
PCP panel concurrency and incomplete correspondence fulfillment. These are narrower
findings than claiming enrollment, claims, provider administration or finance absent.

PR #1542 is merged as `9cea3b61`; it advances PA reporting to a durable evidence
foundation and annual drafts. It does not deliver a PA appeal adjudication workflow,
state notices, source completeness, an approved publication or Texas readiness.

## Applicability and responsibility matrix

| Scope / requirement | Current applicability | Owner and evidence required |
| --- | --- | --- |
| STAR programs, service areas, populations, start and rating periods | Baseline only; actual contract scope unresolved | Plan executive/compliance: executed award, contract and amendments, counties/regions, covered population and transition dates; identify controlling versions before acceptance |
| Medical, behavioral health, EPSDT, medical drugs and other assigned services | Responsibility unresolved at benefit level | Clinical/benefits: effective-dated benefit/carve-out/delegation map; include any assigned benefit even when subcontracted |
| STAR+PLUS, STAR Kids, LTSS/EVV/service coordination, dual coordination | Conditional; no assignment established | Compliance: signed program scope and delegated accountabilities; then applicable program manuals, EVV/service-plan interfaces and transition scenarios |
| CHIP, dental, pharmacy POS, Medicare, exchange | Not applicable to the assumed STAR medical baseline; reassess on assignment | Plan compliance: record exclusions/assigned responsibilities; medical drugs must still be scoped separately from pharmacy POS |
| Medicaid eligibility vs MCO enrollment | Distinct sources and effective periods required | Enrollment: HHSC/broker source-of-truth matrix; actual enrollment/reconciliation layouts, identifier/crosswalk rules, control totals and accepted test corpus; a 271 is not plan enrollment |
| HHSC contract and UMCM | Contractual authority; incorporated chapters/versions unknown | Compliance: obtain executed attachments, amendments, launch-effective UMCM and transition provisions, including claims/encounter, enrollment, network, appeals and financial reporting specifications |
| Encounter destination and operating entity | Required for assigned reportable services; actual receiver not established | EDI lead: identify HHSC-designated receiver/operator and whether TMHP or another entity operates each route; contracted clearinghouse handoff, guides, test IDs, acknowledgments and final acceptance evidence |
| TMHP PEMS-derived verification | State enrollment and revalidation distinct from credentialing/contracting/directory | Provider operations: authorized source agreement and location/NPI/taxonomy/program/effective-date records; no universal public API or nightly CSV format assumed |
| Directory/network/access reporting; 274 | General network duties apply; exact reports/274 conditional on contract | Network lead: effective specifications, service-area adequacy/access tests, directory reconciliation and accepted delivery records |
| Bank, processor, ERP and delegated entities | Plan owns operations; execution may be delegated | CFO: responsibility matrix, contracts, test access, opening balances, accounting policy and bank/ERP/subcontractor reconciliation |
| CMS-0057-F | Medicaid MCO category; separate comprehensive workstream retained | Compliance: launch/rating-period applicability, exact API/report/process provisions and approved interpretation; shared prerequisites below |
| Capacity and recovery at 250,000 members | Membership is not a transaction forecast | Operations: peak enrollment/claims/encounters/PA volumes, call-center concurrency, history/retention, batch windows, SLO/RTO/RPO and contracted acceptance targets |

No fee-for-service TMPPM rule is adopted for managed care solely because a seed
or website names it. Each configured restriction needs a law/contract/incorporation
or applicable program-policy citation, effective version and clinical approval.

TX-00 is the unresolved applicability contract above: compliance owns the signed
program/service-area/benefit/date and interface version inventory. It is a P0
configuration/external-evidence dependency, classified Unverified. Acceptance is
a versioned matrix approved by plan executives and compliance, with each delegated
responsibility assigned to an operating entity and linked evidence owner.

## Authorities and evidence-access limits

Reviewed official sources on the assessment date:

- [Texas Government Code Chapter 540](https://tcss.legis.texas.gov/resources/GV/htm/GV.540.htm):
  §§540.0257, .0265 and .0302–.0307, effective April 1, 2025 in the inspected
  codification. Certification-date notification, prompt-payment and PA provisions
  inform the scenarios below. Contract applicability and amendments need plan approval.
- [Insurance Code Chapter 4201](https://tcss.legis.texas.gov/resources/IN/htm/IN.4201.htm),
  especially §4201.652, and [TDI exemption FAQ](https://tdi.texas.gov/health/hb3459-faq.html):
  Medicaid/CHIP MCO coverage is excluded from this exemption law. HB 3812's
  September 1, 2025 evaluation changes do not extend the exemption to Medicaid.
  TDI is used here to establish the boundary of its own provision, not as STAR's regulator.
- [TMHP PEMS guide](https://www.tmhp.com/topics/provider-enrollment/pems/start-application),
  updated March 10, 2026, and [HHSC enrollment/revalidation](https://hhs.texas.gov/node/2232):
  enrollment/revalidation and separate MCO credentialing/contracting must be checked.
  Revalidation notices and program/location details need source acceptance.
- [Current 42 CFR Part 438](https://www.ecfr.gov/current/title-42/chapter-IV/subchapter-C/part-438):
  §§438.10, .68, .206–.210, .230, .242 and .400–.424. These govern the applicable
  information, network, delegation, data, authorization and appeals boundaries;
  future/staged provisions require rating-period review.
- [CMS final-rule resource page](https://www.cms.gov/initiatives/burden-reduction/overview/interoperability/policies-regulations/cms-interoperability-prior-authorization-final-rule-cms-0057-f)
  and [final-rule text](https://www.cms.gov/files/document/cms-0057-f.pdf):
  process/reporting requirements generally begin in 2026; Medicaid MCO API changes
  generally apply for rating periods beginning on or after January 1, 2027.
  Compliance must map the exact payer provision; one universal deadline is inappropriate.

The [HHSC UMCM index](https://www.hhs.texas.gov/services/health/medicaid-chip/managed-care-contract-management/texas-medicaid-chip-uniform-managed-care-manual)
returned 403; the UMCC page and UMCM 3.21 PDF could not be retrieved. UMCM 2.0
search exposed version 2.14 encounter guidance, but the PDF returned 403; this is
not verification of the launch-effective version. A TMHP companion-guide route
was inaccessible; no Texas encounter/enrollment technical specification was
accepted as inspected. No deadlines, file layouts, state acknowledgment semantics,
274 obligation or financial report layout are inferred from these failures.
The existing `appeals-service/RulePacks/medicaid/tx.json` itself records unresolved
UMCM versions/windows. Its claims must be revalidated, not treated as authority.
Texas administrative-rule version verification remains open with compliance;
no third-party TAC mirror was used to declare a requirement closed.

## Material findings and acceptance contracts

All findings apply to the assumed STAR medical population in the **unresolved
contracted service areas** unless explicitly conditional. Every finding inherits
the exact baseline and source-access limits above. P0 means resolve before launch;
P1 may run in parallel but still requires an accepted workflow if assigned.
A manually operated mitigation must have an accountable owner, controls and
accepted capacity; an undocumented workaround does not close a finding.

### TX-01 — Enrollment, identity and downstream reconciliation

**Unverified; P0; plan configuration + external integration + operations.**
`enrollment-import-service/Program.cs` registers raw-file storage, streaming 834
parser, run processor/worker, transaction and event repositories and downstream
member/coverage clients. `EnrollmentImportService` implements member IDs,
coverage-line reconciliation, terminations and reinstatements; `EnrollmentEventPublisher`
persists idempotent events. This is meaningful generic enrollment implementation.
No accepted Texas HHSC/broker feed, crosswalk or service-area/program mapping was
supplied. End-to-end effects on PA, claims, PCP, correspondence and capitation
remain unverified, including state-to-plan premium reconciliation.

Evidence: `Services/EnrollmentImportService.cs` (coverage reconciliation and
reinstatement sections), `Services/Runs/EnrollmentImportRunProcessor.cs`,
`Services/Edi/Enrollment834EdiParser.cs`; behavioral tests under
`EnrollmentImportService.Tests/Services/{EnrollmentImportServiceTests,EnrollmentImportServiceDependentTests}.cs`
and `Services/Runs/EnrollmentImportRunTests.cs`. These were inspected selectively, not run here.
Requirement: contract/interface package and Chapter 540 §.0257; source authority
must distinguish HHSC eligibility, broker enrollment and verification responses.

Enrollment lead acceptance: reconcile initial/full/delta/control feeds and rejected
records; test Texas identifier crosswalk/duplicate collision, program/area moves,
retroactive add/change/term, reinstatement, newborn/dependent and transition
continuity. Replay in duplicate/out-of-order order; prove final member-months,
claims/PA/PCP effects and separate premium versus provider-capitation totals.
Requires proprietary guides, partner credentials/testing and state/plan acceptance.

### TX-02 — Texas encounter submission and final acceptance

**Confirmed gap in the inspected automated path; P0; product + integration.**
`encounter-submission-service/Program.cs` registers `EncounterSubmissionServiceImpl`,
Kafka `AdjudicationCompletedConsumer` when enabled, and `EncounterSubmissionWorker`.
The consumer's state gate admits FL Medicaid only. The builder rejects non-FL
records and emits `FMMIS_*.json`; workers stage the file. General
`encounter-service`/EncounterEngine 837 generation is not an accepted Texas route.
No executing Texas alternative was established by the source search; platform-wide
absence of any external partner workflow is not asserted.

Reproduction: process a TX Medicaid adjudication in the consumer: its state gate
returns before creating a tracking record. Call `BuildFmmisSubmissionBatchAsync`
with `StateCode = "TX"`: `NotSupportedException` precedes reads/writes. Existing
`BatchIntegrityTests.WrongTenantOrState_IsRefusedBeforeFetchingOrWriting` exercises
the same guard with CA; the TX result follows the identical non-FL condition.
The batch/999 fixes from #1541 are implemented: missing/wrong claim fetches refuse
the batch and positive 999s become `SyntaxAccepted`, not final `Accepted`.
Do not reopen those defects or transfer the Florida 60-day rule to Texas.

EDI lead acceptance: agree actual HHSC destination/operator, source/control identity
and partner handoff; implement durable reservations/outbox, validation, retries,
reject/correction/replacement/void and per-record final response reconciliation.
Reconcile reportable adjudicated, capitated/delegated and other assigned services,
including services with no FFS payment. Maintain distinct transport, clearinghouse,
transaction and state acceptance evidence. Requires controlling HHSC specifications,
credentials, partner testing and state acceptance; scope/layout is blocked on TX-00
(the applicability matrix), not permission to relabel FMMIS as Texas.

### TX-03 — PEMS registration exists; source/history/location acceptance does not

**Partial; P0; product + external integration + configuration.**
`benefit-plan-service/Program.cs` and `fhir-service/Program.cs` call
`AddProviderEnrollmentService(...).UseMongoRepositories().WithTenantConfigCache().WithTexasSource()`.
`StateEnrollmentGate` uses token-derived tenant, configured enforcement and checks
status, effective/termination dates, taxonomy and LOB. Null config/disabled mode
passes intentionally; deployment must prove enforcement is configured.
`TmhpPemsSource.GetEnrollmentAsync` has real HTTP/cache logic and `BulkSyncAsync`
has SFTP parsing, but comments asserting a `/provider/{npi}` API, rate limit and
nightly CSV are not an accepted TMHP interface agreement.

Concrete limits: the source ignores its `asOfDate` parameter and the repository
upserts one record per NPI/state, so it cannot establish enrollment history after
an overwrite. Location-specific records are not represented by that cache key.
On HTTP failure it returns cached state without forcing a stale marker; the gate
does not enforce a freshness threshold. `WithTexasSource` registers a typed client
but separately registers `IStateEnrollmentSource, TmhpPemsSource`; resolving the
interface can use the default HttpClient instead of the configured typed client.
The latter registration risk still needs a composition test before defect closure.
`NightlyBatchSyncWorker` is a one-shot hosted service; neither production host calls
`WithNightlyBatchSync` ; its builder method exists but is not invoked there.
A named worker is not an accepted daily refresh schedule.

Provider lead acceptance: authorized verified source/import, captured source dates,
location/NPI/taxonomy/program effective intervals, historical retroactivity and
freshness/unavailability policy; add real-host registration tests and replay
revalidation/disenrollment. Reproduce history loss by upserting two intervals for
the same NPI/state and querying the earlier service date. Existing
`TmhpPemsSourceTests`/`StateEnrollmentGateTests` cover simulated cache/HTTP and gate
cases, not live PEMS. Keep state enrollment separate from credentialing, contracting,
network participation and directory/access reporting. 274 stays conditional on the
actual specification. Requires credentials/source agreement and operational acceptance.

### TX-04 — Seeded Medicaid gold-card exemption uses the wrong authority

**Confirmed gap; P0; product/content defect.**
`TxMedicaidSeedRules.GetAll` includes enabled platform regulatory-exemption rules
for STAR/STARPlus/STARKids. Both production hosts call `WithPlatformRules().SeedOnStartup()`.
`TxGoldCardExemptionRule.EvaluateAsync` returns Approve for qualifying history;
`PriorAuthRuleEngineService.EvaluateAsync` stops on the first decision before later
clinical rules. TDI's own applicability FAQ excludes Medicaid MCOs from §4201.652;
a plan-approved voluntary PA policy would require a separate explicit authority.

Reproduction: existing `TxGoldCardExemptionRuleTests.Evaluate_QualifyingHistory_ReturnsApprove`
passes a TX Medicaid STAR context with 23 approvals of 25 decisions and receives
Approve. This test proves the problematic behavior; green does not establish its
legal correctness. Seeds have no service-code restriction for this exemption.

Clinical/compliance + engineering acceptance: remove/quarantine the statutory
Medicaid exemption, migrate/disable previously persisted platform rules with audit
and cache invalidation, and prove qualified history cannot bypass applicable STAR
review. Any voluntary waiver must be separately configured, sourced, versioned and
approved. Regression must exercise production seeding, stored rules and engine
ordering, including already-deployed data. No credentials needed for core repair;
clinical/compliance approval and rollout reconciliation required. Other TMPPM seed
limits remain unverified until incorporated-policy applicability is established.

### TX-05 — Operational PA deadlines lack Texas scenario selection

**Confirmed gap in native deadline calculation; P0; product + plan policy.**
`AuthorizationRepositoryMongo.CreateAsync` initializes `AuthorizationDeadlinePolicy`;
`SlaWatchdogService` reads its deadline. `DueAt` accepts only receipt and urgency,
then adds 72 or 168 hours. It has no Texas program, hospitalization, discharge,
poststabilization or business-calendar selector. Chapter 540 §§.0302–.0307 includes
shorter scenario-specific limits; the actual contract remains a required input.
This does not assert that every clinical operator currently waits for the watchdog.

Reproduce with `AuthorizationDeadlinePolicy.Initialize` on a nonurgent request
received Monday October 19, 2026: it sets October 26, while the nonhospitalized
three-business-day scenario requires an earlier deadline. Existing deadline tests
confirm 168/72-hour arithmetic rather than Texas scenario compliance. A one-hour
scenario also cannot be represented by the current policy selector.

Clinical/engineering acceptance: approved versioned state/program/scenario and
holiday rules; preserve original payer receipt across all channels; support
urgency changes, lawful extensions and peer consultation/reconsideration without
resetting federal/contract clocks. Test notice deadlines as well as decisions,
weekends/holidays/DST, outage recovery and all intake channels. Retain the tighter
applicable obligation. Requires current contract/UMCM, compliance content and
operational acceptance; no generic FHIR endpoint can close this.

### TX-06 — Delegation and capitation amendment propagation

**Partial, with confirmed child-sync gap; P1 (P0 if used at launch); product + configuration.**
`provider-contracts-service/Controllers/ProviderContractsController.SyncChildren`
returns 501 for an existing contract; denormalized capitation child fields are
not propagated. Reproduce with an authorized PUT to an existing contract's
`sync-children`. FFS reimbursement already lives in contract terms (ADR 016), so
legacy ffs-service containerization is not remediation. State premium revenue
is a separate ledger from provider capitation.

Contracting/CFO acceptance: effective-dated benefit/financial-responsibility and
delegate ownership; amendment/termination propagation, child-version audit,
retro-enrollment recoupment/overpayment and subcontractor reconciliation. Test a
mid-month amendment plus retroactive member change and retry without duplicate
payment. Review manual mitigation only with controlled reconciliation and volume
acceptance. LTSS/EVV/service coordination are dependencies if assigned, not blanket
STAR blockers. Requires contracts/rates, delegate tests and finance approval.

### TX-07 — PCP capacity race and unscheduled reconciliation

**Partial, with confirmed concurrency gap; P1; product + operations.**
`PcpAssignmentService.AssignAsync` checks panel count, ends prior assignments,
then inserts separately; two different members can both see one free slot and
both succeed. `PcpPanelReconciliationJob.ScanAsync` can identify overages but is
registered scoped without a host scheduler. `HttpPanelCounter` now fails closed
on unavailable counts (#1541); that fixed bypass is not an open defect.

Reproduction design: barrier both count reads at limit minus one, release both
assignments and assert the resulting count exceeds the limit. No concurrent test
was executed here; source operations demonstrate no shared reservation boundary.
Provider operations/engineering acceptance: authoritative effective-date/network/
program panel scope; atomic reservation and rollback/idempotency; member choice,
reassignment, transition exceptions, scheduled reconciliation, communications and
downstream consistency. Test concurrent members and retries plus future/retro
assignments. Requires approved assignment policies and operator acceptance.

### TX-08 — Notices, delivery and PA appeals remain incomplete

**Partial; P0; product + approved content + fulfillment integration.**
`correspondence-service/Program.cs` registers composer/resolver/renderer and Mongo
repository. The executing controller supports composition/rendering; no domain
trigger/delivery worker is registered there. The architecture's step-1 boundary
explicitly leaves triggers/channels/individual-letter approval for later steps.
`appeals-service` has durable state/rule-pack/outbox behavior, but `CreateAppealRequest`
requires ClaimId/ClaimNumber; it is not an end-to-end PA appeal producer.
#1542's fact endpoint attests outcomes and extensions, not appeals or legal grants.

Compliance/operations acceptance: approved HHSC templates and effective versions,
languages/accessibility, all adverse claim/PA triggers, delivery/retry/returned mail,
proof of timely notice, expedited appeal/fair-hearing handoff and continuation/
reinstatement of benefits. Federal §438.408 limits standard appeal resolution to
30 days and expedited to 72 hours, with conditional extensions; §438.420 benefit
continuation has eligibility conditions. Texas-specific tighter clocks/forms and
current rule-pack `toConfirm` items require the unavailable UMCM package. Reproduce
the fulfillment gap by tracing a decision to a rendered letter and showing there
is no registered channel completion/receipt consumer in this host. Requires print/
electronic partner tests, approved content, compliance and operations acceptance.

### TX-09 — Payments implemented; integrated close and Texas payment policy partial

**Partial; P0 for payment/close correctness; product + finance + external integration.**
Payment host registers NACHA transmission, bank reconciliation and optional GL
outbox/dispatcher. `NachaSettlementMatcher` produces `PaymentFileSettled` for whole
credits-only claims files; `ar-service/Gl/GlPostingService` and `GlEntryBuilder`
consume it. ADR 026 and NACHA/BAI2/GL runbooks define the bounded path. Do not repeat
the old “no settlement-to-GL” finding. `GlSourceEvents` has no produced return,
provider-capitation or premium-cash event; `ArBalancesController.GetAgingSummary`
sums stored buckets rather than deriving overdue open items. That does not establish
all ERP functionality is missing.

CFO acceptance: authoritative open items/due dates, clean-claim receipt and exceptions,
interest policy, COB/TPL/recoveries/disputes; dollar-level adjudication-to-835-to-bank
settlement/return/reversal/reissue-to-GL/ERP reconciliation, duplicate/restart/period
controls and two close simulations. Chapter 540 §.0265 includes a 45-day ordinary
claim limit and different assigned LTSS/facility limits; do not import unrelated
TDI commercial prompt-pay penalties. Exact interest obligations and HHSC financial/
statistical reports require applicable contract/rules, not a guessed universal rate.
Reproduce the bounded missing producer by following the return/capitation/premium
path to the event inventory: no corresponding GL event type/consumer exists there.
Requires bank/processor/ERP credentials/testing, accounting policy, opening balances
and state report definitions/acceptance.

### TX-10 — Adjudication and inpatient reimbursement applicability

**Implemented scoped native logic; Texas contract correctness unverified; P0.**
Claims host registers `InpatientGroupingStage` before `PricingStage` and
`CoordinationOfBenefitsStage`. Grouping is selected by plan setting and pricing
uses its result; failures pend rather than silently falling back when grouping
is required. Secondary/tertiary prior-payer-complete COB is implemented; the legacy
pend-code name is not proof all secondary claims are unsupported. ADR 024 supports
MS-DRG/repricing integration, not a licensed Texas APR-DRG grouper or every payment
methodology. No assumption that Texas medical contracts require Medicare IPPS.

Claims/contracting acceptance: exact methodology/version, licensed tables/grouper
where required, hospital/reference data and billed-vs-regrouped policy; benefit/
network/provider/contract/COB/TPL test corpus with expected dollars, retroactivity,
interest and appeals effects. Existing `InpatientGroupingStageTests`, pricing
`InpatientGroupingTests` and COB tests provide behavioral evidence but were not
rerun here. Requires contract/rates/reference licenses, clinical and finance approval.

### TX-11 — Security, synthetic exclusion and recovery

**Scoped security implementations exist; target deployment unverified; P0.**
Production FHIR native bindings and `FhirSyntheticSourceRegistration` guard selected
mocks; `eligibility-service` guards its selected default gateway rather than all
native startup. Coverage failure is explicit unavailable, not a negative enrollment
determination; temporal accumulation uses `UnavailableAccumulatorClient`, not fake
zeros outside approved development. Native PAS recorder persists before responding;
DTR Mongo persistence is implemented. These closures do not establish deployed
identity, service-token issuance or native data completeness.

Security/operations acceptance: token-derived tenant/member confinement, service
identity provisioning, least privilege, audit/retention, secret rotation, synthetic
disabled and selected source inventory; test negative tenant/member access and
upstream outages on intended Azure/GCP. Prove restore, replay, incident response,
runbooks/on-call and HHSC readiness-review evidence. Database-per-tenant modes have
explicit watchdog/outbox limitations; test the selected topology. Requires target
cloud access and plan/security/operations sign-off, not state data in Git.

### TX-12 — Throughput and launch acceptance

**Unverified; P0 launch gate; deployment + operations.** No accepted workload,
capacity/recovery target or HHSC readiness package was supplied. Benchmark counts
are not a 250,000-member operational acceptance result. Operations must derive
traffic from utilization, provider feeds, batch sizes and channel concurrency;
measure sustained/peak behavior, duplicate storms, lost dependencies, queue drain,
backlog recovery and restore at agreed history/retention. Record run environment,
raw measurements, reconciliation totals, bottlenecks and pass/fail targets. Include
call-center authorization/audit flows, payment cutoff and enrollment peaks.
Requires target environment, partner/state readiness and plan-approved SLO/RTO/RPO.

### TX-13 — CMS reporting after merged #1542 and subsequent interoperability work

**Partial overall; implemented evidence foundation; P0/P1 by launch/rating period.**
`PaReportingController` requires both decision and evidence permissions to record
facts; actor/tenant are token-derived. `AuthorizationRepositoryMongo` creates a
server-owned ledger, captures determinations on revision-fenced writes, pushes
idempotent sourced facts and streams tenant/LOB cohorts. `AnnualPaReporting` applies
separate effective/knowledge cutoffs and returns `publicationReady: false`.
Behavioral tests cover BSON precision, retries, receipt scope, final appeals,
legacy/conflict diagnostics and HTTP permission gates. The source boundary is
shared with Texas; a California PR label does not make it California-only.

Reporting/compliance acceptance: native and external PA sources at every relevant
review level/channel, legacy reconciliation, original request linkage, lawful
extension evidence, plan-vs-tenant/LOB mapping, required items/services list,
approved cohort/timing/restatement semantics, persisted reconciled freeze and
immutable publication artifact. In-flight precommit timestamps/cursor reads do
not give a transactionally frozen report. Old full-document writers can erase the
new ledger; rollout/rollback must preserve it. Follow ADR 030/runbook. No fabricated
history or reconstructed zero outcomes. Requires source contracts, compliance
approval, volume/recovery acceptance and approved publication procedures.

Keep comprehensive CMS work separate: Patient Access usage reporting, Provider
Access attribution/opt-out, P2P consent/lookback and complete clinical/document
export, PA discovery/documentation/response and API-specific Bulk requirements.
Current `BulkExportService`'s 501 is an honest unsupported boundary. Limited
endpoint/fixture inspection cannot establish conformance. Apply exact rating-period
API dates from the rule; do not postpone already applicable operational duties.

## Older findings explicitly rechecked

| Prior claim | Current source conclusion and evidence |
| --- | --- |
| Header-derived tenancy / absent service authentication | Shared `TenantMiddleware.InvokeAsync` resolves token tenant, rejects missing claims and conflicting headers; `ChoServiceTokenSource` supports workload-identity exchange with local signing restricted to development/testing. Claims outbound handlers and deployed identity registration still need deployment acceptance. No blanket absence claim is retained. |
| Native FHIR startup requires synthetic permission | `FhirSyntheticSourceRegistration` guards selected mocks only; native resource/P2P bindings are real. Native data, confinement and deployed startup acceptance remain separate. |
| PAS/DTR are in-memory only | PAS recorder persists before response; Mongo DTR resource store persists questionnaire/response operations. Full profiles/packages and production recovery remain partial. |
| Coverage errors mean ineligible / temporal values fabricated | `CoverageLookup.ReadAsync` wraps non-404 transport/auth/payload failure as unavailable; production `TemporalAccumulatorRegistration` selects Unavailable. Historical accumulator source remains absent; unavailable is not a zero or an eligibility determination. |
| Secondary/tertiary COB absent; DRG not integrated | Registered COB and inpatient grouping/pricing execute; incomplete prior-payer data still pends. Contract-specific Texas payment methodology remains TX-10 acceptance. |
| No ACH settlement-to-GL | Current matcher/outbox/GL entry path exists for whole credits-only claims files; TX-09 retains the precise remaining event/close limits. |
| Stedi gateways only stubs | `HealthcareGatewayServiceCollectionExtensions` calls `AddStediHealthcareGateway` when configured/selected; real clients, mappings and lifecycle stores exist for eligibility/claims/acknowledgment/status/attachments/remittance. Explicit configuration errors do not fall back silently to Mock. Existing infrastructure Stedi tests were located, not rerun. No trading-partner agreement, Texas receiver certification or live connection is inferred. |
| Legacy ffs-service is a native reimbursement blocker | ADR 016 contract reimbursement terms supersede its FfsRateConfig. Not applicable to the native contract-pricing architecture; capitation copies remain TX-06. |
| Texas implementations are absent | Generic enrollment, registered Texas PEMS gate/source, PA seeds and Texas appeals rule pack exist. Interface acceptance, misapplied seed authority, scenario clocks and reporting contracts remain individually open. No complete accepted Texas state-reporting package was established. |
| Vendor adapter stubs establish native gaps | Not applicable to the selected native-core architecture under ADR 027. An explicitly selected external-core engagement would require separate implementation and acceptance. |

## Recommended PR and acceptance sequence

1. **Policy containment:** TX-04 Medicaid exemption removal plus persisted-rule
   migration; TX-05 state/scenario deadline design and tests. Clinical/compliance
   review is required before enabling policy content. Inventory all TMPPM-derived
   seeds against the applicable managed care sources.
2. **Identity and foundation:** TX-01 accepted feed/crosswalk/program configuration,
   TX-03 source/freshness/history/location and production composition fixes,
   TX-10 benefit/contract/pricing/COB corpus; security acceptance TX-11 in parallel.
3. **Provider/financial consistency:** TX-06 versioned capitation/delegation sync,
   TX-07 panel reservation/scheduler and approved manual mitigations if any;
   TX-09 bank/return/open-item/ERP boundaries under a scoped accounting ADR.
4. **Texas encounters:** TX-02 receiver/partner contract first, then durable handoff,
   per-record state responses, correction/void/replacement and completeness replay.
   Begin guide/test-access procurement immediately; it can run beside steps 1–3.
5. **Member rights and reporting:** TX-08 delivered notices/PA appeals and benefits
   continuation, TX-09 required reports, TX-13 authoritative source adapters and
   reporting freeze/publication acceptance. #1542 is already merged, not a future PR.
6. **Launch gates:** TX-12 load/backlog/restore, state/partner certification, finance
   close, HHSC readiness review and plan operational sign-off. CMS API work follows
   its actual compliance dates and may need to move earlier for the chosen launch.

Each implementation PR must carry its owning finding, failing regression before
repair, revised production binding, replay/rollback plan and the exact tests run.
Do not merge this assessment as a statement that external acceptance is complete.

## Validation record

Executed against `9cea3b61` with .NET SDK 8.0.426 and standard VSTest 17.11.1
(arm64): **116 passed, zero failed, zero skipped** across the selections below.
No temporary reflection runner was used. Historical #1541/#1542 test totals remain
tied to their original commits and are not added to this count.

Common invocation: `dotnet test <project> -m:1 --filter '<filter>' --verbosity quiet
-p:UseSharedCompilation=false -p:UseAppHost=false`; restore-enabled runs additionally
used `-p:NuGetAudit=false` to avoid a network audit during behavioral validation.
The provider and Mongo reruns used `--no-restore` after dependency restore. Each
filter below is OR-combined with `FullyQualifiedName~` for the listed class names.

| Project | Filter classes | Passed |
| --- | --- | --- |
| `tests/CloudHealthOffice.ProviderEnrollmentService.Tests/CloudHealthOffice.ProviderEnrollmentService.Tests.csproj` | TmhpPemsSourceTests, StateEnrollmentGateTests | 30 |
| `tests/CloudHealthOffice.PriorAuthRuleEngine.Tests/CloudHealthOffice.PriorAuthRuleEngine.Tests.csproj` | TxGoldCardExemptionRuleTests, PriorAuthRuleEngineTests | 5 |
| `src/services/authorization-service/AuthorizationService.Tests/AuthorizationService.Tests.csproj` | AnnualPaReportingTests, PaReportingEvidenceTests, AuthorizationDeadlinePolicyTests | 29 |
| `tests/CloudHealthOffice.AuthorizationService.Tests/CloudHealthOffice.AuthorizationService.Tests.csproj` | PaReportingAuthenticationTests | 7 |
| `tests/CloudHealthOffice.EncounterSubmissionService.Tests/CloudHealthOffice.EncounterSubmissionService.Tests.csproj` | BatchIntegrityTests, AdjudicationConsumerTenantTests | 13 |
| `src/services/authorization-service/AuthorizationService.Tests/AuthorizationService.Tests.csproj` | PaReportingMongoTests | 5 |
| `src/services/coverage-service/CoverageService.Tests/CoverageService.Tests.csproj` | HttpPanelCounterTests, PcpAssignmentServiceTests | 27 |

The PA rule filter matched five gold-card cases; it is not a claim that the full
rule-engine suite ran. Mongo test bodies actually executed with the real ephemeral
server, resolving the earlier PR-local execution limit for this baseline/run.
This does not prove compatibility with managed Mongo or cloud configuration.

Initial sandbox attempts encountered missing restore assets and denied test-runner
socket binding; authorized runs outside the sandbox completed normally. Initial
wrong working-directory/unsupported CLI-switch attempts ran no tests. No runner
crash remains an unresolved blocker for the selections reported above.

Not run: full solution build/full regression, enrollment behavioral/Mongo suites,
claims/COB/grouping, FHIR/DTR/PAS suites, Stedi/live tests, capitation/finance replay,
PCP concurrency reproduction, managed-cloud performance/restore, partner/state/bank
certification, conformance validation and compliance approval. These are explicit
acceptance limits, not hidden passes. No production data was used or submitted.
Documentation relative destinations and `git diff --check` were checked before PR
creation. No application behavior, deployment, merge or publication was performed.
