# California launch acceptance after PR #1547

Reviewed October 25, 2026 (America/Phoenix), main
`1b9319c17bf1ab376a0155d0c7dbf6100090ee77`. Target remains native CHO in
plan-operated Azure/GCP for 250,000 California Medicaid members. The plan retains
calls, claims and payment operations; EDI can be contracted. No particular EDI
partner, legal entity, award, counties, benefit allocation or launch date is assumed.

**Launch acceptance is unverified.** This is the current evidence/acceptance
refresh, not a launch approval or a complete legal/contract crosswalk. The
[capability ledger](../CAPABILITY_RECONCILIATION.md) owns implementation status.
[Scenario definitions](california-acceptance-cases.json) contain proposed plan
acceptance cases with no populated results. Those are not executed tests or a
regulatory certification service.

Current implementation follow-up: [coverage-writer controls](../operations/PCP-COVERAGE-WRITER-CONTROLS.md)
and proposed [ADR 032](../adr/032-pcp-coverage-writer-boundary.md) extend the production
fence to lifecycle writes and stored future coverage reservations. New executable
validation is separate from the historical records below; PCP scheduling remains
refused. A proposed PCP-card replacement scenario is now included with empty results.

## What the merged fixes actually close

- #1541 repaired scoped reporting, encounter batching/999 semantics, missing
  panel-count behavior and terminology projection defects. It did not produce a
  California encounter route or a complete validated benefit/policy catalog.
- #1542 captures native PA receipt/decision evidence and sourced appeal/extension
  facts, with annual drafts. `AnnualPaReporting` still returns
  `PublicationReady = false`. Its attested facts do not adjudicate PA appeals,
  grant lawful extensions, fulfill notices or supply every authoritative source.
- #1546 blocks the Texas statutory gold-card rule on Medicaid and disables its
  three seeds. That is a shared safety correction, not California policy content.
- #1547 transacts the **current-day assignment API** through Mongo member/panel
  fences, local roster counting, history and coverage writes. Its focused source
  CI passed 27 VSTest cases including replica-set races and rollback, recorded in
  [the runbook](../operations/PCP-ATOMIC-CAPACITY.md). This is historical scoped CI
  evidence, not a rerun here. Scheduled/retroactive dates are explicitly refused;
  imports/direct writers, external participation limits, plan/location counting,
  reconciliation scheduling and managed-cloud acceptance remain open.

ADRs 006/017 retain domain-owned Mongo stores; 016 retains unified contract pricing;
019 favors configured policy before extensions; 021/022 keep Provider Access
opt-out separate from payer-bound P2P opt-in. ADR 028 uses the originating claim's
nonfinancial EOB for encounter information; do not add a duplicate visit projector.
ADRs 029–031 remain proposed where recorded as such, despite implemented code.

## CA-00: authority, scope and responsibility gate

Executive/legal/compliance must approve a versioned inventory before signing off
any scenario: contracting MCP versus delegate; county/model; contract term/rating
period and actual launch; Knox-Keene/DMHC applicability; populations and duals;
benefit and carve-out responsibilities; subcontractors; effective policies and
technical versions. Public boilerplates and another plan's Exhibit L are not the
prospective plan's executed authority. Record pending approvals explicitly.

Assign an accountable owner to enrollment, provider sources, 274/directory,
encounters, UM/appeals, notices, care/ECM/Community Supports, calls, payments,
accounting, APIs and recovery. An EDI vendor handles only the duties accepted in
its signed interface agreement. The plan still needs reconciliation and exception
ownership. Pharmacy POS, medical drugs, county behavioral health, LTSS, dental,
CCS and Medicare responsibilities must be mapped separately; neither blanket
exclusion nor wholesale inclusion is inferred here.

## Source register and version limits

Official sources accessed on the review date. The source labels below identify
where policy owners must start; executable rules need approved section-level,
effective-date mappings. Uninspected private companion guides are dependencies.

