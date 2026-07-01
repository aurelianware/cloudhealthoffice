# CMS-0057-F Readiness Matrix

**Status as of:** July 2026
**Owner:** Cloud Health Office product / compliance engineering
**Purpose:** canonical cross-service readiness record for the CMS
Interoperability and Prior Authorization Final Rule (CMS-0057-F).

This matrix describes Cloud Health Office's technical capability posture. It is
not legal advice, regulatory certification, or a payer attestation. Final
compliance depends on the deploying payer's population, lines of business,
configuration, source-system data quality, implementation choices, operating
procedures, and legal / compliance review.

## Status Legend

| Status | Meaning | Buyer-facing wording |
| --- | --- | --- |
| **Implemented** | Code and tests exist in repo for the named capability, and the capability can be demonstrated without custom development. | "Implemented technical capability." |
| **Integration required** | Cloud Health Office has the service surface or projection pattern, but production use depends on wiring payer source systems, live adapters, tenant config, and operational controls. | "Available as part of implementation." |
| **Phase 2 required** | Known work remains before this should be represented as production-ready for payer attestation. | "Roadmapped / implementation gap." |
| **Out of platform scope** | Requirement typically depends on a payer, PBM, HIE, provider, or state system outside platform ownership. | "Coordinate through payer ecosystem integration." |

## Executive Summary

Cloud Health Office has meaningful CMS-0057-F accelerator assets already in
place: `fhir-service`, SMART-on-FHIR scope enforcement, PAS `$submit`, CRD/DTR
services, Bulk FHIR export scaffolding, consent service, provider-directory
proxies, claims EOB projection, and a passing FHIR test suite. The current
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
| Patient Access API expansion | **Integration required** | `src/services/fhir-service/Controllers/PatientController.cs`, `CoverageController.cs`, `ExplanationOfBenefitController.cs`, `PatientAccessController.cs`; `src/services/fhir-service/Middleware/SmartScopeEnforcementMiddleware.cs`; `tests/CloudHealthOffice.FhirService.Tests/PatientAccessMapperTests.cs`; `docs/compliance/claims-cms-0057-f-readiness.md` | FHIR service has resource surfaces and SMART enforcement. Claims-domain doc still flags broader unauthenticated/member access, `_history`, search-parameter completeness, and cross-service resource parity as Phase 2. | "Cloud Health Office provides the Patient Access API foundation; production readiness depends on payer identity, consent, member, coverage, claims, and source-system integration." |
| Provider Access API | **Integration required** | `SmartScopeEnforcementMiddleware.cs`; provider-directory controllers/proxies in `src/services/fhir-service/Controllers/ProviderDirectoryController.cs`; provider projections in `src/services/provider-service/Controllers/FhirPractitionerController.cs`, `FhirPractitionerRoleController.cs`, `FhirOrganizationController.cs`; consent service under `src/services/consent-service/` | Provider access requires attributed-provider logic, patient opt-out/authorization policy, and payer-specific data-minimization rules. | "Cloud Health Office has the SMART/FHIR and provider-resource foundation; attributed access and opt-out workflows are implementation work." |
| Payer-to-Payer API | **Phase 2 required** | Bulk export controller/service in `src/services/fhir-service/Controllers/BulkExportController.cs` and `Services/BulkExportService.cs`; consent lifecycle in `src/services/consent-service/` | Needs end-to-end member opt-in, five-year historical data scoping, outbound/inbound payer exchange workflow, receiving-payer audit, and production export storage/security pattern. | "Cloud Health Office has Bulk FHIR and consent building blocks; payer-to-payer exchange should be sold as an implementation workstream, not a turnkey claim." |
| Prior Authorization API / Da Vinci PAS | **Implemented / integration required** | `src/services/fhir-service/Controllers/PasController.cs`; `Services/PasAutoAdjudicator.cs`; `Services/PasResponseBuilder.cs`; `src/services/authorization-service/`; `src/engines/CloudHealthOffice.PriorAuthRuleEngine/`; tests under `tests/CloudHealthOffice.FhirService.Tests/Services/Pas*` and `Controllers/Pas*` | PAS `$submit`, auto-decision/pending behavior, and authorization persistence path exist. Production deployment requires payer rule loading, utilization-management policy review, attachment workflow, denial-reason governance, and source-system reconciliation. | "PAS technical surface is implemented; payer rules and operational governance are deployment-specific." |
| CRD requirements discovery | **Implemented / integration required** | `src/services/fhir-service/Controllers/CrdController.cs`; `Controllers/CrdConfigController.cs`; `Services/CrdService.cs`; `tests/CloudHealthOffice.FhirService.Tests/Services/CrdServiceTests.cs` | Needs payer-specific rule configuration and provider/EHR launch workflow validation. | "CRD service is available for pilot configuration." |
| DTR documentation templates | **Integration required** | `src/services/fhir-service/Controllers/DtrController.cs`; `Services/DtrService.cs`; `Models/DtrConfig.cs`; `tests/CloudHealthOffice.FhirService.Tests/Services/DtrServiceTests.cs` | DTR is present as service/model support; production questionnaires, prepopulation, and clinical-document governance are payer-specific. | "DTR is available as an implementation module." |
| Prior-authorization decision timelines | **Implemented / integration required** | `PasAutoAdjudicator.cs`; `authorization-service` status models/controllers; compliance checks in `Cms0057ComplianceChecker.cs`; tests under `tests/CloudHealthOffice.FhirService.Tests/Services/PasAutoAdjudicatorTests.cs` | Timeline logic exists, but production SLA compliance depends on operational queues, manual review workflows, and escalation reporting. | "Cloud Health Office can track and enforce decision windows once payer workflow is configured." |
| Denial reason and status transparency | **Implemented / integration required** | PAS response builder and authorization-service models/controllers; appeal profiles under `docs/fhir/profiles/`; tests under `PasResponseBuilderTests.cs` | Requires payer-approved denial reason taxonomy, clinical guideline references, reviewer workflow, and letter/correspondence alignment. | "Cloud Health Office can surface structured denial/status data; payer policy content is implementation-specific." |
| Public prior-authorization metrics reporting | **Phase 2 required** | Related observability primitives in `CloudHealthOffice.Infrastructure`; authorization-service data model; portal/monitoring docs | Needs explicit annual/public metrics aggregation, report generator, publication workflow, and audit snapshot retention. | "A high-priority compliance evidence module to add before payer go-live." |
| Provider Directory API | **Implemented / integration required** | `src/services/fhir-service/Controllers/ProviderDirectoryController.cs`; `src/services/provider-service/Services/FhirPractitionerProjector.cs`, `FhirPractitionerRoleProjector.cs`, `FhirOrganizationProjector.cs`; tests under `tests/CloudHealthOffice.FhirService.Tests/Controllers/ProviderDirectory*` | Resource projections and proxies exist. Production readiness depends on network/source-system freshness, endpoint publication, and directory-update operations. | "Provider Directory FHIR surface is one of Cloud Health Office's stronger readiness areas." |
| SMART on FHIR / OAuth scopes | **Implemented / integration required** | `src/services/smart-auth-service/`; `SmartScopeEnforcementMiddleware.cs`; `SmartConfigurationController.cs`; tests under `tests/CloudHealthOffice.SmartAuth.Tests/` and `tests/CloudHealthOffice.FhirService.Tests/` | Scope enforcement exists. Production requires issuer/client registration, patient/provider identity model, third-party app registration process, and audit controls. | "SMART enforcement is implemented; identity-provider onboarding is deployment-specific." |
| Bulk FHIR export | **Implemented / integration required** | `BulkExportController.cs`; `BulkExportService.cs`; `Models/BulkExportModels.cs`; `tests/CloudHealthOffice.FhirService.Tests/Services/BulkExportServiceTests.cs` | Current export path is a technical scaffold; production use requires storage, encryption, lifecycle, manifest retention, and recipient access controls. | "Bulk export foundation exists; production storage and exchange controls remain implementation work." |
| Audit, privacy, and tenant controls | **Integration required** | Tenant middleware across services; `src/services/consent-service/`; HIPAA docs under `docs/features/HIPAA-*`; observability/PHI scrubbing tests | Strong building blocks exist. Payer launch requires deployment-specific BAA, security review, logging retention, incident response, backup/DR, and tenant isolation validation. | "Security architecture is credible; diligence package must be assembled per pilot." |

