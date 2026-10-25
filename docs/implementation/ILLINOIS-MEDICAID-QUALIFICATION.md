# Illinois Medicaid qualification and remediation backlog

Assessment: October 24, 2026, America/Phoenix. **Prospective Chicago-area Medicaid
plan business, targeting a 2027 plan start**, approximately 250,000 members.
**Launch readiness remains unverified.** Native CHO has substantial implemented
core administration, but the executing automatic encounter route excludes Illinois,
no Illinois state-provider source is registered, and shared consistency, fulfillment,
financial and exchange boundaries remain. These are actionable preparation work;
they do not establish eligibility to win or perform an Illinois contract.

The [canonical capability ledger](../CAPABILITY_RECONCILIATION.md) owns current
status. This workstream supplies the Illinois applicability/evidence layer and
prioritized acceptance contracts. Shared limitations remain shared platform work;
Illinois requirements are not inferred from Texas or Oregon findings.

## 1. Scope and assumptions

CHO is the native core in the plan's Azure or GCP environment. The plan operates
its call center, claims and payment operations. The user confirmed Chicago-area
Medicaid business and the **2027 start year**, not an exact day, county list, legal
entity, procurement award, executed agreement or complete benefit allocation.
HealthChoice Illinois (HCI) is the provisional baseline. Cook and any collar
counties must be named individually. No CountyCare affiliation is inferred.

Source baseline: fetched `origin/main`,
`e7d633930222050184e98ddb851bf09fb3ef9495`; initially clean branch
`assessment/illinois-medicaid`. The prior clean Oregon branch at
`d314ac01b742f9fa16f8102a897310d95101b107` remains intact. CONTRIBUTING,
README/documentation index, SECURITY, capability ledger, production acceptance
workstream, active gap matrices, relevant ADRs 006/009/010/016/017/024/026/027/030,
and enrollment, PCP, encounter, PA evidence and finance runbooks were consulted.
No applicable root/ancestor AGENTS.md was found; the tracked worker-specific
AGENTS.md does not cover these changes. Source paths below are pinned to the
baseline except the two explicitly added encounter test cases.

### Related PRs and evidence boundaries

