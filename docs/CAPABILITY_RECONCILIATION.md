# Capability Reconciliation

**Verified:** October 19, 2026 (America/Phoenix)
**Initial implementation baseline:** remote `main` at `d5af561d490f1c440c7cd7fb1098fa3e18d8521c`.

**Original reconciliation base integrated:** main `1b38d780` (October 19, 2026); its change after `3b5a2035` updates published interoperability JSON only, with no application-code changes.

**Conflict-resolution refresh:** merged `main` at `3b5a2035` on October 19, 2026, including PR #1409. Existing test results remain tied to their original baseline; the lifecycle addendum records new checks against the refreshed branch.
**Original reconciliation branch:** `docs/capability-reconciliation`, separate checkout; unrelated work preserved.

**DTR durability follow-up base:** main `ec62f453` (October 19, 2026); CAP-04/09 and the follow-up below record the scoped implementation change.

## Evidence policy and classification

Use the latest verified implementation as the source of truth. Do not repeat resolved historical gaps. Describe an implemented capability accurately even when full standards conformance or production readiness is unverified. Keep negative claims as narrow and evidence-based as positive claims.

- **Implemented:** reviewed executing path and supporting behavioral tests establish the named scoped behavior.
- **Partially Implemented:** meaningful behavior is verified but an identified part remains incomplete.
- **Confirmed Gap:** inspected logic or asset concretely lacks the named behavior; not inferred from a stale TODO.
- **Unverified:** insufficient current evidence; neither a confirmed defect nor an implementation/compliance claim.

