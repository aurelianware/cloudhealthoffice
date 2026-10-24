# California Medicaid qualification and CMS-0057-F workstream

Follow-up after merged PR #1541: [PA reporting evidence foundation](../operations/PA-ANNUAL-REPORTING-EVIDENCE.md)
adds native receipt/decision capture, sourced appeal/extension facts and an as-of
annual draft. This advances the reporting row below without closing its source,
plan-scope, required-services-list or publication acceptance requirements.
The validation record at the end remains the original #1541 record.

Source review: October 24, 2026, main
`b9a00fb4eaca20757d02ae8b6bcea51fe6bacc58`, followed by the scoped fixes linked
below. Target: a new 250,000-member California Medicaid managed care plan,
native CHO core, plan-hosted Azure/GCP. The plan retains call center, claims
operations and payment operations. EDI fulfillment may be contracted; a
partner's existence or acceptance is not assumed.

The [capability ledger](../CAPABILITY_RECONCILIATION.md) remains the status
source. This document is the requirement/acceptance backlog, not a second
whole-platform maturity score. An implementation fix is not state, bank or
regulatory acceptance. No production data, deployment or submission is part of
this change.

## Architecture applied

- ADRs 005/006/017: Kubernetes runtime, domain-owned stores, MongoDB document
  persistence; no native Cosmos SQL SDK and no new shared database.
- ADRs 008/018/020/024: owning services project FHIR; fhir-service applies the
  external identity/access policy. ADR 023's managed FHIR store is a downstream
  copy, not a substitute core or an accepted CMS export service.
- ADRs 016/019: FFS reimbursement belongs on unified contracts; configure
  plan rules first, then use versioned out-of-process extensions. Do not build
  a California source fork for policy that existing configuration can express.
- ADRs 021/022: Provider Access uses opt-out after attribution; P2P requires
  requesting-payer-bound consent. Preserve those separate decisions.
- ADR 028: serve encounter information through the originating claim's
  nonfinancial EOB. A second projector would duplicate the same visit. Revisit
  this only if independent encounters enter the source system.
- ADRs 010/011/026/027: durable scoped evidence, honest scoring, bounded
  settlement accounting, engagement-specific external-core connectors.
- Proposed ADR 029 records the bounded terminology projection correction.
  Proposed ADRs and implemented code are reported separately; this work does
  not silently declare every proposed decision accepted.

## California launch backlog

