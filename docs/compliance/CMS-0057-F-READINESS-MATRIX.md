# CMS-0057-F Readiness Matrix

**Current scoped source review:** October 24, 2026, main `b9a00fb4` plus the scoped review branch. The canonical ledger records corrected behavior and actual validation; this is not a deployed compliance attestation. See the [California/CMS requirement workstream](../implementation/CALIFORNIA-MEDICAID-QUALIFICATION.md).
**Owner:** Cloud Health Office product / compliance engineering
**Purpose:** requirement and deployment context for the CMS
Interoperability and Prior Authorization Final Rule (CMS-0057-F).

This matrix describes Cloud Health Office's technical capability posture. It is
not legal advice, regulatory certification, or a payer attestation. Final
compliance depends on the deploying payer's population, lines of business,
configuration, source-system data quality, implementation choices, operating
procedures, and legal / compliance review.

## Evidence reference

[Capability Reconciliation](../CAPABILITY_RECONCILIATION.md) is the canonical
ledger for reconciled statuses, dates, implementation paths, tests and limits.
Its classifications are **Implemented**, **Partially Implemented**,
**Confirmed Gap** and **Unverified**. Deployment integration is a separate
requirement, not a substitute status. The requirement table below summarizes
that evidence; detailed or older ePA assessments do not override it.

## Executive Summary

Cloud Health Office has meaningful CMS-0057-F accelerator assets already in
place: `fhir-service`, SMART-on-FHIR scope enforcement, PAS `$submit`, CRD/DTR
services, Bulk FHIR export scaffolding, consent service, provider-directory
proxies, claims EOB projection, and focused behavioral test evidence. The current
sales-safe framing is **technical readiness for CMS-0057-F implementation**, not
"certified compliance" or "100% compliant out of the box."

The strongest first commercial offer is a **CMS-0057-F Compliance Accelerator**
that deploys beside a payer's existing CAPS and turns Cloud Health Office into the FHIR,
SMART/OAuth, prior-authorization, audit, and evidence layer. The production
implementation still requires payer source-system integration and gap closure
around patient access breadth, payer-to-payer consent/exchange workflow, public
prior-authorization metrics, and adapter mode clarity.

## Pilot Diligence Package

Use these companion artifacts to turn the readiness matrix into a buyer or pilot
workstream:

- [CMS-0057-F-COMPLIANCE-ACCELERATOR-BRIEF.md](CMS-0057-F-COMPLIANCE-ACCELERATOR-BRIEF.md)
  for the one-page buyer-facing offer.
- [CMS-0057-F-PILOT-DILIGENCE-CHECKLIST.md](CMS-0057-F-PILOT-DILIGENCE-CHECKLIST.md)
  for intake, evidence, security, integration, and go/no-go tracking.
- [CMS-0057-F-DEMO-MODE-LIVE-ADAPTERS.md](CMS-0057-F-DEMO-MODE-LIVE-ADAPTERS.md)
  for labeling demo, hybrid, and live payer-backed evidence.
- [CMS-0057-F-PRIOR-AUTH-METRICS-TEMPLATE.md](CMS-0057-F-PRIOR-AUTH-METRICS-TEMPLATE.md)
  for public prior-authorization metrics planning.

## Primary Requirement Matrix