## Evidence Snapshot

The current repo can demonstrate:

- `dotnet test tests/CloudHealthOffice.FhirService.Tests/CloudHealthOffice.FhirService.Tests.csproj --no-restore`
  passing (see CI for current count).
- `npm run build` passing for the TypeScript generator/package.
- FHIR service support for PAS, CRD, DTR, Bulk Export, metadata,
  OperationDefinition, StructureDefinition, CodeSystem, ValueSet, Patient,
  Coverage, ExplanationOfBenefit, Provider Directory, and appeal-related
  resources.
- SMART scope enforcement that binds patient-scoped tokens to patient resource
  access.
- Consent-service lifecycle, encryption options, repository tests, and event
  publisher tests.

## Known Sales-Readiness Risks

1. **Overclaim drift.** Older docs still use phrases such as "100% compliant"
   or "production-ready" for broad CMS-0057-F posture. Derivative materials
   should reconcile to this matrix.
2. **Mock/default adapter ambiguity.** `fhir-service` registers mock data
   adapters for some resources in development. Buyer-facing demos should label
   demo mode explicitly and show which resources are backed by live services.
3. **Payer-to-payer workflow is not yet turnkey.** Bulk export and consent
   services are useful building blocks, but a deployable payer-to-payer package
   needs more workflow, security, and audit evidence.
4. **Prior-authorization metrics reporting needs a product artifact.** CMS
   reporting can become a differentiator if Cloud Health Office generates the public report and
   supporting audit package.
5. **Legal/compliance evidence is not the same as code readiness.** Payers will
   still need BAA, deployment review, source-system integration validation,
   security controls, and compliance counsel sign-off.

## Recommended 90-Day Sequence

### Days 1-15: Make the story diligence-ready

- Treat this matrix as canonical.
- Update public docs and whitepapers that overstate readiness.
- Add a one-page Compliance Accelerator buyer brief.
- Add a pilot-specific diligence checklist covering BAA, HIPAA controls,
  deployment, source-system integration, identity, audit, and support.

### Days 16-45: Build proof artifacts

- Publish sample `/fhir/r4/compliance-status` output for a demo tenant.
- Add a public prior-authorization metrics report template.
- Add a demo script covering Patient Access, Provider Directory, PAS `$submit`,
  pended/denied status, and Bulk Export job flow.
- Document demo-mode vs live-adapter behavior.

### Days 46-75: Harden implementation gaps

- Replace default mock adapter ambiguity with explicit `DemoMode` configuration.
- Wire live service-backed adapters for highest-value FHIR resources first:
  `Patient`, `Coverage`, `ExplanationOfBenefit`, `Practitioner`,
  `PractitionerRole`, `Organization`, `Claim`, and `ClaimResponse`.
- Add tests for patient-bound access, provider-attributed access, opt-out,
  consent revocation, and denied cross-patient queries.

### Days 76-90: Package the pilot

- Create a fixed-scope 6-8 week CMS-0057-F Compliance Accelerator pilot plan.
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