| Priority | Finding / evidence | Next acceptance work and owner |
| --- | --- | --- |
| P0 | **CA-01 — confirmed missing California path:** `encounter-submission-service/KafkaConsumers/AdjudicationCompletedConsumer` gates to FL Medicaid. Its batch builder emits FMMIS JSON. `encounter-service` has general 837 building, but that is not evidence of PACES acceptance. | EDI lead: obtain current DHCS companion guides/test access and define a California partner handoff over actual claim/encounter source records. Preserve claim/control identities, delegated encounter completeness, retries, per-record responses, correction/void/replacement and reconciliation. Do not remove the FL gate to pretend CA is supported. |
| P0 | **CA-02 — corrected scoped batch/acknowledgment defects:** missing claim reads previously skipped while all records were batched; a positive 999 wrote final Accepted. | [Batch-integrity runbook](../operations/ENCOUNTER-BATCH-INTEGRITY.md). Remaining: durable staging/reservations and final state response. EDI/operations must reconcile historical acceptance counts. |
| P0 | **CA-03 — California enrollment acceptance unverified:** 834 intake and native member/coverage lifecycles exist. No accepted plan-specific feed/corpus supplied. | Enrollment lead: actual feed specification, CIN and dependent identity mapping, retroactivity, county/aid/category changes, newborn/reinstatement/termination, duplicate/out-of-order records and downstream capitation/claims replay. Treat a generic parser as implemented, plan acceptance as open. |
| P0 | **CA-04 — correspondence fulfillment partial:** correspondence controller/rendering implement step 1 of `architecture/correspondence.md` §16; triggers, channels and per-letter gates remain later steps. | Operations/compliance: approve current forms, threshold-language/accessible variants, event triggers, fulfilled notice deadlines, print/electronic partner receipts, retry/returned-mail and reconciliation. Do not substitute a PDF render for delivery. |
| P0 | **CA-05 — integrated finance partial:** ADR 026 credits-only claims settlement event is implemented. `GlSourceEvents` still names unproduced return/capitation/premium seams; AR aging sums stored buckets. | CFO/engineering: accounting ADR and authoritative open items/due dates, bank/ERP responsibility matrix, durable events, period controls, returns/reissues/reversals, two complete close simulations and opening-balance reconciliation. Do not duplicate functions deliberately owned by an ERP. |
| P1 | **CA-06 — provider verification/integration partial:** `DhcsPaveSource` returns null and unimplemented bulk results. General provider/network administration is real. | Provider operations: choose an authorized verified state source/import, demonstrate freshness and sanctions/enrollment validation, accepted network reporting including applicable 274; do not invent a universal PAVE API. |
| P1 | **CA-07 — delegation/amendment propagation partial:** contract sync-children returns 501 for capitation copies; FFS already uses contract terms under ADR 016. | Provider contracting/CFO: effective-dated financial responsibility, capitation amendment propagation and reconciliation. The obsolete ffs-service Dockerfile is not a launch prerequisite. |
| P1 | **CA-08 — PCP capacity partial:** unavailable-count bypass corrected; read-before-insert race and unscheduled reconciliation remain. | [Capacity runbook](../operations/PCP-PANEL-UNAVAILABLE.md). Provider operations/engineering must select authoritative count/effective-date scope and atomic reservation/reconciliation design before claiming hard panel limits. |
| P1 | **CA-09 — California benefit/rule acceptance unverified:** native benefits, pricing, COB, PA, rule packs and extensions exist. | Clinical/compliance: actual contract/benefit corpus, EPSDT, LTSS, behavioral-health boundaries, ECM/Community Supports, medical drugs, TPL and tighter state clocks. Use configured rules/extensions under ADR 019; do not invent a complete state benefit pack. |
| P0 | **CA-10 — production acceptance unverified:** identity, tenancy and recovery building blocks exist. | CTO/security: intended Azure/GCP tenant, synthetic disabled, least privilege, load/failure/recovery, on-call, restore and partner acceptance. Real Mongo tests do not establish managed-cloud compatibility, capacity or an SLA. |