| CMS-0057-F area | Cloud Health Office status | Repo evidence | Gap / implementation note | Buyer-facing position |
| --- | --- | --- | --- | --- |
| Patient Access API expansion | **Unverified** (complete payer workflow) | CAP-05, CAP-22 | Patient, Coverage, Encounter and Claim have CHO-native sources; SMART controls and imported clinical reads exist. All-native composition no longer requires a synthetic provider (CAP-22/27/30 refresh). The May 2026 claims assessment is historical, not current proof that member access or resource breadth is missing. Validate the complete payer-specific workflow. | "Patient-access building blocks; end-to-end source, identity and consent integration requires verification." |
| Provider Access API | **Unverified** (complete payer workflow) | CAP-05; provider/FHIR projections and consent integration records | Attribution/opt-out controls and nonfinancial CARIN claim EOB projection exist. Dynamic attribution and complete deployed conformance remain incomplete/unverified. ADR 028 serves encounter information through the source claim EOB; a second projector is intentionally excluded. Validate selected deployment behavior. | "Provider/FHIR building blocks; payer-specific access policy and integration require verification." |
| Payer-to-Payer API | **Partially Implemented** | CAP-10, CAP-14, CAP-22 | Registered-payer binding, purpose-specific consent/attestation, native member match/export and financial redaction exist. Complete clinical/supporting-document export and Bulk remain incomplete; fully denied PA filtering merged in #1485 (`8e3413a2`). Deployment validation remains separate. | "Implemented payer-client binding with remaining consent, export and integration limits." |
| Prior Authorization API / Da Vinci PAS | **Implemented** (scoped behavior) | PAS controller/recorder, automated-decision API; real-host Mongo seam and recording tests (CAP-01/02) | Submit stores mapped request/decision before response; failure issues no preAuthRef. Stored service-line outcomes are projected. Full profile/response conformance, notification and payer deployment diligence remain separate. | "Persisted PAS submission, inquiry and service-line decision capabilities; full conformance and production readiness require further validation." |
| CRD requirements discovery | **Partially Implemented** | CrdService, CrdController; CrdFailClosedTests (CAP-03) | Unknown/uncoded input and evaluation failures produce uncertainty; explicit classifications can permit no-PA-needed. Full member context and structured coverage actions are separate. | "CRD discovery/cards with explicit uncertainty handling; broader conformance requires validation." |
| DTR documentation templates | **Partially Implemented** | DtrService, MongoDtrResourceStore, host binding and real-Mongo tests (CAP-04/15) | Creates, conditional updates, reads, searches and existence checks use Mongo across pods. Full answer validation/canonical resolution and package semantics remain incomplete. | "Mongo-backed DTR resource operations and searches, with remaining validation, canonical and package limitations." |
| Prior-authorization decision timelines | **Implemented** (receipt target) | AuthorizationDeadlinePolicy, SlaWatchdogService, receipt/real-store tests (CAP-07) | Receipt-v1 persists UTC deadlines and preserves receipt across RFAI. Program-specific extensions, tighter rules, alert delivery and per-tenant-database workers require separate validation/work. | "Receipt-based authorization deadline tracking and tenant-explicit escalation; deployment policy must be validated." |
| Denial reason and status transparency | **Implemented** (decision controls) | AuthorizationDecisionPolicy, decision API, PAS response builder; decision-integrity/Mongo reload tests (CAP-02) | Complete line sets, quantities and coded/human-readable adverse reasons enforced before writes. Full PAS/X12 code semantics and notices are not established. | "Persisted line-level decisions and adverse reasons with PAS projection; payer taxonomy and notice workflow remain deployment work." |
| Public prior-authorization metrics reporting | **Partially Implemented** | Cms0057Reporting; PaReportingEvidence; AnnualPaReporting; PaReportingPackageRepositoryMongo (CAP-06/33/39) | Native receipt/decision capture, sourced appeal/extension facts, bitemporal annual drafts and private snapshot packages with retained inputs/hashes are implemented. A draft list of PA-required items and services (drugs excluded) is generated from the evaluated rule set (`GET api/v1/prior-auth-rules/required-services`, JSON or CSV). All-channel/all-level source reconciliation, accepted plan mapping, policies held outside the rule engine, approved timing/restatement semantics, DHCS outputs and public approval/publication remain open. Package publication readiness remains false. | "Reproducible private reporting evidence; complete public reporting requires source acceptance and publication approval." |
| Provider Directory API | **Unverified** (production directory workflow) | provider-service FHIR projectors and directory proxies; existing directory tests | Projector/proxy code is not fresh proof of source freshness, public endpoint behavior or complete network directory conformance. Validate those separately. | "Provider-directory projections and proxies; deployment workflow requires verification." |
| SMART on FHIR / OAuth scopes | **Partially Implemented** | LaunchContextController, MongoLaunchContextStore, SmartTokenContextResolver, SmartAppApprovals; launch/withdrawal/end-to-end tests (CAP-05) | Launch binding and patient/encounter token context implemented; withdrawing approval ends refresh. Do not infer every response field, SMART v2/asymmetric auth or all revocation semantics. | "SMART launch context and approval withdrawal are implemented; full standards and deployment readiness require separate validation." |
| Bulk FHIR export | **Confirmed Gap** | `BulkExportController.cs`; `BulkExportService.cs`; `Models/BulkExportModels.cs`; `tests/CloudHealthOffice.FhirService.Tests/Services/BulkExportServiceTests.cs` | The default export path now refuses initiation with HTTP 501 and a FHIR not-supported OperationOutcome; it creates no job, manifest, or download URLs. A real exporter still requires durable jobs, NDJSON output, storage, encryption, lifecycle, manifest retention, and recipient access controls. | "Bulk export is planned; the current path is a stub." |
| Audit, privacy, and tenant controls | **Partially Implemented** | CAP-08–12, CAP-21, CAP-22 | Scoped isolation/binding fixes have test evidence; default mock sources and deployment-specific controls remain distinct. BAA, security review, incident/DR and isolation diligence are per deployment. | "Verified scoped controls, with deployment-specific diligence still required." |