[#1542](https://github.com/aurelianware/cloudhealthoffice/pull/1542) merged reusable
PA evidence and annual drafts; [#1543](https://github.com/aurelianware/cloudhealthoffice/pull/1543)
merged the Texas documentation assessment. Both are in this baseline.
User-supplied [#1544](https://github.com/aurelianware/cloudhealthoffice/pull/1544)
was an open draft at the original assessment baseline and is now merged into main
as `ab204567`. This branch integrates that commit, preserving its Oregon encounter
and deterministic PCP concurrency characterizations without duplicating them.
It makes no runtime repair. Its findings and 487 historical test results are not
Illinois acceptance; the original Illinois validation totals below retain their
original baseline.

### Applicability matrix — IL-00

Applicability confidence is separate from implementation status. High confidence
below means a baseline category applies to an HFS-contracted HCI MCO; allocation
to a delegated administrator and exact contract terms remain unresolved. The
contracted MCO retains applicable accountability; CHO is software, not that entity.

| Scope / requirement | Confidence and current boundary | Accountable owner / evidence to close |
| --- | --- | --- |
| Legal customer and procurement | Unresolved MCO, delegate or service provider; no award asserted | Executive/legal: executed HFS agreement or delegation, procurement/award identity, retained duties and signatories |
| Launch and rating period | 2027 year confirmed; exact effective day/rating period unknown | Executive/compliance: implementation calendar and controlling 2027 contract/amendments |
| Counties and populations | Chicago area confirmed; Cook/collar counties and enrollment categories unknown | Enrollment/network: service-area schedule, member/program identifiers, population and transition inventory |
| Physical health, behavioral health, SUD, dental, pharmacy, transportation and other benefits | Each responsibility unresolved; none excluded merely because a subcontractor participates | Benefits/operations: per-benefit coverage, authorization, payment, notice, encounter and coordination owners, carve-outs and state-retained duties |
| YouthCare/DCFS, LTSS/HCBS/EVV, waiver programs | Conditional on assigned populations/benefits | Clinical/legal: applicable program agreement, waiver/State Plan authorities, agency/delegate responsibilities and transitions |
| Dual coordination, MLTSS, FIDE SNP | Conditional; distinguish Medicaid coordination from operating MA/Part D | Duals/program lead: launch-effective HFS/CMS agreements and population/benefit schedule |
| Enrollment and premium | High baseline category; approved current 834/820 and CEB roles unknown | Enrollment/finance: current source guides, sender/receiver inventory, daily/monthly control totals and premium reconciliation |
| Providers and network | High baseline category; verification mechanism/report layout unknown | Provider ops/compliance: authorized IMPACT-derived source, dates/locations/revalidation, network manual and accepted directory/access reporting |
| Encounters | High baseline duty; transaction/benefit/transport details conditional | EDI/state liaison: approved current guides, tradelinks, subcontractor/PBM paths, certification and state-accepted reconciliation |
| Notices, UM, grievances, appeals and hearings | High federal category; exact Illinois/2027 terms and approved content conditional | Compliance/member ops: effective requirement matrix, approved languages/accessibility/templates and delivery/action acceptance |
| Claims/payment/financial/quality reporting | High category; methods, interest/special payments and deliverable layouts conditional | CFO/claims/regulatory: executed rates/responsibility schedules, statute review, report definitions and accepted close |
| ADT/care coordination | High HCI MCO category; delegated connection/protocol unknown | Clinical/integration: HFS/PointClickCare onboarding, member panels, follow-up protocols/staffing and action evidence |
| CMS-0057-F | High MCO category; provision/rating-period/vendor allocation unresolved | Interoperability/compliance: separate complete inventory, conformance and data/consent acceptance; applicable APIs are 2027 launch dependencies |
| Hosting, workload and readiness | Azure/GCP and member count are assumptions, not measurements | CTO/SRE/operations: chosen topology, migration, SLO/RTO/RPO, source completeness, load/recovery and HFS/partner readiness acceptance |

IL-00 is **Unverified**, P0 governance/configuration/external evidence, owner
executive/legal/compliance. Acceptance is an approved versioned matrix linking each
requirement to legal owner, program/population/county/benefit, effective date,
source and delegated operator. All findings inherit its unknowns. A conditional
future requirement is not automatically a confirmed product gap or launch blocker.

### Authority register and access limits

Official sources accessed October 24, 2026. Public models are provisional evidence;
resolve applicable law, executed contract, incorporated policy and effective date
before configuring production. No Illinois fee-for-service reimbursement rule is
substituted for an MCO's actual provider payment terms.

| Ref | Official source / version / supported use |
| --- | --- |
| A1 | [HFS managed-care contracts index](https://hfs.illinois.gov/medicalproviders/cc/managedcarecontracts.html) and [August 2025 HCI model](https://hfs.illinois.gov/content/dam/soi/en/web/hfs/medicalproviders/cc/managedcarecontracts/hcicontractmodel.docx), downloaded and Word XML read. Distinct HCI/FIDE/DCFS agreement families. §§4.8–4.9 daily/monthly 834 and five-business-day audit update without awaiting 820; §4.18 readiness approval; §§5.7–5.8 network/PCP; §§5.13–5.19 care; §5.27 encounters; §5.29 clean-claim targets; §5.30 member rights; §7 premium; Attachment XIII network files. Model values are not final 2027 configuration. |
| A2 | [HFS MCO Oversight Commission report, December 31, 2025](https://www.ilga.gov/Documents/Reports/ReportsSubmitted/6374RSGAEmail14093RSGAAttachMCO%20Commission%20Report%20December%2031%202025.pdf) describes current HCI contracts expiring December 2026/replacement launch January 2027; [HFS September 9, 2024 notice](https://hfs.illinois.gov/medicalproviders/notices/notice.prn240909a.html) anticipated Plan Year 2027 procurement. Neither proves this prospective customer's award or exact launch. |
| A3 | [HFS January 1, 2026 map](https://hfs.illinois.gov/content/dam/soi/en/web/hfs/medicalproviders/cc/documents/managedcaremap.pdf) distinguishes statewide plans, Cook-only CountyCare and specialized YouthCare. Verify 2027 awards/counties rather than transplanting the map. |
| A4 | [HFS October 2, 2025 transition notice](https://hfs.illinois.gov/medicalproviders/notices/notice.prn251002a.html): MMAI ended December 31, 2025; FIDE SNPs began January 1, 2026. [FIDE scope](https://hfs.illinois.gov/medicalproviders/cc/mmai/fullyintegdesnpii.html) and [non-FIDE/MLTSS options](https://hfs.illinois.gov/medicalproviders/cc/mmai/howtoenrollinafidesnp.html) require population-specific launch confirmation. Older official pages describing ongoing MMAI are stale for this transition. |
| A5 | [HFS H.R.1 FAQ](https://hfs.illinois.gov/info/fedresctr/faqshr1impact.html) describes January 2027 ACA Adult six-month redetermination and other federal-change implementation; validate detailed dates/exemptions against state instructions. [CMS January 15, 2025 continuous-eligibility guidance](https://www.hhs.gov/guidance/document/section-5112-requirement-all-states-provide-continuous-eligibility-children-medicaid-and-0) addresses children under 19. These govern state eligibility, not locally inferred MCO termination. |
| A6 | [HFS encounter manual](https://hfs.illinois.gov/medicalproviders/cc/encountermanual.html), [May 2018 overview](https://hfs.illinois.gov/content/dam/soi/en/web/hfs/sitecollectiondocuments/chap1_encounteroverview.pdf), [adjudication process](https://hfs.illinois.gov/content/dam/soi/en/web/hfs/sitecollectiondocuments/chap2_encounteradjudicationprocess.pdf), and [submission/testing](https://hfs.illinois.gov/content/dam/soi/en/web/hfs/sitecollectiondocuments/chap3_encountersubmissionguidelines.pdf). Documented path is HFS/DoIT SFTP → HFS EDI → HFS MMIS, proprietary weekly remit/EDW; 837I/P/D and separate pharmacy pathways, layered 999 and state acceptance. Historical endpoints, vendor names, targets and layouts require current onboarding confirmation. |
| A7 | [HFS companion-guide index](https://hfs.illinois.gov/medicalproviders/handbooks/5010.html) and [May 2012 MCO Chapter 300](https://hfs.illinois.gov/content/dam/soi/en/web/hfs/sitecollectiondocuments/5010.pdf): daily/monthly enrollment and 820 premium roles. Age/FTP wording prevents treating it as accepted 2027 transport configuration. MEDI/REV/AVRS inquiries are not enrollment feeds. |
| A8 | [HFS provider participation](https://hfs.illinois.gov/medicalproviders/cc.html) distinguishes IMPACT enrollment from plan contracting. [October 5, 2026 revalidation notice](https://hfs.illinois.gov/medicalproviders/notices/notice.prn261005a.html) announces two-year cycles for specified high-risk/non-NPI/lab/ABA providers, exceptions, individual service-location revalidation and nonretroactive reactivation after disenrollment. No public universal IMPACT API verified. |
| A9 | [HFS 2025–2026 EQR report](https://hfs.illinois.gov/content/dam/soi/en/web/hfs/info/reports/annual/20252026eqrtechreport.pdf) references April 2022 v3.0 network instructions; obtain current manual/appendices. No Illinois 274 obligation established. A1 Attachment XIII provides provisional quarterly/as-requested full-network reporting. |
| A10 | [HFS ADT program](https://hfs.illinois.gov/healthchoiceadt.html) and [FAQ](https://hfs.illinois.gov/healthchoiceadt/healthchoiceadtfaq.html): PointClickCare, formerly Collective Medical, operates exchange; MCOs provide member panels and receive notifications. New plan/vendor authorization and onboarding remain required. |
| A11 | [42 CFR 438 Subpart F](https://www.ecfr.gov/current/title-42/chapter-IV/subchapter-C/part-438/subpart-F): notices, accessibility, appeals, hearing/continuation and effectuation. [§438.210](https://www.ecfr.gov/current/title-42/section-438.210): standard seven-calendar-day service authorization for rating periods beginning on/after January 1, 2026, expedited 72 hours, with lawful qualifications/extensions. Program/service applicability and any tighter terms need mapping. |
| A12 | [CMS-0057-F fact sheet](https://www.cms.gov/newsroom/fact-sheets/cms-interoperability-prior-authorization-final-rule-cms-0057-f), [final rule](https://www.cms.gov/files/document/cms-0057-f.pdf) and [PA process FAQ](https://www.cms.gov/initiatives/burden-reduction/overview/interoperability/frequently-asked-questions/improving-prior-authorization-processes). Medicaid managed-care APIs phase in with the first rating period beginning on/after January 1, 2027; operational/reporting duties generally begin 2026, first PA metrics posting March 31, 2026 for CY2025. New-plan historic source limits must be explicit. |
| A13 | [305 ILCS 5/5-30.1](https://www.ilga.gov/ftp/ILCS/Ch%200305/Act%200005/030500050K5-30.1.html), [89 IAC Part 140](https://my.ilga.gov/agencies/JCAR/EntirePart?titlepart=08900140), [§140.76](https://www.ilga.gov/ftp/JCAR/AdminCode/089/089001400B00760R.html) identified, but full-page retrieval failed with 502. Current enacted text/effective provisions, payment interest and hospital UM applicability require legal verification; search snippets are not sufficient implementation authority. |

Current proprietary/assigned encounter, 834/820, provider-network and ADT packages,
state/partner credentials and 2027 executed materials were unavailable. The HFS
award press-release page returned no usable content; no award is inferred from
secondary announcements. State Plan/waiver provisions and other-agency
responsibilities require assigned-program review. DOI provisions are not imported
without verifying jurisdiction and applicability.

## 2. Current capability assessment

Each chain below was inspected from host registration into executing behavior,
persistence and relevant consumers/tests. A named class, TODO or historical test
count alone was not treated as implementation evidence.

| Capability / shared reference | Executing evidence and current boundary |
| --- | --- |
| Enrollment and identity | `src/services/enrollment-import-service/Program.cs:35–110` registers real parser, validators, Mongo run/transaction/event stores, archive and background worker. Raw-EDI controllers drive `EnrollmentImportService.cs:446+` member/coverage add/change/reinstate/terminate; authenticated `HttpCoverageServiceClient` preserves failures. `EnrollmentEventPublisher` persists deterministic replay IDs/version retries. Member typed Medicaid IDs and `Enrollment834EdiParser.cs:370` REF*23 exist; dependent identity rejects missing stable evidence. Illinois crosswalk/feed certification remains unverified. |
| Generic encounters / CAP-34 | `encounter-service/Program.cs` binds Mongo lifecycle/revision tracking and `Encounter837Service`; controller supports create, correction, batch and 837P/I/D export. Caller-marked submit is not transmission proof. The separate automatic submission host is FL-only; A/E 999 states are syntax-only, correctly distinct from final acceptance. |
| Providers, contracts, capitation | Native directory/credentialing/contracts and provider capitation runs/statements/approval/retro/disbursement exist. `capitation-service/Program.cs:70–140` binds Mongo, service clients, bank source and NACHA. Provider-enrollment hosts select Texas/CAQH; no registered Illinois source. `ProviderContractsController.SyncChildren` returns 501. |
| PCP / CAP-35 | `coverage-service/Program.cs:50–92` binds directory/panel clients and Mongo assignment service. `PcpAssignmentService` checks active dated medical coverage/provider/network/panel/LOB/age and persists history/coverage; unavailable count fails closed. Capacity check and insert are separate; reconciliation job has no host scheduler. |
| Care administration | `care-management-service/Program.cs` requires Mongo, registers tenant/actor auth and optional work gateway. `CareCasesController` persists referrals, assessments, consent, plans, activities and revision-fenced history. Null work gateway can retain activity without task; no ADT/member-panel route found in inspected host/source search. |
| Tenancy/service identity / CAP-24/29 | Shared `TenantMiddleware.cs:55–95` derives tenant from validated token and rejects header conflicts. Native claims HTTP lookups use `UseChoServiceIdentity`; claims outbound identity behavioral tests passed. Target-cloud trust/encryption/confinement acceptance remains unverified. |
| Native FHIR / CAP-22/27/30 | `fhir-service/Program.cs:134–170` binds native router/clients; `FhirSyntheticSourceRegistration.cs:12–29` guards selected mocks. All-native composition need not enable synthetic permission. Source completeness and deployed acceptance remain separate. |
| PAS and DTR / CAP-01/02/04/23/25/26 | `PasController.cs:214–260` records through registered `HttpPasAuthorizationRecorder` before returning usable success. Authorization Mongo repository persists/revision-fences records. FHIR Program registers `MongoDtrResourceStore`, whose questionnaire/response CRUD/search is durable. Real-Mongo focused PAS/DTR tests executed; package validation, unit-consumption and wider operations remain bounded. |
| Coverage/temporal eligibility | `CoverageLookup.cs:25–48` separates real no-coverage from authentication/transport/unreadable failure. `TemporalAccumulatorRegistration` defaults production to explicit Unavailable; guarded synthetic stub is not automatic fallback. Historical source remains missing; ordinary native accumulation is distinct. |
| COB and grouping/pricing | Claims host registers later-payer `CoordinationOfBenefitsStage`, MS-DRG grouper and grouping stage before pricing. `PricingStage` consumes grouping output; feature flag defaults off and unsupported/configuration failures pend. PoA/facility reference-data and licensed APR-DRG/EAPG boundaries remain. Selected COB and end-to-end grouped pricing tests passed; Illinois contractual dollars/methodology are unverified. |
| Stedi | `StediHealthcareGatewayServiceCollectionExtensions` binds real named configured eligibility/claim/attachment/status/ack/remittance clients with authentication/retries. Live partner subscription, HFS agreement and certified route are unverified; no live Stedi call or Stedi suite was executed here. |
| Notices and appeals | Correspondence host binds Mongo templates/composer/document storage and returns Rendered; no delivery worker registered. Appeals host binds Mongo/events/outbox and an embedded Illinois rule pack, evaluated by `AppealResponseDeadlinePolicy`; 36 rule-pack tests passed. `il.json` still flags 2026 contract confirmation and missing appeal acknowledgment value. Claim-linked appeal persistence is not proof of complete PA/grievance/hearing/continued-benefits fulfillment. |
| Payments and finance / ADR 026 | Payment host binds bank reconciliation, durable GL outbox and dispatcher. `NachaSettlementMatcher.cs:120–165` records eligible whole transmitted credits-only claim-file bank settlement and event together. AR `GlEntryBuilder.Settlement:179–203` and posting service validate identities, deduplicate and balance ACH-in-transit/cash. Returns/capitation/premium/open-item close remain bounded. |
| PA evidence/reporting / CAP-39 | Authorization Mongo captures receipt/decision and revision-fenced sourced appeal/extension facts; `PaReportingController` produces annual drafts. `AnnualPaReporting.cs:100–123` explicitly sets `PublicationReady=false`. Source completeness, approved cohorts/freeze/publication remain. `BulkExportService` throws 501 and produces no durable job/files (CAP-14). |

## 3. Confirmed gaps and impacts

The IDs below are stable Illinois applicability references, not separate copies of
the shared product ledger. All remediation statuses are **proposed** unless noted
in §6. P0 means required before the affected launch workflow can be accepted,
not proof an unknown contract assigns that workflow. Common scope is assigned
HCI members/benefits in actual Chicago-area counties under IL-00; legal owner is
the prospective contracted MCO, with delegated operational owner to be confirmed.

### IL-ENC — Automatic Illinois encounter route

**Confirmed gap**, high baseline encounter-duty confidence, P0 for assigned
reporting. Shared CAP-34 complete-batch/syntax-ack fixes are already implemented;
this is the remaining routing/profile and durable-staging work, also relevant to
other states. `encounter-submission-service/Program.cs:80–90` registers the consumer
and worker. `KafkaConsumers/AdjudicationCompletedConsumer.cs:191–199` skips Illinois;
`Services/EncounterSubmissionService.cs:148–149` rejects non-FL and `201–202` emits
FMMIS-oriented JSON. The generic 837 generator is not called by this worker.
`Workers/EncounterSubmissionWorker.cs:123–126` marks batched before object staging,
without atomic reservation/staging. New Illinois characterization cases reproduce
the event omission and refusal before HTTP/database work; existing batch tests
verify JSON contents. Impact: source services cannot be reconciled through this
automatic route to HFS-accepted records, risking reporting/payment oversight gaps.

Owner: EDI architect, state liaison and encounter operations. First define reusable
durable reservation/outbox/object staging, claim/version lineage and recovery;
then add the Illinois/accepted-partner profile using current approved guides
(A1/A6/A7). Preserve separate transport, syntax and state-record acceptance;
correlate IDs, rejection queues, corrections/replacements/voids and delegated/
capitated completeness. Acceptance: crash/retry at each durable boundary, no lost
or duplicate claims, out-of-order responses, recovered rejections and source-to-
state accepted count/financial reconciliation, with HFS/partner certification.
A separately contracted EDI partner can mitigate only after accepted handoff,
trading-partner agreements, reconciliation and capacity evidence; raw JSON staging
is insufficient. Interim controls expire on accepted automation or the agreed
volume/date limit, whichever comes first. Effort is not estimated before current
layouts, pathways and acceptance are available.

### IL-PROVIDER — State-enrollment verification source

**Confirmed gap** in the inspected Illinois source registration; native provider
administration is **Partial** for Illinois and exact network reporting is
**Unverified**. High baseline confidence under A1/A8; P0 for required participating
and applicable ordering/referring/prescribing providers. Benefit-plan and FHIR
Program register Texas/CAQH. `ProviderEnrollmentServiceRegistration` has no IL/
IMPACT source, including `WithAllStateSources`; `MultiStateEnrollmentAggregator`
returns null without a source. `StateEnrollmentGate` passes when unconfigured/
disabled and evaluates when enabled; directory/credentialing is not state proof.
Provider gate/aggregator tests demonstrate configured semantics; no live Illinois
verification is claimed. Impact: missing dated state enrollment evidence and
location/revalidation changes can affect provider eligibility and encounters.

Owner: provider operations, integration engineer and compliance. Obtain an
authorized IMPACT-derived extract/partner verification contract; implement dated
NPI/taxonomy/location/affiliation intervals, provenance/freshness and explicit
unknown/unavailable states, then register accepted host/policy configuration.
Test historical service dates, disenrollment/nonretroactive reactivation,
location-specific revalidation (A8), stale feeds and outages. Separately map current
network/directory/access report fields and delegated networks (A1/A9). No universal
IMPACT API or required Illinois 274 is assumed. Controlled verified extracts/manual
checks can bridge only approved frequency/volume with dated audit/exception review;
stale data cannot assert active enrollment. Sunset at source automation acceptance
or the approved freshness/volume limit. External source rights, reporting manual
and plan/HFS acceptance are required; estimate awaits that contract.

### IL-PCP — Atomic capacity and reconciliation

**Partial** native assignment; **Confirmed gap** in configured capacity atomicity
and absent scheduled reconciliation. High confidence for assigned PCP model,
exact Illinois rules conditional (A1). P0 before unattended capacity-sensitive
assignment. `PcpAssignmentService.cs:190–210` reads count before later ending old
assignments/inserting history; no shared panel reservation closes the race.
Program registers scoped `PcpPanelReconciliationJob`, no scheduler. The merged
#1544 deterministic last-slot test is shared supporting evidence and is retained
without duplication. Current 27 focused tests credit validation and unavailable
capacity failure closure (CAP-35), not concurrency correctness. Impact: panel
overfill and diverging provider/member histories.

Owner: provider/member engineering. ADR must establish dated slot authority,
provider/member key, multi-coverage dedup and atomic reserve/release with history/
coverage convergence. Add scheduler, checkpoint/retry and backlog metrics.
Acceptance: two concurrent last-slot requests produce exactly one reservation;
restart/rollback, same-provider idempotency, retro/future reassignment and outage
cause no incorrect writes; reconcile to accepted panel source. Serialized monitored
operations are only bounded interim controls, expiring before unattended use or
capacity threshold. Cross-service persistence boundaries make an isolated check
change insufficient; effort awaits ADR and approved assignment policy.

### IL-CONTRACT — Capitation child synchronization

**Partial** native contracts/capitation, confirmed 501 child-sync boundary; P0 only
if the engagement assigns dependent provider capitation/delegated contracts.
`provider-contracts-service/Controllers/ProviderContractsController.SyncChildren:319–342`
loads the contract and returns 501 without propagating capitation child identity/
LOB. Native `CapitationRunService` already prices initial/retro statements and
holds unavailable coverage; HFS premium is separate. Impact: amended/terminated
contracts can leave children inconsistent. Source establishes the 501; no full
contract amendment replay was executed here.

Owner: contract engineering and provider finance. Inspect actual child APIs,
approve version/effective-date authority, then implement authenticated idempotent
propagation, per-child results, retries and reconciliation. Acceptance: amendments,
termination, duplicate/restart/partial failure and retro financial deltas reconcile
separately for provider PMPM and HFS premium. Interim dual-controlled exception
reports expire before unattended use or agreed cutoff. Requires actual contract,
child interface and finance approval; estimate after scoped design. ADR 016
supersedes legacy FFS children with native reimbursement terms; missing legacy
ffs-service packaging is not missing native fee-for-service pricing.

### IL-NOTICE — Fulfillment and complete member-rights lifecycle

**Partial** correspondence/appeals; confirmed rendered-only delivery boundary,
wider native PA/grievance/hearing/continued-benefits completion **Unverified**.
High federal baseline (A11), provisional Illinois terms (A1), P0 where assigned.
Correspondence Program has no delivery worker; controller returns Rendered.
Appeals Mongo/outbox and Illinois pack are real. Model §5.30 has 15-business-day
standard appeals and expedited 24-hour information/decision steps; the embedded
pack is not approval of 2027 terms. Its acknowledgment `toConfirm` remains.
`AppealsController:754–805` accepts an operator's already-sent extension notice
timestamp; it does not prove delivery. Impact: unresolved timeliness/content/action
proof despite composed letters and durable claims appeals.

Owner: member ops/UM/appeals/compliance and correspondence engineering. Version
approved effective-dated notices; persist decision-to-letter intent with stable key,
recipient/language/accessibility, vendor send/retry/dead-letter/returned-mail and
late alerts. Link PA/member grievance/appeal/hearing/continuation/reversal actions
to the authoritative decision. Acceptance: every assigned adverse/expedited/
extension/reversal scenario has correct deadline, approved template, one send,
restart recovery, delivery/failure evidence and source-to-notice reconciliation.
Provider payment disputes remain separate. Requires approved content/contract and
delivery/vendor/operational acceptance. A plan-operated fulfillment queue can bridge
with capacity, daily deadline reconciliation and immutable proof; expires at
accepted automated fulfillment or agreed volume/date limit and is not equivalent
to required automation. Estimate awaits channels/content/vendors.

### IL-FINANCE — Return, open-item and integrated close boundaries

**Partial**, P0 for assigned native financial lifecycle; high financial-correctness
confidence, exact Illinois payment arrangements conditional (A1/A13).
Already implemented: whole credits-only bank-confirmed claims settlement with
durable event and balanced GL posting. `NachaSettlementMatcher.cs:80–87` flags
returns rather than completing settled return accounting; unsupported cases retain
explicit `GlAccountingLimitation`. `ArBalancesController.GetAgingSummary` sums
stored buckets after loading balances; it does not derive invoice due-date aging.
Premium invoice aging is separate. Ten payment and ten GL settlement tests passed;
no full close/return/capitation/bank/ERP acceptance was executed. Impact: incomplete
return/reissue/capitation/premium reconciliation and open-item aging evidence.

Owner: CFO, treasury/payment operations and finance engineering. Define authoritative
invoice/open-item/cash/return events and approved accounting first. Split durable
return/reversal/reissue linkage, provider capitation versus HFS premium producers,
due-date aging/database aggregation and integrated ERP close. Acceptance: balanced
expected-dollar corpus, no duplicate cash/expense, original transmission/settlement
lineage, period/cutoff/retro boundaries, restart/out-of-order recovery and bank/ERP
control totals. Interim dual-controlled accounting can bridge accepted events with
daily reconciliation and explicit cutoff, expiring at integrated acceptance or
agreed volume/date. Requires bank/ERP interfaces, rates, special-payment/interest
review and finance acceptance; no reliable overall effort before those contracts.

## 4. Unverified requirements and evidence dependencies

### IL-ENROLL — Illinois identity, roster and premium reconciliation

**Partial** generic lifecycle; Illinois production feed/policy/reconciliation
**Unverified**, high HCI baseline confidence (A1/A5/A7), P0. Owner enrollment and
finance/integration. Generic member IDs, dependent identity, retro dates, event
idempotence and coverage operations are credited (§2; 263 tests). No accepted
Illinois recipient crosswalk, county/program/benefit assignment, CEB roster or
834/820 reconciliation corpus was supplied. Acceptance: approved sender/receiver
and identifiers, duplicate/retro/terminate/reinstate/transition corpus, daily/monthly
control totals, source-vs-plan eligibility distinction, claims/PA/PCP/notices/
capitation convergence and premium adjustments. Never wait for 820 to apply roster
updates where A1's term applies; never locally terminate from an inquiry outage or
redetermination schedule. Controlled daily reconciliation needs approved frequency,
volume, exception ownership and sunset at accepted automation/date cap. Feed rights,
current guides and state/plan operational acceptance remain; no scope-supported
estimate yet. Impact: coverage/assignment/payment discontinuity.

### IL-UM — Authorization and continuity policy acceptance

**Unverified** Illinois-specific policy acceptance, native receipt-based deadline
arithmetic **Implemented**; high A11 baseline confidence, exact service/program
and 2027 terms conditional (A1/A13), P0 for assigned UM. Owner UM/compliance and
authorization engineering. `authorization-service/Services/AuthorizationDeadlinePolicy.cs`
selects 72/168 hours from receipt and does not grant pause/extension. This matches
generic expedited/standard arithmetic, not a complete Illinois drug, urgency,
continuity or special-population selector. No shorter Illinois clock is asserted
from unavailable legal text. Accept an approved service/program/date matrix,
urgency/extension qualifications, benefit continuation/transition and expedited
escalation scenarios linked through decision, delivered notice and appeal. Add
policy selection only for established requirements, with source/version and
behavioral regressions; do not import Texas exemptions/gold-card seeds. Interim
clinical review needs permitted scope, monitored deadlines and documented sunset
at automated acceptance or before unstaffed use. Current state law/full contract,
clinical content and operational sign-off remain gates; no effort estimate before
those inputs. Impact: missed effective decision or continuity duties despite a
generic clock. No new UM-specific deadline test selection ran in this assessment.

### IL-PRICING — Benefit and reimbursement correctness

**Unverified** Illinois acceptance, scoped native COB/grouped pricing **Implemented**;
P0 for claims the platform will pay. Owner benefits/provider contracts/claims/CFO.
A1 model clean-claim targets are provisionally 90% within 30 days and 99% within
90 days; final rules, interest and effective contract values require legal review
(A13). Obtain actual hospital, FQHC/RHC, directed/supplemental, COB/TPL, recovery and
recoupment responsibilities and rates. Coordination does not imply payment
responsibility. Acceptance: date-effective golden claim-line/member/provider-dollar
corpus including inpatient methodology, duals, adjustments/void/recovery and special
payments; no Illinois FFS/MS-DRG/APR-DRG substitution by assumption. Configured flags,
licensed assets and PoA/reference data require acceptance. Interim adjudication
review must be approved and bounded; it expires before autonomous use. No repair
or estimate without established contract/corpus. Impact: wrong benefit or payment.

### IL-ADT — Event-to-care and specialized responsibility

**Partial** native care cases/plans; Illinois panel/event integration **Unverified**,
no executing ADT path found in inspected source. High HCI confidence (A1/A10), P0
for assigned coordination; vendor/operational owner unresolved. Owner clinical/care
ops plus integration. Obtain HFS/PointClickCare member-panel, transport, event and
onboarding package. Implement durable envelope, identity quarantine, dedup/late-
event handling and approved protocol-to-task/follow-up/escalation evidence. Accept
panel changes, unmatched/duplicate/late events, outage/queue failure and documented
clinical action/completion under agreed staffing/deadlines. Work gateway Null can
retain an activity without queued task; event receipt alone is insufficient.
Accepted vendor transport can mitigate connectivity with monitored handoffs, but
not clinical action/staffing. Sunset any interim handoff at accepted automation or
agreed queue/volume threshold. Specialized BH/crisis, DCFS/YouthCare, LTSS/waivers/
EVV and agency-sharing are conditional IL-00 dependencies, with accepted delegated
source totals and follow-up if assigned. Effort awaits specifications/protocols.

### IL-REPORT — HFS deliverable definitions and source reconciliation

**Unverified** Illinois package, high reporting-category confidence (A1/A6/A9), P0
for due launch/operating deliverables. Owner regulatory reporting/actuarial/finance.
No accepted current financial, quality, utilization, encounter-monitoring or network
package supplied. Existing PA drafts/generic reports are not HFS acceptance.
Obtain versioned schedule/layout/methodology and map claims, accepted encounters,
premium, provider/delegated and finance totals. Acceptance: reproducible period/
cutoff/revision calculations, complete source reconciliation, exceptions, audit and
signed accepted submissions. Interim controlled preparation is only feasible if
permitted, capacity-tested and independently reconciled; sunset at automated
acceptance or agreed submission limit. External specifications and compliance/
HFS acceptance remain; no estimate without definitions. Impact: omitted or
inconsistent regulatory/financial reporting.

### IL-SECURITY and IL-SCALE — Deployment, recovery and operational acceptance

Both **Unverified** deployment/operations gates, P0, owners security/CTO/SRE and
plan operations. IL-SECURITY credits scoped token tenancy/service identity/native
sources/PAS/DTR/outage distinctions (§2); requires selected Azure/GCP bindings,
synthetic exclusion, least-privilege identities/key rotation, member/tenant negative
access, encryption/audit/retention and tested backup/restore with native dependencies.
No new vulnerability is asserted. IL-SCALE requires agreed enrollment peaks,
claims/encounter/delegated volumes, PA traffic, call seats/concurrency, batch windows,
history/retention and SLO/RTO/RPO; 250,000 members do not define those quantities.
Accept measured sustained/burst throughput/latency, replay/idempotence, backlog drain,
broker/database/dependency failure, restore, migration balances, call-center and
incident/on-call exercises. No load/DR/cloud campaign ran. No manual mitigation
substitutes for required security, recovery or state readiness; acceptance gates
remain until measured evidence and plan/state sign-off. Effort follows workload,
topology and migration scope. Impact: confinement/availability or unreconciled loss.

### IL-CMS — Shared prerequisites and 2027 launch dependency

**Partial** shared implementation (CAP-06/14/39), high MCO applicability, P0 where
the applicable rating period starts on/after January 1, 2027 (A12). Keep a separate
comprehensive CMS-0057-F workstream, but do not defer applicable APIs until after
this prospective 2027 launch. MCO, delegate and interoperability vendor allocation
must be explicit. Owner interoperability/compliance with clinical/reporting leads.
Native PA evidence/annual draft is implemented, not an approved public artifact;
real bulk jobs/files remain absent. Acceptance must cover authoritative all-channel
facts/legacy limits, plan-versus-tenant mapping, cohorts, immutable reconciled freeze,
approved metrics/publication procedure; Patient Access usage reporting, Provider
Access attribution/opt-out, P2P consent/lookback/segmentation, complete clinical/PA
document data, PA discovery/documentation/response and scoped durable bulk/NDJSON/
authorized download/restart/revocation. Validate profiles/terminology and deployed
end-to-end data completeness. Endpoint presence or 501 boundaries are insufficient.
External vendor procurement can mitigate implementation only after accepted data,
rights, conformance and operation; no endpoint-only/manual compliance claim. Sunset
interim exchange at required effective date or accepted replacement, whichever is
earlier. Comprehensive scope determines effort. Impact: missing required 2027
exchange and incomplete reporting despite scoped native data foundations.

## 5. Mitigation and implementation plan

Procure current technical packages/test access and settle role/service area/benefits
now; these are dependencies for concrete adapters, not reasons to postpone reusable
correctness work. Every implementation PR must name its finding, baseline behavior,
production binding, durable boundary, regression/replay, migration/rollback and
external gates. Larger changes require scoped ADR/issue under CONTRIBUTING.

| Sequence / proposed PR | Scope and dependencies | Objective acceptance / external gate |
| --- | --- | --- |
| 1. IL-00/ENROLL/SECURITY scope and configuration contract | Approve 2027 role/counties/benefits, authority/version matrix, source/receiver inventory, native identity and member/plan mappings | Approved matrix; synthetic replay/control totals and downstream convergence; selected-cloud trust/native-only deployment acceptance |
| 2. IL-PROVIDER + IL-PRICING evidence and dated verification | Authorized source contract and actual reimbursement/benefit corpus after scope; separate source adapter from network report mapping | Dated location/revalidation/outage scenarios; expected-dollar corpus and required enrollment/network attributes; source rights and legal/plan approval |
| 3a. IL-PCP shared capacity ADR/reservation | Authoritative dated slot/member persistence contract; reuse merged #1544 concurrency characterization | One winner for last slot, rollback/restart/idempotence/retro convergence; scheduled source reconciliation and panel operational acceptance |
| 3b. IL-CONTRACT capitation propagation | Inspect child API; approve effective contract/version and finance rules | Per-child idempotent results/retry; amend/terminate/retro replay and PMPM totals; provider-finance approval |
| 4a. IL-ENC shared durable staging | Reusable reservation/outbox/object/lineage design before state routing | Crash/duplicate recovery across reservation/staging/send with exact source membership; no state acceptance claim yet |
| 4b. IL-ENC Illinois profile/partner + acceptance reconciliation | Approved current HFS guides/benefit paths/tradelinks and 4a; use generic 837 where accepted | Layered transport/999/state result lineage, corrections/void/rejection recovery and accepted totals incl assigned delegates; state/partner certification |
| 5a. IL-UM/NOTICE approved policy, fulfillment and rights | Authoritative decision sources, approved 2027 rules/content and delivery vendor | Deadline/language/accessibility, one send/retry/returned-mail evidence, PA/grievance/hearing/continuation replay; compliance/ops acceptance |
| 5b. IL-ADT event-to-action | Authorized PCC panel/event package and clinical protocol/staffing; enrollment identity dependency | Panels, dedup/late/quarantine/outage and owned care completion/escalation audit; PCC/clinical acceptance |
| 5c. IL-FINANCE event/open-item/close PR series | Approved source/accounting contract first, then return/reissue, capitation/premium, aging and ERP close separately | Balanced expected dollars, no duplicate cash, period/replay/lineage/bank-ERP reconciliation; finance/bank acceptance |
| 5d. IL-REPORT/CMS source/report/exchange PR series | Current HFS definitions and CMS provision/rating-period inventory; source-complete approved contracts | Reconciled reports/freeze and scoped durable exchange/conformance. Bring this sequence forward to meet applicable 2027 deadlines; vendor/state/compliance approval |
| 6. IL-SCALE + assigned specialized/delegated acceptance | Actual volume/topology/migration/benefit scope, completed prerequisites and test access | Measured SLO/RTO/RPO/replay/restore, delegate/bank/state certification, HFS readiness and plan operations sign-off before affected launch |

No engineering-week estimate is supported for the full program: key interfaces,
contract values, source-of-truth ADRs, approved content and workload are unavailable.
Each PR has a reviewable scope and acceptance contract above; estimate after those
inputs and a focused spike. Temporary mitigations need named owner, permitted scope,
capacity, daily reconciliation/escalation and a written expiration date/threshold;
none is currently operationally accepted or equivalent to mandated automation.

## 6. Remediations implemented and verification results

**No runtime remediation was implemented and no gap was closed.** Safe local work
added two Illinois encounter characterizations: `BatchIntegrityTests` adds the IL
refusal row, and `Security/AdjudicationConsumerTenantTests` adds an IL event exclusion
case. They establish the baseline limitation, not desired launch behavior. Other
remaining work requires approved external specifications or a broader durable-state
design; interfaces/content/credentials were not fabricated to simulate completion.
All designs above remain proposed; only characterization evidence is locally
verified. Nothing is externally verified or operationally accepted by this review.

SDK **8.0.426**, net8.0, standard VSTest. Commands ran from the repository with
`dotnet test <project> -m:1`; filters below use
`--filter 'FullyQualifiedName~ClassA|FullyQualifiedName~ClassB'`.
Core runs used `--logger 'console;verbosity=minimal'`; platform runs `--nologo`.
`--no-restore` applied except care/appeals after asset restore; the two filtered
FHIR platform runs used `--no-build --no-restore` after current-source build.

| Project | Full suite or exact filter classes | Passed |
| --- | --- | --- |
| `tests/CloudHealthOffice.EncounterSubmissionService.Tests/CloudHealthOffice.EncounterSubmissionService.Tests.csproj` | Full suite, including two new IL cases | 33 |
| `src/services/enrollment-import-service/EnrollmentImportService.Tests/EnrollmentImportService.Tests.csproj` | Full suite, including Mongo/raw-834/streaming/orphan/identity cases | 263 |
| `src/services/coverage-service/CoverageService.Tests/CoverageService.Tests.csproj` | PcpAssignmentServiceTests, HttpPanelCounterTests | 27 |
| `tests/CloudHealthOffice.ProviderEnrollmentService.Tests/CloudHealthOffice.ProviderEnrollmentService.Tests.csproj` | StateEnrollmentGateTests, MultiStateEnrollmentAggregatorTests | 27 |
| `src/services/care-management-service/CareManagementService.Tests/CareManagementService.Tests.csproj` | Full suite | 116 |
| `tests/CloudHealthOffice.EligibilityService.Tests/CloudHealthOffice.EligibilityService.Tests.csproj` | CoverageLookupFailureTests, TemporalAccumulatorRegistrationTests | 25 |
| `tests/CloudHealthOffice.FhirService.Tests/CloudHealthOffice.FhirService.Tests.csproj` | FhirSyntheticSourceRegistrationTests | 9 |
| `tests/CloudHealthOffice.PaymentService.Tests/CloudHealthOffice.PaymentService.Tests.csproj` | NachaSettlementReconciliationTests | 10 |
| `src/services/ar-service/ArService.Tests/ArService.Tests.csproj` | GlBankSettlementTests | 10 |
| `src/services/authorization-service/AuthorizationService.Tests/AuthorizationService.Tests.csproj` | AnnualPaReportingTests, PaReportingEvidenceTests | 24 |
| `tests/CloudHealthOffice.ClaimsService.Tests/CloudHealthOffice.ClaimsService.Tests.csproj` | CoordinationOfBenefitsStageTests, AdjudicationGroupsInpatientDrgEndToEndTests, AdjudicationLookupsCarryServiceIdentityTests | 79 |
| `tests/CloudHealthOffice.FhirService.Tests/CloudHealthOffice.FhirService.Tests.csproj` | MongoDtrResourceStoreTests, DtrServiceMongoTests, PasSubmitPersistenceSeamTests | 32 |
| `src/services/appeals-service/AppealsService.Tests/AppealsService.Tests.csproj` | RulePackTests (includes Illinois) | 36 |

**691 passed, zero failed, zero skipped** across these distinct successful selections;
counts describe coverage, not readiness. MongoDtrResourceStoreTests and
PasSubmitPersistenceSeamTests used real EphemeralMongo V7 through MongoRunnerFixture,
without catch/early-return skips. DtrServiceMongoTests uses an in-memory store
despite its name: only its service behavior is credited. Enrollment's full suite
includes real-Mongo fixtures; no managed-cloud restore acceptance is inferred.

Initial sandbox VSTest attempts aborted before execution when local control-socket
binding was denied; authorized reruns passed. Initial care/appeals no-restore runs
failed missing assets (NETSDK1004); ordinary restore/rerun passed. Aborted/build-
failed attempts are excluded from totals. No full solution build/regression,
complete provider/capitation/coverage suite, live Stedi/HFS/PCC/bank/delegate test,
Illinois reimbursement corpus, financial close, complete conformance, cloud load/
restore/migration or operational/state readiness campaign ran. No credentials,
real member/claim data, state submissions, regulatory publication, merge or deployment
were used/performed. Changed local Markdown destinations and `git diff --check`
were validated before delivery.

### Main integration validation — October 24, 2026

Integrated main `ab204567` (merged #1544), preserving both state assessments,
both IL/OR encounter cases and the shared PCP race characterization. Rebuilt
current sources on SDK 8.0.426: the full encounter suite passed **35 tests** and
the selected `ConcurrentMembers_CurrentlyBothTakeTheLastPanelSlot` test passed
**1 test**, zero failures/skips. These are separate integration checks, not a
rerun or expansion of the original 691-test assessment. Initial sandbox runs
aborted on the VSTest control socket; authorized `--no-build --no-restore`
reruns completed. All 51 relative documentation targets exist and diff checks
pass. No runtime behavior changed or open finding was closed.

### Older findings explicitly reconciled

| Historical claim | Current scoped disposition |
| --- | --- |
| Header-derived tenant / missing service identity | Scoped controls Implemented, claims outbound identity tested; IL-SECURITY deployment acceptance Unverified. |
| Native FHIR needs synthetic permission | Selected-mock guard/native composition Implemented and tested; actual source completeness/deployment remains Unverified. |
| PAS and DTR are memory-only | Scoped persistent recorder/Mongo CRUD/search Implemented, selected real-Mongo tests executed; wider package/unit/exchange/restore boundaries remain. |
| Coverage outages mean ineligibility / fabricated temporal balances | Failure distinction and explicit production-unavailable behavior Implemented/tested; historical accumulator source remains missing where required. |
| Secondary/tertiary COB absent; DRG detached from pricing | Scoped native COB and end-to-end grouped pricing Implemented/tested; Illinois contractual methodology/dollars Unverified. |
| Claims ACH cannot reach GL | Bounded bank-confirmed credits-only settlement path Implemented/tested; IL-FINANCE remains Partial. |
| Stedi gateway is stubbed | Real configured connector registration/clients inspected; live subscription/Illinois route/certification Unverified and no Stedi suite run here. |
| Legacy ffs-service absence blocks native pricing | Not applicable under ADR 016 native contract terms; separate capitation sync and reimbursement acceptance still apply. |
| QNXT/Facets/HealthEdge stubs block native core | Not applicable under ADR 027 unless engagement architecture changes. |
| Existing Illinois enrollment/encounter/IMPACT/PA/ADT/reporting names prove state readiness | Generic flows and Illinois appeals pack credited precisely; encounter/provider source gaps confirmed; Illinois feeds, ADT, report/UM policy and acceptance remain as scoped above. Other-state implementations do not satisfy them. |

## 7. Remaining business, certification and launch gates

The next business milestone is an approved 2027 Chicago-area scope/responsibility
and source inventory, followed by reusable correctness PRs and accepted state/
partner profiles. Secure current guides and onboarding access while designing the
shared durable boundaries. The plan must retain signed acceptance of migration,
expected-dollar adjudication/close, source-to-state encounters, provider/enrollment
reconciliation, delivered notices/member rights and event-to-care action.

No MCO award, credentialed connection, approved notice, licensed reimbursement
method, complete reporting/conformance or HFS readiness approval follows from
source inspection. Required HFS readiness review (A1 §4.18), trading-partner/
subcontractor acceptance, cloud security/scale/restore, clinical/operational staffing
and compliance/finance sign-off remain open. For this 2027 target, applicable
CMS API work must be planned as a launch dependency alongside native-core acceptance.