California sources reviewed:
[DHCS encounter reporting](https://www.dhcs.ca.gov/data-statistics/data-resources/medi-cal-managed-care-encounter-data-reporting/),
[DHCS companion-guide/274 FAQ](https://www.dhcs.ca.gov/data-statistics/dhcs-data-reporting-and-monitoring-webinar-series-frequently-asked-questions/),
[ECM/Community Supports](https://www.dhcs.ca.gov/calaim-transforming-medi-cal/enhanced-care-management-and-community-supports/).
The actual contract, APLs and current partner-access companion guides govern
plan acceptance. No guide unavailable to this review is treated as inspected.

## CMS-0057-F applicability and acceptance matrix

The plan is in the Medicaid managed care impacted-payer category. Confirm
launch/rating-period-specific applicability with compliance. Baseline rule:
[CMS final-rule fact sheet](https://www.cms.gov/newsroom/fact-sheets/cms-interoperability-prior-authorization-final-rule-cms-0057-f),
[final-rule resource page](https://www.cms.gov/initiatives/burden-reduction/overview/interoperability/policies-regulations/cms-interoperability-prior-authorization-final-rule-cms-0057-f).
Operational/API timing and exact payer provisions must not be collapsed into
one universal due date.

| Requirement / source | Executing evidence | Remaining acceptance / implementation |
| --- | --- | --- |
| Patient Access including applicable non-drug PA data | `FhirDataBindings`, native router, claims financial EOB, authorization-service PA EOB and clinical resource controller | Validate complete actual data, source freshness, patient/app identity and disclosure evidence, required profiles/searches and production controls. All-native startup fix already exists. |
| Provider Access claims/encounters, clinical/PA data, attribution and opt-out | `ProviderAccessAuthorizationService`, consent opt-out, nonfinancial claim EOB; ADR 028 deliberately uses source claims for encounters | Dynamic authoritative attribution and end-to-end data/conformance acceptance. Do not create a duplicate encounter EOB projector. |
| P2P opt-in, payer binding, required data and lookback | ADRs 020/022 native sources and consent attestations, financial redaction, active/recent PA filter | Native export source currently returns Patient, Coverage, claim EOB and PA EOB, not complete clinical/supporting-document export. Member-match input remains a CHO DTO rather than HRex Parameters. Scoped multi-payer workflows, timing and deployment acceptance remain open. |
| PA requirements discovery, documentation, submission/response and adverse reasons | PAS recorder/response, CRD uncertainty, durable DTR | Full selected CRD/DTR/PAS contracts, canonical/package validation, expiration semantics and actual payer source data remain partial. Do not treat implementing all optional IG features as a statutory requirement. |
| Receipt-based decision clocks across channels | `AuthorizationDeadlinePolicy` and watchdog | Plan-specific tighter/clinical urgency rules, lawful extensions and delivered notices; authoritative intake of fax/phone/portal/API. Generic 72/168-hour targets do not prove every plan clock. |
| Non-drug public PA metrics and items/services list | `Cms0057Reporting`, authenticated CMS console | Scoped arithmetic/receipt fixes are implemented. Authoritative appeals/extensions, annual final outcomes/as-of history, complete listed services and public artifact remain open. See [reporting runbook](../operations/CMS0057-REPORTING-DATA-QUALITY.md). |
| Patient Access unique-member/repeat-use reporting | Shared record-access/audit primitives exist | No dedicated complete usage-reporting pipeline established by this review. Durable successful patient/app disclosure capture, identity/deduplication, annual aggregation and CMS submission acceptance are required. Do not infer these counts from arbitrary logs or total HTTP requests. |
| Bulk Data where required for the selected API/architecture | Default `BulkExportService` refuses with explicit unsupported response | Implement durable scoped jobs, actual NDJSON/storage, authorized/expiring downloads, consent/revocation/restart/retry and acceptance. Generic 501 is honest but does not satisfy required bulk behavior. See Table H3 of the final rule for API-specific applicability. |
| Required standards vs recommended guides | Rule fact sheet lists required standards and recommended IGs separately; product pins in `interop/versions.json` and validator manifest | Assess exact required API-specific versions/approved updates. Product PDex 2.1.0/CARIN 2.2.0 pins and CRD/DTR/PAS 2.2.x targets are not evidence every version is mandated. Never downgrade correct profile canonicals to fit an outdated test kit. |
| Profile/terminology/server validation | Checksum-pinned HL7 validator, goldens from real projectors, pinned external test-kit entries | New strict mode requires terminology and no baseline exceptions. It remains fixture testing. Live reference resolution, SMART/backend security, data completeness and payer-server conformance runs are separate. |

Additional primary sources:
[CMS process/reporting FAQ](https://www.cms.gov/initiatives/burden-reduction/overview/interoperability/frequently-asked-questions/improving-prior-authorization-processes),
[CMS metrics template](https://www.cms.gov/prior-authorization-metrics-reporting-overview-template),
[HRex 1.1 member-match](https://hl7.org/fhir/us/davinci-hrex/STU1.1/OperationDefinition-member-match.html).
The FAQ governs operational/reporting interpretation; the template's expected
denominators and recommended format are distinguished from the rule text.
The [2026 CMS drugs/standards proposal](https://www.cms.gov/newsroom/fact-sheets/2026-cms-interoperability-standards-prior-authorization-drugs-proposed-rule)
is a proposal, not silently applied as a final requirement in this change.

## Implementation sequence and external boundaries

1. Review the scoped commits: reporting integrity, encounter integrity,
   capacity-unavailable handling, strict validation and terminology projection.
2. Select the actual California EDI/notice contracts with the plan. Then write
   focused ADRs for durable handoff/response boundaries and acceptance scenarios.
3. Design authoritative appeal/extension/annual reporting, Patient Access usage
   capture, and complete P2P clinical/document export as separate durable work.
   Do not add fields with no authoritative producers and mark reporting done.
4. Design remaining finance and panel reservations with owners under the
   existing domain boundaries; run realistic acceptance corpora and recovery.

This work does not authorize bank/state production submissions, cloud changes,
regulatory publication or a declaration of full compliance. Software defects
fixed here do not resolve missing external evidence. Unblocked fixes are
implemented; larger cross-domain features remain explicit work rather than
unreviewed rewrites without their required ADRs and source contracts.

## Validation record — October 24, 2026

- .NET SDK 8.0.426 installed for this review. Full solution Release build
  completed with zero warnings and zero errors, including the final projection
  changes. No warning policy was relaxed.
- 206 focused Fact/InlineData cases passed, zero failed/skipped, using a
  temporary reflection-based method harness over the actual compiled tests.
  Covered CMS reporting, PA EOB, encounter batch integrity, PCP counter and
  assignment/controller behavior, portal CMS page/client, claim procedure
  vocabulary, billed/financial claim projections and all 11 golden drift tests.
  This is **not** a VSTest suite pass: VSTest 17.11.1 crashes inside
  `TestEngine.ShouldRunInProcess` before execution in this environment.
  Collection fixtures, full discovery/parallelism and real-Mongo integration
  tests were not executed by the temporary harness. Standard CI remains required.
- A comparison worktree at unmodified `b9a00fb4` reproduced 21 failing cases
  (13 passed) across reporting, batch and panel-counter expectations. The
  comparison used integer enum values and reflective access to the new
  diagnostic field so those expectations could compile against the old API;
  it is not a historical full-suite result.
- Seven Python tests passed for strict/default validation gates and focused
  baseline selection. These use a synthetic validator, not a conformance oracle.
- Goldens regenerated using the actual owning projectors and compared again.
  Strict HL7 7.0.3 validation of the PA golden against PDex 2.1.0, with
  `https://tx.fhir.org/r4` terminology, completed with **zero errors and five
  warnings**. Before the projection fixes the same strict PA check had three
  errors (display, procedure value-set result, denial binding).
- Broad strict fixture validation remains **failed/incomplete**: ten fixtures
  produced verdicts, one inpatient Basis check produced no verdict due to an
  external terminology/network timeout. Seven completed CARIN EOBs each retain
  the standalone `careTeam.provider.resolve()` Practitioner-context error.
  The raw Claim fixture retains an unknown synthetic NDC plus an unresolved
  place-of-service extension; the native Encounter fixture retains its
  unresolved extension. No new exceptions hide these.
- A subsequent full default regression run completed all 11 verdicts and
  **failed** on one unbaselined NDC result: `00002771501` is unknown to the
  validator's NDC `2021-11-01` terminology version. The remaining errors match
  existing reference/extension baselines. This does not establish that the NDC
  is invalid under every terminology release; verify fixture provenance and
  terminology currency before changing it. No new exemption was added.
- The CARIN errors from labelling J1745/D2740 as CPT were corrected in source,
  with behavioral tests and goldens. A care-team reference error in a single
  resource is not proof the live provider endpoint is absent: validate the
  intended server/reference context. A synthetic fixture NDC rejection is not
  proof every production NDC is wrong. Native Claim/Encounter extension findings
  are distinct from ADR 028's nonfinancial Provider Access payload.
- Local changed-document link destinations, golden JSON parsing, shell syntax
  and `git diff --check` passed. No production environment, state submission,
  bank transmission or regulatory publication was performed.

Reproduce in an ordinary repository environment:

```bash
dotnet build cloudhealthoffice-main.sln --configuration Release -m:1
dotnet test src/services/authorization-service/AuthorizationService.Tests/AuthorizationService.Tests.csproj -m:1
dotnet test tests/CloudHealthOffice.EncounterSubmissionService.Tests/CloudHealthOffice.EncounterSubmissionService.Tests.csproj -m:1
dotnet test src/services/coverage-service/CoverageService.Tests/CoverageService.Tests.csproj -m:1
dotnet test tests/CloudHealthOffice.ClaimsService.Tests/CloudHealthOffice.ClaimsService.Tests.csproj -m:1
dotnet test src/portal/CloudHealthOffice.Portal.Tests/CloudHealthOffice.Portal.Tests.csproj -m:1
dotnet test tests/CloudHealthOffice.FhirConformance.Tests/CloudHealthOffice.FhirConformance.Tests.csproj -m:1
python3 scripts/interop/tests/test_fhir_validate_modes.py
FHIR_VALIDATION_STRICT=1 FHIR_TERMINOLOGY_SERVER=https://tx.fhir.org/r4 \
  ./scripts/interop/fhir-validate.sh prior-authorization-eob
```

Commands list the normal CI/reviewer route, not a claim that all full suites
ran here. Use the configured network proxy and approved terminology endpoint
as appropriate; never disable TLS or replace licensed content to make a run
green. See the runbooks for enum compatibility and financial/operational
acceptance boundaries before rollout.