Classification applies to the named behavior, not the entire subsystem. Historical assessments remain dated records. Their baseline descriptions do not override newer implementation evidence. Original baseline content is recoverable at [the audited commit](https://github.com/aurelianware/cloudhealthoffice/tree/d5af561d490f1c440c7cd7fb1098fa3e18d8521c); current-status annotations below identify corrections. The original reconciliation changed documentation only. Subsequent implementation changes are explicitly recorded in dated follow-ups below; they do not establish production deployment or conformance. Messaging source documents are corrected; published/runtime copy must be refreshed and reviewed separately before the marketing launch.

## Traceable capability matrix

All evidence paths are repository-relative; method names follow a colon. Test-run IDs identify the exact executed project and filter below. Test-run IDs resolve to exact project paths and commands below. The date/commit column identifies each evidence baseline; the final consistency review did not rerun or broaden the earlier capability tests. This is the single authoritative ledger for reconciled capabilities; other documents provide context or approved wording.

| ID | Status | Verified date / source commit | Historical claim and original assessment | Current implementation | Supporting tests / execution | Verified behavior and remaining limits | Related documentation updates |
| --- | --- | --- | --- | --- | --- | --- | --- |
| CAP-01 | Implemented | 2026-10-19 / `d5af561d` | **PAS durable submission / D-01,D-02,D-03** — docs/epa-gap-matrix.md §1, §2.1, §3 D-01–03; docs/compliance/CMS-0057-F-READINESS-MATRIX.md PAS row | src/services/fhir-service/Controllers/PasController.cs:RecordAndRespondAsync; src/services/fhir-service/Services/Pas/PasAuthorizationRecorder.cs; src/services/authorization-service/Controllers/AutomatedDecisionsController.cs | tests/CloudHealthOffice.FhirService.Tests/Integration/PasSubmitPersistenceSeamTests.cs; tests/CloudHealthOffice.FhirService.Tests/Controllers/PasSubmitRecordingTests.cs (F1) | Stores mapped request and decision before answering; persistence failure produces OperationOutcome without preAuthRef; inquire round-trips the stored outcome. Full PAS profile validation and production readiness are separate. | docs/compliance/CMS-0057-F-READINESS-MATRIX.md; docs/epa-gap-matrix.md; docs/architecture/prior-authorization.md |
| CAP-02 | Implemented | 2026-10-19 / `d5af561d` | **PAS service-line outcomes / D-06** — docs/epa-gap-matrix.md §1, §2.1, §4.1 reviewAction row; readiness matrix denial row | src/services/authorization-service/Backends/AuthorizationDecisionPolicy.cs:Prepare/Apply; src/services/authorization-service/Controllers/AuthorizationsController.cs; src/services/fhir-service/Services/PasResponseBuilder.cs:AddRequestedServices | tests/CloudHealthOffice.AuthorizationService.Tests/AuthorizationDecisionIntegrityTests.cs (A2); src/services/authorization-service/AuthorizationService.Tests/Persistence/AuthorizationRepositoryMongoConcurrencyTests.cs:MixedDecision_SurvivesMongoReloadWithCodedLineReasonsAndHistory (A1); tests/CloudHealthOffice.FhirService.Tests/Services/PasResponseBuilderTests.cs (F1) | Stable line numbers, quantities, A1/A2/A3/A4 outcomes and coded/human-readable adverse reasons are validated before mutation and survive Mongo reload. Item projection is implemented; complete STU2 semantics and X12 reason-code conformance are not established. Repeated procedure codes require an explicit authorization line number; automatic claim-to-auth line matching and unit consumption remain separate. | docs/compliance/CMS-0057-F-READINESS-MATRIX.md; docs/epa-gap-matrix.md |
| CAP-03 | Implemented | 2026-10-19 / `d5af561d` | **CRD uncertainty / first part of D-21** — docs/epa-gap-matrix.md §1; readiness matrix CRD row | src/services/fhir-service/Services/CrdService.cs:BuildRequirementsUnknownCard; src/services/fhir-service/Controllers/CrdController.cs exception handling | tests/CloudHealthOffice.FhirService.Tests/Services/CrdFailClosedTests.cs (F1) | Unknown/uncoded input and failed/timed-out evaluation return uncertainty rather than no-PA-needed. An explicit classification can permit no-PA-needed. This is not full CRD conformance; hardcoded context and structured coverage actions are separate limitations. | docs/compliance/CMS-0057-F-READINESS-MATRIX.md; docs/epa-gap-matrix.md |
| CAP-04 | Implemented (durability) | 2026-10-19 / DTR durability follow-up; base `ec62f453` | **Durable DTR / D-22,G-53** — docs/epa-gap-matrix.md §1, §4.3; readiness matrix DTR row | src/services/fhir-service/Program.cs IDtrResourceStore binding; src/services/fhir-service/Services/MongoDtrResourceStore.cs; src/services/fhir-service/Services/DtrService.cs:GetQuestionnaireAsync/CreateQuestionnaireAsync/UpdateQuestionnaireAsync/SearchQuestionnairesAsync | tests/CloudHealthOffice.FhirService.Tests/Services/MongoDtrResourceStoreTests.cs; DTR controller/service/security tests (D1 follow-up below) | Questionnaire creates, conditional updates, id reads, existence checks and questionnaire/response searches use Mongo, including legacy payload-only documents. No per-pod resource index. Tenant overrides are resolved before filters; searches sort ids before paging. Real-Mongo tests cover cross-instance visibility, legacy documents and competing writes. Per-request filtering materializes tenant-scoped candidates; indexed server-side pagination, full answer validation, canonical resolution and complete packages remain separate. | docs/compliance/CMS-0057-F-READINESS-MATRIX.md; docs/epa-gap-matrix.md; src/site/MESSAGE_SHEET.md; src/site/assistant/knowledge.md |
| CAP-05 | Partially Implemented | 2026-10-19 / `d5af561d` | **SMART launch context and approval revocation** — docs/epa-gap-matrix.md §1, §4.6; readiness matrix SMART row; docs/architecture/smart-oauth-trust.md introduction | src/services/smart-auth-service/Controllers/LaunchContextController.cs; src/services/smart-auth-service/Services/MongoLaunchContextStore.cs; src/services/smart-auth-service/Services/SmartTokenContextResolver.cs; src/services/smart-auth-service/Services/SmartAppApprovals.cs; src/services/smart-auth-service/Controllers/AccountController.cs | tests/CloudHealthOffice.SmartAuth.Tests/LaunchContextStoreTests.cs; tests/CloudHealthOffice.SmartAuth.Tests/SmartLaunchFlowTests.cs; tests/CloudHealthOffice.SmartAuth.Tests/SmartConsentRevocationTests.cs; tests/CloudHealthOffice.SmartAuth.Tests/SmartAuthToFhirEndToEndTests.cs (S1) | Tenant/client-bound launch consumption, token patient/encounter context and withdrawal of approval affecting refresh are exercised. Do not equate this with every SMART response field, revocation/introspection mechanism, v2 scope or asymmetric-auth requirement. Deployment-specific production validation is unverified. | docs/architecture/smart-oauth-trust.md; docs/compliance/CMS-0057-F-READINESS-MATRIX.md; docs/epa-gap-matrix.md |
| CAP-06 | Partially Implemented | 2026-10-19 / `d5af561d` | **CMS reporting / D-27,G-20** — docs/epa-gap-matrix.md §1, §4.4; readiness matrix public metrics row | src/services/authorization-service/Services/Cms0057/Cms0057Reporting.cs:ComputeMetrics/Group; src/services/authorization-service/Controllers/AuthorizationsController.cs | src/services/authorization-service/AuthorizationService.Tests/Services/Cms0057ReportingTests.cs (A1) | Computes standard/expedited counts, rates, mean/median and timeliness; NotYetTracked explicitly lists approved-after-appeal and extended-then-approved. Does not establish complete annual public reporting. Legacy repository summaries are not proof that the new calculator is missing. | docs/compliance/CMS-0057-F-READINESS-MATRIX.md; docs/epa-gap-matrix.md |
| CAP-07 | Implemented | 2026-10-19 / `d5af561d` | **Receipt-based deadlines / D-08,D-09,G-18** — readiness matrix decision timeline row; docs/epa-gap-matrix.md D-08 tail, §4.4 timer row | src/services/authorization-service/Services/AuthorizationDeadlinePolicy.cs; src/services/authorization-service/Services/SlaWatchdogService.cs; src/services/authorization-service/Repositories/AuthorizationRepositoryMongo.cs:EnsureDecisionDeadlineAsync; Consumers/RfaiDocsReceivedConsumer.cs; src/services/fhir-service/Services/Pas/PasAuthorizationMapper.cs | src/services/authorization-service/AuthorizationService.Tests/Services/AuthorizationDeadlinePolicyTests.cs; src/services/authorization-service/AuthorizationService.Tests/Persistence/AuthorizationRepositoryMongoConcurrencyTests.cs:LegacyDeadlineInitialization_IsTenantAndRevisionFenced_AndOneConcurrentWriterWins; src/services/authorization-service/AuthorizationService.Tests/Persistence/SlaWatchdogRealStoreTests.cs (A1); F1 receipt seam tests | CHO receipt-v1 persists UTC receipt/due timestamps and urgent/standard targets; RFAI does not restart the clock; worker uses the persisted deadline. Program-specific extensions, tighter policies, notification and per-tenant-database worker support remain separate. | docs/compliance/CMS-0057-F-READINESS-MATRIX.md; docs/epa-gap-matrix.md |
| CAP-08 | Implemented | 2026-10-19 / `d5af561d` | **PAS tenant authority / D-12** — docs/epa-gap-matrix.md §1 tenant summary (D-12 itself already says fixed) | src/services/fhir-service/Controllers/PasController.cs; src/services/fhir-service/Services/PasAutoAdjudicator.cs authenticated tenant argument | tests/CloudHealthOffice.FhirService.Tests/Security/PasRuleSetTenantTests.cs (F2) | Token-derived tenant is used; body tags are not authority. An unbuilt reference copy is not a deployed production path. | docs/epa-gap-matrix.md |
| CAP-09 | Implemented | 2026-10-19 / `d5af561d`; durable-store follow-up based on `ec62f453` | **DTR search isolation / D-14** — docs/epa-gap-matrix.md §1 tenant summary | src/services/fhir-service/Services/MongoDtrResourceStore.cs:ListVisibleQuestionnairesAsync; DtrService.cs:EnsureNotDefaultLibrary; src/services/fhir-service/Controllers/DtrController.cs | tests/CloudHealthOffice.FhirService.Tests/Security/DtrQuestionnaireTenantIsolationTests.cs; tests/CloudHealthOffice.FhirService.Tests/Security/DtrQuestionnaireWriteAccessTests.cs (F1) | Own tenant plus un-overridden read-only defaults are visible; writes to reserved default library refused. Durable search across instances is covered by CAP-04. | docs/epa-gap-matrix.md |
| CAP-10 | Partially Implemented | 2026-10-19 / `d5af561d` | **Payer-to-payer client/payer binding / D-13** — docs/epa-gap-matrix.md §1 tenant summary and §3 D-13 | src/services/fhir-service/Controllers/FhirControllerBase.cs:TryBindReceivingPayer; src/services/fhir-service/Services/Identity/CallerIdentityResolver.cs; src/services/smart-auth-service/Controllers/Admin/SmartClientRegistrationsController.cs | tests/CloudHealthOffice.FhirService.Tests/Security/PayerToPayerReceivingPayerBindingTests.cs (F2); tests/CloudHealthOffice.SmartAuth.Tests/SmartAuthToFhirEndToEndTests.cs (S1) | System-context registered-payer binding and mismatched-body rejection implemented. This does not prove receiving-payer-specific consent or recipient-scoped member-match. | docs/epa-gap-matrix.md; docs/compliance/CMS-0057-F-READINESS-MATRIX.md |
| CAP-11 | Implemented | 2026-10-19 / `d5af561d` | **SMART scp handling / D-32** — docs/epa-gap-matrix.md §3 D-32 | src/services/fhir-service/Services/Identity/SmartTokenValidation.cs; src/services/fhir-service/Services/Identity/CallerIdentityResolver.cs; src/services/fhir-service/Services/Identity/SmartScopeClaims.cs | tests/CloudHealthOffice.FhirService.Tests/Security/SmartScpClaimTests.cs (F2) | Exercises scope/scp handling through HTTP; no inference of all issuer integrations. | docs/epa-gap-matrix.md |
| CAP-12 | Implemented | 2026-10-19 / `d5af561d` | **Authorization uniqueness and tenant/status index / D-39,D-37** — docs/epa-gap-matrix.md §3 D-37,D-39 (fixed rows) and stale D-01 tail | src/services/authorization-service/Repositories/AuthorizationIndexes.cs; src/services/authorization-service/Repositories/AuthorizationRepositoryMongo.cs; src/services/authorization-service/Controllers/AutomatedDecisionsController.cs | src/services/authorization-service/AuthorizationService.Tests/Persistence/AuthorizationRepositoryMongoConcurrencyTests.cs (A1) | Real-Mongo tests cover concurrent insert, cross-tenant number reuse, index creation and legacy duplicate startup failure. No deletion/renumbering implied; current supported Mongo backend only. | docs/epa-gap-matrix.md; docs/architecture/prior-authorization.md |
| CAP-13 | Implemented | 2026-10-19 / `d5af561d` | **Claims PA lookup fails open / D-04** — docs/epa-gap-matrix.md D-04 historical finding | src/services/claims-service/Services/Adjudication/Stages/BenefitCalculationStage.cs; src/services/claims-service/Services/Resolution/HttpAuthorizationValidationClient.cs | tests/CloudHealthOffice.ClaimsService.Tests/Services/Adjudication/Stages/BenefitCalculationStagePriorAuthLookupTests.cs; tests/CloudHealthOffice.ClaimsService.Tests/Security/PriorAuthLookupCarriesServiceTokenTests.cs (C1) | Unavailable lookup pends before benefit engine; definitive invalid/not-found denies; PA lookup carries service identity. Does not prove every claim requires PA or fix unrelated coverage/plan token propagation. | docs/epa-gap-matrix.md |
| CAP-14 | Confirmed Gap | 2026-10-19 / `d5af561d` | **Bulk export scaffold / D-28** — docs/epa-gap-matrix.md G-34; readiness matrix bulk row | src/services/fhir-service/Services/BulkExportService.cs:InitiateExportAsync | tests/CloudHealthOffice.FhirService.Tests/Services/BulkExportServiceTests.cs (F1) | Current implementation immediately completes zero-count jobs and manufactures output URLs without performing export. Passing scaffold tests are not a real export. | docs/compliance/CMS-0057-F-READINESS-MATRIX.md; src/site/MESSAGE_SHEET.md; src/site/assistant/knowledge.md |
| CAP-15 | Confirmed Gap | 2026-10-19 / `d5af561d` | **DTR complete package/prepopulation** — docs/epa-gap-matrix.md G-54,G-55 | src/services/fhir-service/Services/DtrService.cs:GetQuestionnairePackageAsync | Code inspected; package behavior not independently exercised by this focused run | Returns Questionnaire-only collection and does not use patientId for prepopulation. Complete package semantics and clinical logic are not implemented by this method. | docs/epa-gap-matrix.md; docs/compliance/CMS-0057-F-READINESS-MATRIX.md |
| CAP-16 | Confirmed Gap | 2026-10-19 / `d5af561d` | **CRD classification persistence portion of D-22** — docs/epa-gap-matrix.md D-22 | src/services/fhir-service/Services/CrdClassificationStore.cs | Code inspected; no restart integration test executed | Tenant classifications use expiring IMemoryCache; durable DTR creation does not resolve CRD classification persistence. | docs/epa-gap-matrix.md |
| CAP-17 | Confirmed Gap | 2026-10-19 / `d5af561d` | **QNXT authorization adapter** — docs/compliance/CMS0057-ACCEPTANCE-INVENTORY.md Augment boundary | src/services/authorization-service/Backends/QnxtAuthorizationBackend.cs | Methods inspected; no live QNXT test or credentials used | Production methods throw NotImplementedException. CHO-native persistence is not evidence of a live external-core adapter. | docs/compliance/CMS0057-ACCEPTANCE-INVENTORY.md (historical note); src/site/MESSAGE_SHEET.md |
| CAP-18 | Confirmed Gap | 2026-10-19 / `d5af561d` | **Trained fraud weights** — src/ml/README.md | src/ml/claim-fraud-v1.pt | Asset inspected; no trained-model evaluation executed | Named asset is a text placeholder. Separate rejection-suggestion/advisory-examiner implementations still exist. | src/site/MESSAGE_SHEET.md; src/site/assistant/knowledge.md |
| CAP-19 | Unverified | 2026-10-19 / `d5af561d` | **Full standards conformance and production readiness** — Historical complete/production-ready labels in docs/features/FHIR-IMPLEMENTATION-SUMMARY.md and roadmap/release/marketing assessments | Focused implementation review is not full IG validation, certified interoperability, deployment diligence or payer acceptance | No complete conformance/production campaign executed | Neither confirmed absence nor completed certification inferred. Requires specification/version, workflow, environment and dated results. | docs/POSITIONING.md; docs/guides/FEATURES.md; src/site/MESSAGE_SHEET.md; src/site/assistant/knowledge.md |
| CAP-20 | Confirmed Gap | 2026-10-19 / `d5af561d` | **FFS build/container coverage** — docs/deployment/KNOWN-GAPS.md | src/services/ffs-service/ directory; .github/workflows/deploy-azure-aks.yml; .github/workflows/docker-build.yml | Read-only inventory; no build/deployment performed | Service directory still lacks Dockerfile and named workflow entries. Keep as a build gap, distinct from fee engine functionality. | docs/deployment/KNOWN-GAPS.md |
| CAP-21 | Implemented | 2026-10-19 / `d5af561d` | **Authorization updates are last-write-wins** — docs/architecture/prior-authorization.md Limitations | src/services/authorization-service/Repositories/AuthorizationRepositoryMongo.cs:RevisionIs/UpdateAsync; src/services/authorization-service/Exceptions/AuthorizationConcurrencyException.cs | src/services/authorization-service/AuthorizationService.Tests/Persistence/AuthorizationRepositoryMongoConcurrencyTests.cs:TwoConcurrentUpdates_FromTheSameRead_ExactlyOneWins_TheOtherConflicts; AReplaceBasedOnAReadBeforeAnSlaEscalation_Conflicts_InsteadOfUndoingIt (A1) | Real Mongo conditional writes use the read Revision; exactly one concurrent writer succeeds and stale updates cannot undo escalation. Scoped to the current Mongo repository; not a claim of distributed transactions across services. | docs/architecture/prior-authorization.md |
| CAP-22 | Partially Implemented | 2026-10-20 / follow-up based on `a17c9f3c` | **Default FHIR reads are all live payer-backed** — readiness matrix adapter warnings / ePA G-30 | src/services/fhir-service/Program.cs IFhirDataAdapter registration (RoutingFhirDataAdapter over FhirDataBindings); src/services/fhir-service/Services/ChoNative/ChoNativeFhirDataAdapter.cs; IPatientAccessDataProvider registration | CAP-27 tests below; host registration inspected for IPatientAccessDataProvider | Patient and Coverage can be served from CHO's own member-service and coverage-service (CAP-27); the fhir-service Kubernetes manifest binds both, while the code default stays the mock. Encounter and Claim, and the Patient Access data provider / member directory behind Payer-to-Payer, still use mock data (MockFhirDataAdapter, MockPatientAccessDataProvider). No external payer core is integrated. | docs/adr/018-fhir-patient-coverage-from-cho-services.md; docs/diligence/ADAPTER-STATUS.md |
| CAP-23 | Implemented | 2026-10-19 / `3b5a2035` | **No authorization state machine, audited cancellation or expiry / D-23,G-24** — docs/epa-gap-matrix.md historical defect/register | src/services/authorization-service/Backends/AuthorizationLifecyclePolicy.cs; src/services/authorization-service/Backends/ChoAuthorizationBackend.cs; src/services/authorization-service/Controllers/AuthorizationsController.cs; src/services/authorization-service/Services/AuthorizationExpiryWorker.cs; src/services/authorization-service/Repositories/AuthorizationRepositoryMongo.cs | tests/CloudHealthOffice.AuthorizationService.Tests/AuthorizationLifecycleTests.cs; tests/CloudHealthOffice.AuthorizationService.Tests/UnusedAuthorizationsTests.cs; src/services/authorization-service/AuthorizationService.Tests/Persistence/AuthorizationRepositoryMongoConcurrencyTests.cs; src/services/authorization-service/AuthorizationService.Tests/Services/Cms0057ReportingTests.cs (refresh checks below) | Ordinary transition guards, audited cancellation and atomic tenant/revision-checked expiry are implemented. Expiry preserves clinical reporting and explicitly covered historical service dates. Per-tenant-database sweeps (D-38), appeals/overturn/reopening workflows and deployment readiness remain separate. | docs/epa-gap-matrix.md; src/site/MESSAGE_SHEET.md; src/site/assistant/knowledge.md |
| CAP-24 | Implemented (pipeline identity) | 2026-10-19 / follow-up based on `a8065f11` | **Examiner coverage/plan lookup identity / D-36,D-40** | src/services/claims-service/Services/Resolution/HttpCoverageClient.cs; HttpCoverageResolver.cs; HttpBenefitPlanResolver.cs | tests/CloudHealthOffice.ClaimsService.Tests/Security/AdjudicationLookupsCarryServiceIdentityTests.cs; src/services/coverage-service/CoverageService.Tests/Security/CoveragePipelineAuthTests.cs; tests/CloudHealthOffice.BenefitPlanService.Tests/Security/ClaimsPlanReadAuthenticationTests.cs | The three internal reads use the existing tenant-guarded service identity through the real outbound handler, including approval preparation and COB. Receiving API tests verify acceptance and tenancy; missing/foreign tenants, untrusted hosts and issuer failures remain restricted. User roles and global authentication policies are unchanged. Deployment identity validation and G-64 enablement remain separate. | docs/operations/CLAIMS-ADJUDICATION-SERVICE-IDENTITY.md; docs/epa-gap-matrix.md |
| CAP-25 | Partial | 2026-10-20 / unit-identity follow-up based on `2cf1f13e` | **Claim → authorization line matching and unit identity / G-64** | claims-service ClaimLine, AdapterClaimLine, BenefitCalculationStage, HttpAuthorizationValidationClient; authorization-service validate endpoint; PAS mapper/recorder and automated-decision persistence | BenefitCalculationStagePriorAuthLookupTests; HttpAuthorizationValidationClientTests; AuthorizationsControllerExtendedTests; PasAuthorizationMapperTests; PasSubmitPersistenceSeamTests; HttpPasAuthorizationRecorderTests; X12837PriorAuthorizationTests | Native explicit authorization line references survive adapter conversion and are sent separately from claim ordinals. Repeated-procedure ambiguity pends instead of denying; cache keys distinguish authorization lines. Service unit codes and PAS quantity code/system now survive native/persistence/validation boundaries; the recorder refuses dropped coding. No unit conversion or consumption ledger is implemented. Member binding is implemented in CAP-26; authorized-unit accounting, EDI enrichment and deployment validation remain open. Broader PA enforcement stays disabled. | docs/operations/CLAIMS-AUTHORIZATION-LINE-MATCHING.md; docs/operations/AUTHORIZATION-UNIT-ACCOUNTING.md; docs/epa-gap-matrix.md |
| CAP-26 | Implemented (native claims member comparison) | 2026-10-20 / follow-up based on `634e8046` | **Claim authorization belongs to the individual member / G-64 prerequisite** | authorization-service validate endpoint; claims HttpAuthorizationValidationClient, BenefitCalculationStage, X12837ClaimMapper | AuthorizationsControllerExtendedTests; AuthorizationAuthenticationTests; HttpAuthorizationValidationClientTests; BenefitCalculationStagePriorAuthLookupTests; X12837ClaimMapperTests; ClaimSubmissionServiceTests; PriorAuthLookupCarriesServiceTokenTests | Claims requires an echoed requested member and explicit comparison; mismatches deny, unconfirmed/missing identity pends. Separate-patient EDI no longer substitutes the subscriber; missing individual IDs fail intake. Legacy PA-required claims explicitly reusing a subscriber for a dependent pend. Direct legacy validate calls without a member remain unbound; historical identity reconciliation and production rollout are not completed. G-64 stays disabled. | docs/operations/CLAIMS-AUTHORIZATION-MEMBER-BINDING.md |
| CAP-27 | Implemented (Patient/Coverage CHO-native binding) | 2026-10-20 / follow-up based on `a17c9f3c` | **FHIR Patient and Coverage read only synthetic data** — CAP-22; ePA G-30; adapter-status Patient/Coverage rows | src/services/fhir-service/Services/ChoNative/ (ChoNativeFhirDataAdapter, RoutingFhirDataAdapter, FhirDataBindings, FhirSearchTranslation, FhirAdapterExceptionFilter); src/services/member-service/Controllers/MembersController.cs:SearchFhirPatients/GetFhirPatient; src/services/member-service/Repositories/MemberFhirSearchRepository.cs; src/services/coverage-service/Services/FhirCoverageProjector.cs; src/services/coverage-service/Repositories/CoverageFhirSearchRepository.cs; src/services/coverage-service/Controllers/CoverageController.cs:GetFhirCoverage/SearchFhirCoverage | tests/CloudHealthOffice.FhirService.Tests/ChoNative/*; tests/CloudHealthOffice.FhirService.Tests/Services/FhirAdapterStatusServiceTests.cs; src/services/member-service/MemberService.Tests/Repositories/MemberFhirSearchRepositoryTests.cs (real Mongo); src/services/member-service/MemberService.Tests/Integration/MemberFhirPatientSearchPipelineTests.cs; src/services/coverage-service/CoverageService.Tests/Repositories/CoverageFhirSearchRepositoryTests.cs (real Mongo); src/services/coverage-service/CoverageService.Tests/Security/CoverageFhirPipelineTests.cs; src/services/coverage-service/CoverageService.Tests/Services/FhirCoverageProjectorTests.cs | With `FhirAdapters:Sources:Patient` or `:Coverage` set to `ChoNative`, reads and searches come from the owning service's projection; Patient.id is the member id; SMART patient binding, tenant from token and service identity for SMART callers are unchanged; unsupported search input is FHIR 400 and upstream failure FHIR 502; adapter-status reports the binding the router uses. Not covered: Encounter/Claim, Payer-to-Payer member source and member-match, Coverage subscriber/relationship/dependent, Patient search generalPractitioner, accent-insensitive name matching, coverage-service record-access audit, deployment validation. | docs/adr/018-fhir-patient-coverage-from-cho-services.md; docs/diligence/ADAPTER-STATUS.md |
| CAP-28 | Implemented (plan edit service, contract v1) | 2026-10-20 / follow-up based on `abb3dac0` | **A plan cannot add its own claim edits without changing CHO's code** — extension model, ADR 019 | src/services/claims-service/Services/Adjudication/Stages/PlanEditsStage.cs; src/services/claims-service/Services/Adjudication/PlanEdits/ (PlanEditServiceClient, PlanEditServiceOptions, PlanEditRequestMapper, PlanEditServiceRegistration); src/services/shared/CloudHealthOffice.PlanEdits.Contracts/; claims-service ClaimEditCatalog, EditIdentity, EditOverridePolicy (PLAN_EDIT), EditDispositionApplier, PendDetails.PlanEditFindings | src/services/shared/CloudHealthOffice.PlanEdits.Contracts.Tests/*; tests/CloudHealthOffice.ClaimsService.Tests/Services/Adjudication/PlanEdits/* (stage, HTTP client through the real registration, options, decisions) | A tenant configured under `Adjudication:PlanEdits:Tenants` has each claim sent, signed, to its own edit service at pipeline Order 450; fired edits pend `PLANEDIT` as decidable edits (identity, override policy, fingerprint, approval re-run, upheld denial edits deny lines group CO); no usable answer pends `SVCUNAVAIL` or, by choice, passes; misconfiguration fails startup; no names, addresses or notes are sent. Not covered: automatic denial by a plan edit, per-rule override levels, non-CO group codes, dual-secret rotation, jurisdiction packs, event contracts/webhooks, deployment validation. | docs/adr/019-extension-model-plan-edit-service.md; docs/contracts/plan-edits/v1/README.md |
| CAP-29 | Implemented (local security campaign profiles) | 2026-10-20 / authorization baseline `ea9e137a`; claims/appeals and FHIR/SMART follow-ups | **Evidence-first security challenges and knowledge-base feed** | tools/SecurityChallenge/; .github/workflows/security-challenge.yml; ClaimStatusTransitionService revision refresh | 33 authorization; 47 claims/appeals; 42 FHIR/SMART scenarios; evidence-policy tests; two controlled FHIR mutation experiments | Actual Kestrel/Mongo profiles exercise scoped tenant isolation, privileges, member validation, claims/appeals lifecycle, two RSA/JWKS issuer trust boundaries, native Patient search/pagination and bulk-job ownership. FHIR sensitivity requires audience/scope mutants to fail specified cases with healthy controls; no production bypass switches. Checksummed evidence feeds review-only KB drafts. Existing-state claims/member fixtures are seeded directly; appeals/member use Development NoOp encryption. Production identity/encryption/ingress, financial accounting, full intake/adjudication, cross-service claim-link checks, finalized claim flows, Kafka, attachments, bulk file downloads and comprehensive mutation coverage remain unsupported. No automatic publication or compliance claim. | docs/security-challenge/README.md; docs/security-challenge/CLAIMS-APPEALS.md; docs/security-challenge/FHIR-SMART.md; SECURITY.md |
| CAP-30 | Implemented (Payer-to-Payer CHO-native binding) | 2026-10-20 / follow-up based on `ec494afe` | **Payer-to-Payer export and `$member-match` read only synthetic data** — ePA G-30, T-4; ADR 020 | src/services/fhir-service/Services/ChoNative/ChoNativeMemberDirectory.cs; src/services/fhir-service/Services/PayerToPayer/ChoNative/ChoNativePayerToPayerSources.cs; src/services/fhir-service/Services/PayerToPayer/PayerToPayerExportBuilder.cs (IPayerToPayerExportSource); src/services/fhir-service/Services/PayerToPayer/PayerToPayerMemberMatchSource.cs (MemberMatchCandidates); src/services/fhir-service/Services/PayerToPayer/PayerToPayerEobRedaction.cs; src/services/fhir-service/Models/FhirJsonResource.cs; src/services/member-service/Repositories/MemberFhirSearchRepository.cs (subscriber filter, graph-derived subscribers, any-form identifiers) | tests/CloudHealthOffice.FhirService.Tests/ChoNative/ChoNativePayerToPayerEndpointTests.cs; tests/CloudHealthOffice.FhirService.Tests/ChoNative/ChoNativePayerToPayerSourceTests.cs; src/services/member-service/MemberService.Tests/Repositories/MemberFhirSearchRepositoryTests.cs (real Mongo); src/services/member-service/MemberService.Tests/Integration/MemberFhirPatientSearchPipelineTests.cs | With `FhirAdapters:Sources:PayerToPayer=ChoNative`, the P2P-01 export sends member-service's Patient, coverage-service's Coverage and claims-service's in-window EOBs, each EOB reduced to an allowlist (who, when, services, diagnoses, coverage) so no amount, adjudication, CARC/RARC, payee, supporting information (including CHO's AI dispositions), extension or contained resource leaves; paging that changes mid-read, or more than 10,000 EOBs, answers 502; `$member-match` candidates are, with a member id, the members bearing it as their own id or their subscriber's in any case or separator spelling (family graph, legacy field as fallback; a member-service that does not echo the any-form mode is refused), otherwise everyone born on the supplied date, bounded at 500 per path (beyond it, or paging that changes mid-read: ambiguous); SSN-only requests answer insufficient criteria, and a supplied SSN that CHO cannot verify answers ambiguous; upstream failure is FHIR 502; adapter-status reports the binding. Not covered: PA data in the export, D-13, multi-tenant serving, bulk transfer, SSN index, an indexed normalized member id (any-form matching scans the tenant), deployment validation. | docs/adr/020-payer-to-payer-from-cho-services.md |
| CAP-31 | Implemented (Provider Access opt-out) | 2026-10-20 / follow-up based on `bbb1d137` | **Provider Access required an opt-in; CMS-0057-F shares unless the member opts out** — ePA D-11, G-09; ADR 021 | src/services/shared/CloudHealthOffice.Consent.Contracts/ConsentAuthorizationPolicy.cs (EvaluateOptOut, ConsentProvision); src/services/fhir-service/Services/Consent/ConsentEvaluator.cs; src/services/fhir-service/Services/PayerToPayer/HttpConsentRegistryConsentSource.cs; src/services/fhir-service/Services/ProviderAccess/ProviderAccessAuthorizationService.cs; src/services/consent-service/Models/Consent.cs (Provision) | tests/Cms0057Acceptance.Tests/Scenarios/ProviderAccessConsentTests.cs; tests/Cms0057Acceptance.Tests/Scenarios/PatientAccessClinicalTests.cs; tests/CloudHealthOffice.SmartAuth.Tests/SmartScopeEnforcementTests.cs; src/services/consent-service/ConsentService.Tests/Services/ConsentAuthorizationPolicyTests.cs; src/services/consent-service/ConsentService.Tests/Controllers/ConsentsControllerTests.cs | An attributed provider is served unless the member has an in-force opt-out (a Deny consent, revocable); a withdrawn opt-in is honoured as a refusal; an opt-out also refuses Payer-to-Payer whatever permits exist; an unreadable registry refuses every purpose. Not covered: member opt-out notices and portal screens, dynamic attribution, PA data. | docs/adr/021-provider-access-opt-out.md |

## Repository-wide discovery inventory

Searched Markdown throughout the repository (excluding Git, dependencies, build outputs), READMEs, capability/readiness/roadmap/assessment documents and messaging sources for gap/TODO/placeholder and broad maturity language. Also inspected relevant implementation TODO/NotImplementedException paths for the scoped capabilities. Search hits identify candidates, not confirmed defects. This bounded reconciliation does not certify every historical claim or every code TODO. Unreviewed assertions below remain **Unverified**, not silently resolved.

| Source | Candidate lines at audited commit | Disposition |
| --- | --- | --- |
| `.github/ISSUE_TEMPLATE/v4-availity-integration.md` | 11 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `.github/ISSUE_TEMPLATE/v5-exchange-aptc-csr.md` | 233, 240 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `.github/ISSUE_TEMPLATE/v5-exchange-qhp-ehb.md` | 516 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `.github/instructions/scripts.instructions.md` | 284 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `AUDIT/investor-readiness-2026-09.md` | 442, 516, 614 | Historical body preserved with one dated supersession note; canonical rows govern reconciled claims. |
| `CHANGELOG.md` | 1499, 1920, 2082, 2145, 2279 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/announcements/v3.0.0-announcement.md` | 178 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/architecture/BRANCHING-STRATEGY.md` | 33, 44, 57, 792 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/architecture/accumulator-service.md` | 100, 119 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/architecture/appeals-case-model.md` | 29 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/architecture/benefit-plan-adapter-pattern.md` | 69, 70, 71, 132, 134, 136, 138, 141, 142 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/architecture/benefits-viewer.md` | 31, 111 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/architecture/claim-adapter-pattern.md` | 96, 97, 98, 180 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/architecture/claim-adjudication-pipeline.md` | 436, 465 | Active wording corrected; canonical rows govern reconciled claims. |
| `docs/architecture/claim-submission-api.md` | 147 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/architecture/declarative-benefit-model.md` | 204 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/architecture/network-as-organization.md` | 173, 174 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/architecture/network-participation-backfill.md` | 3 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/architecture/pcp-assignment.md` | 114 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/architecture/plan-versioning.md` | 155 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/architecture/provider-adapter-pattern.md` | 56, 57, 76, 77, 78, 147, 149, 151, 153, 155, 166, 167 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/architecture/shared-cache.md` | 98 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/architecture/shared-messagebus.md` | 96 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/compliance/CMS-0057-F-DEMO-MODE-LIVE-ADAPTERS.md` | 85, 86 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/compliance/CMS-0057-F-READINESS-MATRIX.md` | 25, 35, 65, 67, 94, 95 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/compliance/CMS0057-ACCEPTANCE-INVENTORY.md` | 33, 36, 193, 194, 195, 196, 198 | Historical body preserved with one dated supersession note; canonical rows govern reconciled claims. |
| `docs/compliance/README.md` | 34 | Active wording corrected; canonical rows govern reconciled claims. |
| `docs/deployment/DEPLOYMENT-GATES-GUIDE.md` | 788 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/deployment/DEPLOYMENT-SECRETS-SETUP.md` | 102, 103, 444, 445, 566, 646, 686, 687 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/diligence/ASSESSMENT-CLAIMS-AUDIT.md` | 26 | Historical body preserved with one dated supersession note; canonical rows govern reconciled claims. |
| `docs/diligence/README.md` | 45, 46 | Active wording corrected; canonical rows govern reconciled claims. |
| `docs/epa-gap-matrix.md` | 22, 73, 74, 75, 78, 94, 103, 145, 169, 182, 264, 434 … | Active wording corrected; canonical rows govern reconciled claims. |
| `docs/features/276-277-IMPLEMENTATION-COMPLETE.md` | 3, 551 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/features/834-IMPLEMENTATION-SUMMARY.md` | 13, 20, 452 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/features/DEPLOYMENT-PIPELINE-FIX-SUMMARY.md` | 345 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/features/EDI-WORKFLOWS-COMPLETE.md` | 3 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/features/EDI-WORKFLOWS-DEPLOYMENT-SUMMARY.md` | 29, 227 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/features/FHIR-IMPLEMENTATION-SUMMARY.md` | 7, 14, 121, 201, 221, 320, 513 | Historical body preserved with one dated supersession note; canonical rows govern reconciled claims. |
| `docs/features/GATED-RELEASE-IMPLEMENTATION-SUMMARY.md` | 566 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/features/IMPLEMENTATION-SUMMARY.md` | 247, 315 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/features/MICROSERVICES-IMPLEMENTATION-STATUS.md` | 63, 64, 66, 67, 68, 69, 70, 71, 72, 75 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/features/MONITORING-IMPLEMENTATION-SUMMARY.md` | 116, 315, 335, 377 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/features/RELEASE-v4.0.0.md` | 48 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/features/ROADMAP.md` | 20 | Historical body preserved with one dated supersession note; canonical rows govern reconciled claims. |
| `docs/features/SAAS-LAUNCH-READINESS.md` | 11, 21 | Historical body preserved with one dated supersession note; canonical rows govern reconciled claims. |
| `docs/features/UPDATES-SUMMARY.md` | 64, 121 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/features/V4-LAUNCH-ROADMAP.md` | 9 | Historical body preserved with one dated supersession note; canonical rows govern reconciled claims. |
| `docs/features/VALUEADDS277-IMPLEMENTATION-COMPLETE.md` | 10, 336 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/features/WEBSITE-PHASE2-COMPLETE.md` | 10, 80, 95, 101, 102, 157 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/features/WEBSITE-UPDATES-FINAL.md` | 99 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/features/questionnaires/examples/qre-core-admin-adapter-example.md` | 6 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/guides/FEATURES.md` | 228, 304, 370, 489, 595, 697 | Active wording corrected; canonical rows govern reconciled claims. |
| `docs/implementation/PRIOR-AUTH-IMPLEMENTATION-SUMMARY.md` | 511 | Historical body preserved with one dated supersession note; canonical rows govern reconciled claims. |
| `docs/operations/NACHA-BANK-TRANSMISSION-RUNBOOK.md` | 308 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/security/argo-service-tokens.md` | 315 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/status/POSITIONING-AUDIT.md` | 30, 31, 33, 91, 97, 99, 107, 115, 160, 162, 168, 170 … | Historical body preserved with one dated supersession note; canonical rows govern reconciled claims. |
| `infrastructure/k8s/IMPLEMENTATION-SUMMARY.md` | 293 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `infrastructure/k8s/SFTP-WORKFLOWS-SUMMARY.md` | 198 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `scripts/tests/TROUBLESHOOTING.md` | 982 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `src/fhir/README.md` | 3, 54 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `src/services/eligibility-service/README.md` | 10 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |

### Historical register coverage

The following is an index of historical D/G identifiers, not a new finding list. Mapped rows inherit only the scope of the linked capability review. Other rows remain Unverified in this reconciliation; their historical status is not upgraded or erased. The original description, severity and proposed effort remain in the dated ePA record.

| Historical ID | Reconciliation evidence |
| --- | --- |
| D-01 | CAP-01 |
| D-02 | CAP-01 |
| D-03 | CAP-01 |
| D-04 | CAP-13 |
| D-05 | Unverified in this reconciliation; retain historical evidence pending focused review. |
| D-06 | CAP-02 |
| D-07 | CAP-07 |
| D-08 | CAP-07 |
| D-09 | CAP-07 |
| D-10 | Unverified in this reconciliation; retain historical evidence pending focused review. |
| D-11 | Unverified in this reconciliation; retain historical evidence pending focused review. |
| D-12 | CAP-08 |
| D-13 | CAP-10 |
| D-14 | CAP-09 |
| D-15 | Unverified in this reconciliation; retain historical evidence pending focused review. |
| D-16 | Unverified in this reconciliation; retain historical evidence pending focused review. |
| D-17 | Unverified in this reconciliation; retain historical evidence pending focused review. |
| D-18 | Unverified in this reconciliation; retain historical evidence pending focused review. |
| D-19 | Unverified in this reconciliation; retain historical evidence pending focused review. |
| D-20 | Unverified in this reconciliation; retain historical evidence pending focused review. |
| D-21 | CAP-03 (uncertainty only) |
| D-22 | CAP-04 / CAP-16 |
| D-23 | CAP-23; ordinary CHO lifecycle/expiry implemented by merged #1409. |
| D-24 | Unverified in this reconciliation; retain historical evidence pending focused review. |
| D-25 | Unverified in this reconciliation; retain historical evidence pending focused review. |
| D-26 | Unverified in this reconciliation; retain historical evidence pending focused review. |
| D-27 | CAP-06 (new calculator; legacy summary not asserted fixed) |
| D-28 | CAP-14 |
| D-29 | Unverified in this reconciliation; retain historical evidence pending focused review. |
| D-30 | Unverified in this reconciliation; retain historical evidence pending focused review. |
| D-31 | Unverified in this reconciliation; retain historical evidence pending focused review. |
| D-32 | CAP-11 |
| D-33 | Unverified in this reconciliation; retain historical evidence pending focused review. |
| D-34 | Unverified in this reconciliation; retain historical evidence pending focused review. |
| D-35 | Unverified in this reconciliation; retain historical evidence pending focused review. |
| D-36 | CAP-24: pipeline identity fixed; deployment validation and G-64 scope rollout remain separate. |
| D-37 | CAP-12 |
| D-38 | Unverified in this reconciliation; retain historical evidence pending focused review. |
| D-39 | CAP-12 |
| D-40 | CAP-24: pipeline identity fixed; deployment validation and G-64 scope rollout remain separate. |
| G-01 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-02 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-03 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-04 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-05 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-07 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-08 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-09 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-10 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-11 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-12 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-13 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-14 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-15 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-16 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-17 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-18 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-19 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-20 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-21 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-22 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-23 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-24 | CAP-23; ordinary lifecycle/expiry only; separate reopening/overturn and D-38 limits retained. |
| G-25 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-26 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-27 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-30 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-31 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-33 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-34 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-35 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-36 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-40 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-41 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-42 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-43 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-44 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-45 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-46 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-47 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-48 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-50 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-51 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-52 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-53 | CAP-04: durability implemented; full answer validation and canonical URL/version resolution remain partial. |
| G-54 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-55 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-56 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-57 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-58 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-59 | Unverified in this reconciliation; retain the historical assessment pending focused review. |
| G-60 | Unverified in this reconciliation; retain historical evidence pending focused review. |

## Executed checks — October 19, 2026

All focused test runs below executed against the fresh main-based checkout at the initial d5af561d baseline, not the earlier authorization-lifecycle branch. Each command used `dotnet test <project> -m:1 --filter '<filter>' --verbosity quiet`. Later FHIR security run also used `--no-restore`. Existing SDK/package caches were used; no source fixes or new tests were introduced. Tests are scoped behavioral evidence, not full regulatory or production validation.

| Run | Exact project | Filter (FullyQualifiedName contains each name, OR-separated) | Result |
| --- | --- | --- | --- |
| F1 | `tests/CloudHealthOffice.FhirService.Tests/CloudHealthOffice.FhirService.Tests.csproj` | `PasSubmitPersistenceSeamTests`, `PasSubmitRecordingTests`, `PasResponseBuilderTests`, `CrdFailClosedTests`, `MongoDtrResourceStoreTests`, `DtrQuestionnaireTenantIsolationTests`, `DtrQuestionnaireWriteAccessTests`, `BulkExportServiceTests` | 96 passed, 0 failed, 0 skipped |
| F2 | `tests/CloudHealthOffice.FhirService.Tests/CloudHealthOffice.FhirService.Tests.csproj` | `PasRuleSetTenantTests`, `PayerToPayerReceivingPayerBindingTests`, `SmartScpClaimTests` | 46 passed, 0 failed, 0 skipped |
| S1 | `tests/CloudHealthOffice.SmartAuth.Tests/CloudHealthOffice.SmartAuth.Tests.csproj` | `SmartConsentRevocationTests`, `SmartLaunchFlowTests`, `LaunchContextStoreTests`, `SmartAuthToFhirEndToEndTests` | 38 passed, 0 failed, 0 skipped |
| A1 | `src/services/authorization-service/AuthorizationService.Tests/AuthorizationService.Tests.csproj` | `AuthorizationDeadlinePolicyTests`, `Cms0057ReportingTests`, `SlaWatchdogRealStoreTests`, `AuthorizationRepositoryMongoConcurrencyTests` | 25 passed, 0 failed, 0 skipped |
| A2 | `tests/CloudHealthOffice.AuthorizationService.Tests/CloudHealthOffice.AuthorizationService.Tests.csproj` | `AuthorizationDecisionIntegrityTests`, `AutomatedDecisionEndpointTests` | 72 passed, 0 failed, 0 skipped |
| C1 | `tests/CloudHealthOffice.ClaimsService.Tests/CloudHealthOffice.ClaimsService.Tests.csproj` | `BenefitCalculationStagePriorAuthLookupTests`, `PriorAuthLookupCarriesServiceTokenTests` | 38 passed, 0 failed, 0 skipped |

Total: **315 passed, 0 failed, 0 skipped**. Example filter syntax: `FullyQualifiedName~PasSubmitPersistenceSeamTests|FullyQualifiedName~PasSubmitRecordingTests`. Full filter names above make every run reproducible. F1/S1 initially shared a build output and MSBuild retried a locked copy; both completed successfully. This was a build scheduling warning, not a product defect.

### Documentation and site validation

- Executed the three commands in `src/site/package.json`'s build chain: `node scripts/generate-mcc-publications.mjs`, `node build.mjs`, `node scripts/generate-sitemap.mjs`, from `src/site`. Built output produced; sitemap contains 90 URLs. The validation checkout is shallow, so non-strict sitemap generation warned and used fallback dates. No artifact was published; production strict/full-history policy is unchanged.
- Executed root `node src/site/js/validate-accessibility.js` (the `validate:site` script). It completed with 21 potential accessibility findings. No public HTML, CSS or validator changed, so those are pre-existing findings; this basic script is not comprehensive accessibility proof.
- Markdown lint passed for the new reconciliation and claim ledger using markdownlint-cli 0.45.0 with MD013 disabled for evidence tables. Added Markdown links, table shape, whitespace and docs-only scope were checked. Existing historical documents retain their original formatting rather than being mass-reformatted.

## Documentation correction map and release boundary

Current summaries corrected in `docs/epa-gap-matrix.md` and `docs/compliance/CMS-0057-F-READINESS-MATRIX.md`; scoped architecture corrections in `docs/architecture/prior-authorization.md` and `docs/architecture/smart-oauth-trust.md`. README, documentation indexes, capability inventory and positioning sources point to the dated evidence. Historical audits/roadmaps retain original evidence with dated current-status annotations. `src/site/MESSAGE_SHEET.md`, `src/site/assistant/knowledge.md` and the marketing ledger share current scoped wording.

The discovery inventory preserves other candidate statements as Unverified; it is not an assertion that every repository TODO or every historical claim has been independently settled. Unrechecked standards details and deployment claims require their own evidence before reuse. Public/runtime copy, including hardcoded `src/site/js/assistant.js` answers and existing HTML, is intentionally unchanged in this documentation PR; source-to-runtime alignment is a required subsequent marketing implementation check. Do not publish stronger claims or finalize the marketing implementation until that review is complete.

No application functionality, license, infrastructure, workflow or deployment settings were modified. No merge or deployment is authorized. Proposed production publication and broader marketing implementation remain separate approval steps.

## Main refresh after PR #1409 — October 19, 2026

Merged main `3b5a2035` into this documentation branch. The only textual conflict
was adjacent D-22/D-23 rows in `docs/epa-gap-matrix.md`: retained the precise DTR
partial implementation assessment and main's ordinary lifecycle/expiry resolution.
CAP-23 and the claim ledger now credit the merged lifecycle behavior. No application
code was edited during resolution; application changes are inherited from main.

Executed sequential focused checks with `dotnet test <project> -m:1 -p:UseAppHost=false --filter '<filter>' --verbosity quiet`:

| Project | FullyQualifiedName contains (OR-separated) | Result |
| --- | --- | --- |
| `src/services/authorization-service/AuthorizationService.Tests/AuthorizationService.Tests.csproj` | `AuthorizationRepositoryMongoConcurrencyTests`, `Cms0057ReportingTests` | 25 passed, 0 failed, 0 skipped |
| `tests/CloudHealthOffice.AuthorizationService.Tests/CloudHealthOffice.AuthorizationService.Tests.csproj` | `AuthorizationLifecycleTests`, `UnusedAuthorizationsTests` | 20 passed, 0 failed, 0 skipped |

These **45 refreshed checks** supplement the initial 315-test record; they are not
a rerun of all initial suites. Initial simultaneous builds collided on shared macOS
apphost output; the sequential checks above passed with apphost generation disabled
for the test invocation only. No project configuration was changed.

The site build completed again with 90 sitemap URLs and the existing shallow-history
non-strict warning. Markdown lint (MD013 disabled), evidence-table/local-link checks,
conflict-marker checks and whitespace validation passed. Relative to current main,
the PR still changes only Markdown. No merge into main or deployment was performed.

## Final consistency review — October 19, 2026

Integrated current main `1b38d780`. Its only change after the previously tested
`3b5a2035` base is the published Da Vinci interoperability JSON; the application
implementation and the 315 initial / 45 refreshed test records above retain their
stated scope. No capability classification was upgraded in this cleanup, and no
additional product test run is represented as completed.

### Active guidance and historical records

Every Markdown file changed by this PR was reviewed for its role, conflicting
statements, source links and scope. Active documents state current scoped behavior
and link here. Historical bodies retain their original assessment/recommendations,
with one short dated supersession note. The ePA document is a mixed record: its
component/requirement summaries are corrected for reconciled capabilities, while
original defect descriptions, public-claim findings and owner/effort records are
explicitly historical. Its §11 generator classifications remain separate scenario
scores, not whole-platform maturity verdicts.

| Document | Role | Treatment |
| --- | --- | --- |
| `AUDIT/investor-readiness-2026-09.md` | Historical assessment | One dated supersession note; original assessment retained; moved links repaired where needed. |
| `README.md` | Active documentation | Current scoped wording; canonical evidence reference; no duplicated status banner. |
| `docs/CAPABILITY_RECONCILIATION.md` | Active documentation | Single canonical ledger with per-row date/commit, evidence, tests, limits and corrected-document paths. |
| `docs/POSITIONING.md` | Active documentation | Current scoped wording; canonical evidence reference; no duplicated status banner. |
| `docs/README.md` | Active documentation | Current scoped wording; canonical evidence reference; no duplicated status banner. |
| `docs/architecture/README.md` | Active documentation | Current scoped wording; canonical evidence reference; no duplicated status banner. |
| `docs/architecture/claim-adjudication-pipeline.md` | Active documentation | Current scoped wording; canonical evidence reference; no duplicated status banner. |
| `docs/architecture/prior-authorization.md` | Active documentation | Current scoped wording; canonical evidence reference; no duplicated status banner. |
| `docs/architecture/smart-oauth-trust.md` | Active documentation | Current scoped wording; canonical evidence reference; no duplicated status banner. |
| `docs/compliance/CMS-0057-F-READINESS-MATRIX.md` | Active documentation | Current scoped wording; canonical evidence reference; no duplicated status banner. |
| `docs/compliance/CMS0057-ACCEPTANCE-INVENTORY.md` | Historical assessment | One dated supersession note; original assessment retained; moved links repaired where needed. |
| `docs/compliance/README.md` | Active documentation | Current scoped wording; canonical evidence reference; no duplicated status banner. |
| `docs/compliance/claims-cms-0057-f-readiness.md` | Historical assessment | One dated supersession note; original assessment retained; moved links repaired where needed. |
| `docs/deployment/KNOWN-GAPS.md` | Active documentation | Current scoped wording; canonical evidence reference; no duplicated status banner. |
| `docs/diligence/ASSESSMENT-CLAIMS-AUDIT.md` | Historical assessment | One dated supersession note; original assessment retained; moved links repaired where needed. |
| `docs/diligence/README.md` | Active documentation | Current scoped wording; canonical evidence reference; no duplicated status banner. |
| `docs/epa-gap-matrix.md` | Active tracking with explicit historical sections | Corrected active inquiry/tenant/repository/line/reporting summaries; dated original findings and plan remain identifiable. |
| `docs/features/FHIR-IMPLEMENTATION-SUMMARY.md` | Historical assessment | One dated supersession note; original assessment retained; moved links repaired where needed. |
| `docs/features/PRIOR-AUTHORIZATION-API.md` | Active guide to scoped library / legacy examples | Current scoped wording; canonical evidence reference; no duplicated status banner. |
| `docs/features/ROADMAP-2026.md` | Historical assessment | One dated supersession note; original assessment retained; moved links repaired where needed. |
| `docs/features/ROADMAP.md` | Historical assessment | One dated supersession note; original assessment retained; moved links repaired where needed. |
| `docs/features/SAAS-LAUNCH-READINESS.md` | Historical assessment | One dated supersession note; original assessment retained; moved links repaired where needed. |
| `docs/features/V4-LAUNCH-ROADMAP.md` | Historical assessment | One dated supersession note; original assessment retained; moved links repaired where needed. |
| `docs/guides/FEATURES.md` | Active documentation | Current scoped wording; canonical evidence reference; no duplicated status banner. |
| `docs/implementation/PRIOR-AUTH-IMPLEMENTATION-SUMMARY.md` | Historical assessment | One dated supersession note; original assessment retained; moved links repaired where needed. |
| `docs/marketing/CLAIM_EVIDENCE_LEDGER.md` | Active documentation | Marketing usage index only; independent copied status table removed. |
| `docs/status/POSITIONING-AUDIT.md` | Historical assessment | One dated supersession note; original assessment retained; moved links repaired where needed. |
| `src/site/MESSAGE_SHEET.md` | Active documentation | Current scoped wording; canonical evidence reference; no duplicated status banner. |
| `src/site/README.md` | Active documentation | Current scoped wording; canonical evidence reference; no duplicated status banner. |
| `src/site/assistant/knowledge.md` | Active documentation | Current scoped wording; canonical evidence reference; no duplicated status banner. |

### Contradictions removed

- `docs/architecture/prior-authorization.md`: retention's expected-status delete
  guard no longer claims that Authorization has no version field; ordinary
  revision-fenced writes and retention predicates are explained separately.
- `docs/epa-gap-matrix.md`: inquiry no longer says it cannot find PAS-submitted
  records; adjudicator tenant no longer comes from the body; the Mongo repository
  is no longer labeled untested; late-decision and standard-rate tracking are
  credited separately from missing complete public reporting. Historical test
  failures are tied to the original audit baseline, not current main.
- `docs/compliance/CMS-0057-F-READINESS-MATRIX.md`: statuses derive from this ledger;
  old claims-domain prose is not treated as current proof of a missing capability.
  Integration duties remain explicit without downgrading implemented PAS/CRD/DTR/
  SMART/deadline functionality.
- `docs/POSITIONING.md` and `docs/guides/FEATURES.md`: unsupported incumbent-cost,
  customer-reference and blanket production/conformance labels are removed or
  explicitly unverified. The feature catalogue retains code/configuration examples
  and technical links; an example is not a verified commercial onboarding path.
- README/indexes now link to one capability authority. The marketing usage index
  no longer duplicates the full status/limits matrix. Historical notes do not
  repeat the eight-capability boilerplate.

### Marketing/runtime follow-up (not implemented in this PR)

The messaging sheet and assistant knowledge agree on implemented capabilities,
precise remaining bulk-export/default-mock/DTR limits, commercial production
licensing and advisory AI scope. Advisory code exists at
`src/ai/edi277Resolution.ts` and
`src/services/claims-examiner-service/Services/Examiner/ExaminerOrchestrator.cs`;
these code paths are not evidence of independently validated autonomous decisions
or production outcomes. The placeholder fraud weights are a separate CAP-18 limit.

| Inspection-only runtime source | Required later copy review |
| --- | --- |
| `src/site/js/assistant.js`, TOPICS `what` | Replace the guaranteed mandate-outcome language with the source sheet's scoped integration wording. |
| `src/site/js/assistant.js`, TOPICS `deploy` | Replace the absolute “PHI stays inside your boundary” statement with deployment-specific data-flow qualification, including external integrations/AI. |
| `src/site/js/assistant.js`, TOPICS `stage` | Remove unverified first-tenant/customer-stage claims; distinguish evaluation evidence from production readiness. |
| `src/site/js/assistant.js`, licensing answer | Align explicitly with the source sentence that production requires a commercial license under BSL 1.1. |
| Existing public HTML | Align affected hero, capability and deployment copy with the reviewed source documents during the separately approved marketing implementation. |

JavaScript/HTML were inspected only; they are unchanged by this PR. Runtime copy
alignment remains a marketing-release follow-up, not a claim that the current
website already reflects these Markdown sources.

### Remaining limits

CAP-04 now establishes durable DTR update/search/existence behavior. CAP-15/16
retain package and CRD classification-durability limits; CAP-14 retains the zero-count bulk-export scaffold; CAP-17/18/20/22 retain
the external-core adapter, trained-model asset, build inventory and default-mock
limits. CAP-05/06/10 retain full SMART contract, public reporting and recipient
consent limits. CAP-19 and unreviewed historical identifiers remain **Unverified**.
No missing integration, standard conformance, production readiness, deployment,
customer, certification or savings outcome is inferred from code alone.

### Final validation record

- Marketing site build: all three commands in the existing site build chain
  completed; sitemap generated **90 URLs**. Shallow-history/non-strict lastmod
  warning remains expected for this local validation; nothing was published by
  these commands.
- `validate:site` completed with **21 existing potential accessibility findings**.
  Public HTML/CSS/validator files are identical to current main; this PR does not
  repair or newly validate their accessibility.
- Markdown lint: the canonical ledger and marketing index pass markdownlint-cli
  0.45.0 with MD013 disabled for evidence tables. Across all 30 modified Markdown
  files, comparison with current-main versions reports **871 findings versus
  872 on main**, with no per-file/rule count increases. Existing legacy formatting
  findings are retained rather than claiming a clean full-repository lint run.
- Local documentation links: **61 legacy relative targets repaired** in modified
  documents; target and fragment checks validated **363 local links** with no unresolved
  destinations or fragments in those files. Remote URLs were not availability-tested. Historical link labels
  and technical evidence were retained.
- All 23 canonical finding rows have the four allowed classifications, explicit
  verification date/source commit, existing implementation/test paths, limits and
  related documentation paths. The marketing index has no independent status table.
- Whitespace/conflict-marker checks and docs-only diff review against main passed.
  All non-Markdown changes inherited by integrating main match main exactly.

No new focused tests were needed for this wording/structure cleanup: no behavior
claim was upgraded beyond the previously recorded code and test evidence. Current
GitHub Actions results must be checked on the final pushed head; earlier successes
are not treated as results for a new commit. Merge remains subject to that CI/review.

## DTR durability follow-up (October 19, 2026)

The follow-up based on `ec62f453` supersedes CAP-04's historical update/search
limitation. The existing Mongo collections now supply updates, searches and
existence checks, including old documents without additional search metadata.
Read [DTR durability operations](operations/DTR-DURABILITY.md) for concurrency,
compatibility and search-scaling limits. G-53 and PAS-02 remain partial for
validation/canonical/package semantics; CRD's separate memory store is unchanged.

D1 validation on SDK 8.0.426: full FHIR suite 1,144 passed; SMART auth suite
262 passed; CMS acceptance suite 373 passed (zero failures/skips in these runs).
Commands: `dotnet test tests/CloudHealthOffice.FhirService.Tests/CloudHealthOffice.FhirService.Tests.csproj -m:1`,
`dotnet test tests/CloudHealthOffice.SmartAuth.Tests/CloudHealthOffice.SmartAuth.Tests.csproj -m:1`,
and `dotnet test tests/Cms0057Acceptance.Tests/Cms0057Acceptance.Tests.csproj -m:1`.
Earlier F1/F2/S1 totals above retain their original baseline and are not
retroactively attributed to this follow-up. Final CI results are recorded in
the implementation PR.

## Claims upstream identity follow-up (October 19, 2026)

CAP-24 supersedes the D-36/D-40 caller-token findings for the three claims
pipeline reads. See [the operations notes](operations/CLAIMS-ADJUDICATION-SERVICE-IDENTITY.md)
for unchanged failure semantics and deployment/rollout limits. G-64 is not enabled
by this change. On SDK 8.0.426 the full claims suite passed 1,452 tests (including
32 new handler/readjudicator cases); the coverage authentication suite passed
16 and the new plan authentication suite passed 4. Broader regression and CI
results are recorded in the implementation PR; earlier capability test totals
retain their original baselines.

## FHIR Patient and Coverage from CHO services (October 20, 2026)

CAP-27 implements [ADR 018](adr/018-fhir-patient-coverage-from-cho-services.md) and
narrows CAP-22. Behaviour changes beyond the new endpoints: member-service's FHIR
Patient projection now uses the member id as `Patient.id` (matching
`EOB.patient` and the SMART launch context) and hides draft members; the
adapter-status report can no longer label a mock-bound data resource Live.
An independent review before merge added: a 502 when a member-confined search
returns another member (defence against a data source ignoring its filter), a
Patient read checked against the bound member, 400 for search parameters the
server would not apply, PHI-free upstream logs, record-access audit on the
coverage-service FHIR endpoints, and a production-PHI data label in the
Kubernetes manifest (synthetic in the GKE demo render).

Validation on SDK 8.0.426 against branch `feat/fhir-native-patient-coverage`:
full solution Release build 0 warnings, 0 errors; fhir-service suite 1,229 passed
(85 new); member-service suite 209 passed (20 new); coverage-service suite 172
passed (27 new); SMART auth suite 262 passed; CMS acceptance suite 373 passed;
Da Vinci interop suite 148 passed, 3 skipped (pre-existing skips). No CMS-0057-F
§11 scenario score is changed by this record: PAT-01, PROV-01 and P2P-01 need a
focused rescoring (P2P-01 still reads the mock member source). Final CI results
are recorded in the implementation PR.

Rescoring after merge (October 20, 2026): the gap matrix §4.5 data-source row
moves from S to P and T-4 is narrowed. PAT-01, PROV-01 and P2P-01 stay GAP,
with updated justifications: PAT-01's product path is real (fhir-service
proxies ExplanationOfBenefit to claims-service's CARIN projection) but its
acceptance test still runs on the mock mapper; PROV-01 still lacks Provider Access PA data and its claims and
encounter data are still mock; P2P-01 still reads the mock member source and
D-13 remains open.

PAT-01 follow-up (October 20, 2026): PAT-01 moves GAP → PARTIAL. A
Replace-tagged acceptance test (`PatientAccessClaimsTests`) drives fhir-service's
ExplanationOfBenefit read and search over HTTP into claims-service's controller
and projector, and fhir-service now withholds (502) a patient-confined EOB
search whose results include another member. It stays short of PASSABLE because
the EOB projection is not CARIN Blue Button: no profile, required elements
missing, no CARIN BB validation run.