| ID | Authority / scope and version issue |
| --- | --- |
| A1 | [DHCS contract index](https://www.dhcs.ca.gov/providers-partners/medi-cal-managed-care-boilerplate-contracts/) and [2024 boilerplate](https://www.dhcs.ca.gov/provgovpart/Documents/2024-Managed-Care-Boilerplate-Contract.pdf): operational-readiness Attachment II; MIS/encounters/UM and member/provider functions in Attachment III. The index describes extension through December 2027 and plan-specific Exhibit L. Obtain the executed agreement and approved amendments; public postings can include pending CMS approvals. |
| A2 | [Medical APL 25-017](https://www.dhcs.ca.gov/file/file-193916-pdf/): PDF is dated December 26, 2025, covering December 2025–January 2027 and 2026 HCO posting responsibilities. Search metadata differed from the document date. This is medical MCP enrollment, not dental APL 26-001; later schedules remain a launch dependency. |
| A3 | [Encounter overview](https://www.dhcs.ca.gov/data-statistics/data-resources/medi-cal-managed-care-encounter-data-reporting/), [APL 26-003](https://www.dhcs.ca.gov/file/file-193996-pdf/) and [QMED 2.0, March 2026](https://www.dhcs.ca.gov/file/quality-measures-for-encounter-data-pdf/): use current companion guides and QMED 2.0. The older overview still points to QMED 1.1/APL 14-020, which 26-003 supersedes. Its sanctions date is not a postponement of data-quality obligations. |
| A4 | [APL 26-008, May 12, 2026](https://www.dhcs.ca.gov/file/apl-26-008-pdf/) supersedes 22-026 for MCP interoperability/PA. Sections I–VIII cover APIs, use/public metrics, education and oversight. Page 17 specifies US Core 6.1.0/SMART 2.0.0 for the 2026 specification transition, subject to permitted updated versions. It also requires quarterly/annual DHCS reporting and QIC review. This differs from merely testing the older CMS baseline. |
| A5 | [APL 21-011 revised August 31, 2022](https://www.dhcs.ca.gov/file/apl21-011-pdf/), linked in the [current APL register](https://www.dhcs.ca.gov/forms-laws-publications/managed-care-all-plan-letters-1998-to-current/): member appeals, notices and rights templates. Separate Knox-Keene/non-Knox-Keene attachments exist. Its older standard authorization text must be reconciled with current federal/state requirements; do not configure an obsolete 14-day default from this PDF in isolation. |
| A6 | [State-published MCP provider listing](https://data.ca.gov/%40california-department-of-health-care-services/medi-cal-managed-care-provider-listing) describes monthly county-level 274 submissions. A published network extract is not proof of an individual provider's authoritative enrollment, revalidation or historical location status. |
| A7 | [ECM/Community Supports index](https://www.dhcs.ca.gov/calaim-transforming-medi-cal/enhanced-care-management-and-community-supports/) and current APL register identify benefit-specific work. Applicable current policy guides, population/benefit scope and executed amendments must be obtained before configuration. Generic case management does not establish acceptance. |
| A8 | [CMS rule resources](https://www.cms.gov/initiatives/burden-reduction/overview/interoperability/policies-regulations/cms-interoperability-prior-authorization-final-rule-cms-0057-f) and [process/reporting FAQ](https://www.cms.gov/initiatives/burden-reduction/overview/interoperability/frequently-asked-questions/improving-prior-authorization-processes). Required standards, recommended IGs, operational versus API dates and payer applicability need separate mappings. Proposed future rules are not silently binding requirements. |

APL/BHIN numbers are not interchangeable. BHIN 26-008 is county behavioral-health
guidance; medical MCP APL 26-008 is the source above. Some older public pages are
useful indexes but not the controlling version of a requirement.

## Current source findings and closure contracts

These are proposed acceptance criteria, not assertions of state-mandated test
counts or proof that a contract was awarded. IDs CA-01–10 retain their original
meaning; CA-11 onward separate newly expanded gates.

| Finding / priority | Current executing evidence | Accountable owner and evidence to close |
| --- | --- | --- |
| CA-01 — confirmed route gap; P0 | `encounter-submission-service/KafkaConsumers/AdjudicationCompletedConsumer.cs` still accepts FL Medicaid only. `BuildFmmisSubmissionBatchAsync` emits FL JSON and refuses CA. General 837 building elsewhere does not make this automatic route California-ready. | EDI/engineering: approved CHO-to-partner and partner-to-DHCS contracts, current guides, durable tenant/claim/control identities, delegated intake, accepted response/correction/void/replacement scenarios. Source-to-final-acceptance reconciliation, not just transmitted bytes or 999. |
| CA-02 — integrity repaired; acceptance partial; P0 | `BatchIntegrityTests` already includes CA refusal. Distinct syntax statuses prevent a positive 999 asserting final acceptance. Durable staging/reservation/final state response remain separate. | EDI/operations: repeat partial batch/read failures and duplicate delivery, prove no lost/double-counted records, reconcile original/replacement lineage and every outstanding rejection. Compute applicable QMED 2.0 measures from accepted records. |
| CA-03 — enrollment unverified; P0 | Native `enrollment-import-service` lifecycle and coverage records exist. No accepted medical MEDS/834/HCO feed, CIN crosswalk or actual plan-code corpus was supplied. | Enrollment: A2-aligned schedules/controls and actual technical layouts; duplicate/out-of-order files, retroactive changes, newborn/dependent identity, county/aid changes, reinstatement and termination. Replay claims, PCP and capitation effects; reconcile member-months and state totals. |
| CA-04 — notice/PA appeal fulfillment partial; P0 | Correspondence implements composition/rendering step 1. `appeals-service/RulePacks/medicaid/ca.json` has scoped calculations; its aggregate is claim-centric. A JSON rule pack and PA reporting facts do not prove PA appeal/hearing or notice delivery. | UM/member services/legal: approved forms and rights attachments for actual plan type, language/format selection, oral/written intake, representative authority, continuation of benefits, external review/handoff and reversal implementation. Test postmark/send/return evidence, deadlines, retries and unresolved exceptions. |
| CA-05 — financial close partial; P0 | ADR 026's bounded bank-confirmed credits-only claims settlement reaches accounting. `GlSourceEvents` still identifies return/premium/capitation seams; AR aging does not establish a reconciled close. | CFO/payments: opening balances, bank/ERP boundaries, expected-dollar corpus, returns/reversals/reissues, premium and capitation lineage, open-item aging, control totals and two agreed representative close simulations. The two-cycle criterion is a proposed plan acceptance gate. |
| CA-06 — state provider integration partial; P0 | Registered `DhcsPaveSource` returns null and an unimplemented bulk result. Comments naming a Web Services API or monthly FTP are not a verified interface agreement. Native provider/network/credentialing functionality exists. | Provider operations/EDI: authorized enrollment verification source, actual schema/access, service-location/program/effective dates, freshness/history, exclusions and fail/unavailable policy. Separately accept 274, directory content and source-to-published freshness; no universal public PAVE API assumed. |
| CA-07 — contract propagation partial; P1 | `ProviderContractsController.SyncChildren` still returns 501 for capitation children. FFS pricing is already on unified contracts (ADR 016). | Contracting/CFO: effective-dated delegated responsibility and capitation amendments; demonstrate propagation, retries, retroactive adjustments and reconciliation to parent terms without duplicating ERP responsibilities. |
| CA-08 — current-day API concurrency fixed; wider acceptance partial; P0 | `TransactionalPcpAssignmentService` is registered in production. Scope is medical/legacy distinct current members, tenant/NPI; unsupported Mongo/date modes refuse. Production lifecycle writers now join the fence and validate stored future coverage spans. Scheduled/retroactive PCP changes and old/direct DB writers remain outside that supported scope. | Provider/enrollment/engineering: actual plan/location panel policy, state-directed assignment/exception policy, every writer, scheduling/retroactivity, continuity/member choice, and cloud transaction topology. Do not equate current-day race tests with California PCP acceptance. |
| CA-09 — benefit/clinical content unverified; P0 | Native benefit, pricing, COB and PA engines are real. `ClinicalCriteria` ships interfaces/local rules, not a verified complete California content corpus. CRD uses resolved context and distinguishes unknown from no-PA-required; an engine approval alone is not a requirements catalog. | Medical director/benefits: effective-dated coverage, PA-required and clinical criteria catalogs with authority, exceptions and representative cases. Scope EPSDT, LTSS, medical drugs, ECM/CS, behavioral boundaries, continuity and TPL separately. Never import Texas seed thresholds as California policy. |
| CA-10 — deployment readiness unverified; P0 | Tenancy/recovery primitives exist; no actual cloud volume, migration, restore or staffed runbook acceptance was provided. #1547 requires a transaction-capable Mongo deployment. | CTO/SRE/security/operations: real peak workload, on-call and incident ownership, replica/transaction/failover behavior, backups/restores, tenant isolation, monitoring and SLO/RTO/RPO. Membership alone is not a load forecast. |
| CA-11 — annual reporting partial; P0 | `PaReportingEvidence` and `AnnualPaReporting` execute; private packages now retain exact snapshot inputs/results/hashes. Outputs remain unapproved drafts. Legacy/source coverage, verified plan mapping, corrections and approval are not supplied. | Compliance/data/UM: intake-to-report reconciliation across channels and appeal levels, lawful-extension provenance, dual cutoffs and restatements, listed services and an approved immutable report. Accept DHCS oversight/template/QIC deliverables separately from a public annual webpage. |
| CA-12 — API/data/standards acceptance partial; P0 | Native Patient/Coverage/claims/PA and imported clinical paths exist; P2P native export does not establish complete clinical/document exchange. `BulkExportService` refuses initiation. `interop/versions.json` currently describes `us_core_v311` as CHO's historical suite. | Interoperability/security: validate A4's applicable 2026 specifications and USCDI scope, required API data, SMART/security, attribution/consent/revocation, freshness, complete clinical/doc exchange and bulk where required. Test actual US Core 6.1.0/SMART 2.0 or an approved newer version, not a v3.1.1-only green result. This is an unclosed qualification gap, not proof every endpoint is absent. |
| CA-13 — Patient Access usage/directory/education acceptance unverified; P0 | Record-access audit primitives and provider FHIR surfaces exist. No accepted complete distinct-member/repeat-use annual pipeline or California directory/education attestation was established by this review. | Interoperability/provider/member services: successful app disclosures with member deduplication and correct annual counts; accepted public directory content/freshness and DHCS deliverables; accessible member/provider education with explicit consent rights. Do not derive usage from generic HTTP counts. |

## Priority implementation sequence

1. Resolve CA-00 and obtain source/partner packages while engineering works. Give
   enrollment, provider, EDI and notices owners explicit deliverable dates.
2. Close CA-08's temporal/all-writer gap before using the current-day API as the
   only PCP workflow. Implement a separate effective-dated reservation/transition
   design; do not remove the date guard to make a demonstration pass.
3. Build the California encounter handoff and response ledger using actual partner
   contracts. In parallel accept provider-source and directory/274 boundaries.
4. Establish a clinically approved California PA policy/clock catalog. Reconcile
   older APL clock text with the currently applicable shorter federal/state/clinical
   limits, actual notice deadlines and lawful extensions. The current generic
   72/168-hour selector is not that catalog; RFAI must not reset receipt.
5. Complete notice/PA appeal workflows and authoritative reporting sources; approve
   reproducible DHCS/public outputs and source completeness, not just arithmetic.
6. Close CA-12/13 conformance/data/usage gaps with version-specific live-server
   evidence. Existing IG pins/test tools may be reusable, but pins are not conformance.
7. Rehearse enrollment-to-claims-to-payment-to-state acceptance and financial close
   under the agreed failure/recovery/volume corpus before requesting launch approval.

## Acceptance evidence contract

For every scenario in the JSON register, retain scenario/version, source commit,
configuration and policy/guide versions, synthetic corpus identifier, execution
environment, timestamps/control IDs, expected and actual results, evidence hashes,
exceptions/reconciliation, owner and reviewer approval. Sensitive operational
evidence belongs in the plan's controlled evidence store, not public git.

An external result must identify the actual partner/receiver, acceptance layer,
test environment, tested submission and final disposition. A 200/999 or internal
test pass cannot substitute for DHCS, bank, print partner or contracted acceptance.
Do not insert fabricated IDs, signatures, certificates or blanket pass statuses.
An approved exclusion requires its scope/authority/approver and effective dates;
silence or an empty evidence list is not an exclusion or a pass.

## Verification record for this refresh

Source inspection rechecked the paths and boundaries above on the pinned main.
The original refresh added one California consumer characterization demonstrating that a CA Medicaid
paid-claim message creates no record through the FL-only consumer. It is a gap
demonstration, not a runtime fix; existing CA batch refusal coverage is retained.
No fresh executable test count is claimed until CI runs that addition.

The original #1541 record in the older qualification document and the 27-case
#1547 replica-set result retain their own commits/environments. They are not new
full-platform or California acceptance results. No state/partner/bank access,
real-member corpus, managed-cloud load/restore, full US Core 6.1.0/SMART 2.0 server
validation or public submission was supplied/executed. Documentation links,
scenario-ID/reference consistency, JSON parsing and diff checks are the local
checks for this assessment. No runtime behavior, deployment or acceptance status
is changed by defining these gates.