## Evidence snapshot

The canonical ledger records **315 focused passing tests** at the initial
`d5af561d` baseline and **45 refreshed passing checks** after lifecycle PR #1409.
These are scoped runs, not a new execution of the entire FHIR suite, production
acceptance or complete standards validation. Use the recorded project/filter,
commit and limits whenever citing them.

## Known Sales-Readiness Risks

1. **Overclaim drift.** Older docs still use phrases such as "100% compliant"
   or "production-ready" for broad CMS-0057-F posture. Derivative materials
   must use the canonical capability ledger before reuse.
2. **Production source composition requires deployment acceptance.** All-native
   bindings no longer require the legacy synthetic provider; selected mocks
   remain guarded. Validate the intended deployment with synthetic permission
   disabled. Source composition is implemented, deployed availability unverified.
3. **Payer-to-payer workflow is not yet turnkey.** Bulk export and consent
   services are useful building blocks, but a deployable payer-to-payer package
   needs more workflow, security, and audit evidence.
4. **Prior-authorization metrics reporting needs a product artifact.** CMS
   reporting can become a differentiator if Cloud Health Office generates the public report and
   supporting audit package.
5. **Legal/compliance evidence is not the same as code readiness.** Payers will
   still need BAA, deployment review, source-system integration validation,
   security controls, and compliance counsel sign-off.

## Suggested diligence workstreams (not a delivery commitment)

See [strict fixture validation](FHIR-VALIDATION-MODES.md) and [reporting data quality](../operations/CMS0057-REPORTING-DATA-QUALITY.md) before using fixture or console output as evidence. Required standards and recommended IGs are different categories; product pins are not automatically legal mandates.

### Evidence and claim review

- Use the dated reconciliation evidence with this matrix; verify historical findings against current code before reuse.
- Update public docs and whitepapers that overstate readiness.
- Add a one-page Compliance Accelerator buyer brief.
- Add a pilot-specific diligence checklist covering BAA, HIPAA controls,
  deployment, source-system integration, identity, audit, and support.
- **Shipped September 2026:** founding-partner offer sheet, CISO diligence
  binder, labeled `GET /fhir/r4/adapter-status`, synthetic `demo-tenant`.

### Demonstration and reporting evidence

- Publish sample `/fhir/r4/compliance-status` output for a demo tenant.
- Review the existing public metrics template against the implemented aggregates and remaining appeal/extension limitations.
- Add a demo script covering Patient Access, Provider Directory, PAS `$submit`,
  pended/denied status, and the explicitly unfinished Bulk Export boundary.
- Document demo-mode vs live-adapter behavior.

### Deployment integration

- Replace default mock adapter ambiguity with explicit `DemoMode` configuration.
- Wire live service-backed adapters for highest-value FHIR resources first:
  `Patient`, `Coverage`, `ExplanationOfBenefit`, `Practitioner`,
  `PractitionerRole`, `Organization`, `Claim`, and `ClaimResponse`.
- Review existing patient-bound, launch, approval-withdrawal and isolation tests before identifying any additional deployment-specific test gaps.

### Pilot scoping

- Define pilot scope and schedule from the actual integration and diligence requirements.
- Define founding-payer commercial terms.
- Prepare a synthetic-data demo environment.
- Target Medicaid MCOs, Medicare Advantage plans, QHP issuers, and
  implementation partners with the same evidence package.

## References

- CMS, Interoperability and Prior Authorization Final Rule (CMS-0057-F):
  <https://www.cms.gov/initiatives/burden-reduction/overview/interoperability/policies-regulations/cms-interoperability-prior-authorization-final-rule-cms-0057-f>
- CMS fact sheet:
  <https://www.cms.gov/newsroom/fact-sheets/cms-interoperability-prior-authorization-final-rule-cms-0057-f>
- Federal Register final rule:
  <https://www.federalregister.gov/documents/2024/02/08/2024-00895/medicare-and-medicaid-programs-patient-protection-and-affordable-care-act-advancing-interoperability>
- HL7 Da Vinci:
  <https://confluence.hl7.org/display/DVP/Da+Vinci+Project>
