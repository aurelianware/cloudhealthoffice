# Capability Reconciliation

**Verified:** October 19, 2026 (America/Phoenix)
**Implementation baseline:** remote `main` at `d5af561d490f1c440c7cd7fb1098fa3e18d8521c`.
**Working branch:** `docs/capability-reconciliation`, separate checkout; unrelated work preserved.

## Evidence policy and classification

Use the latest verified implementation as the source of truth. Do not repeat resolved historical gaps. Describe an implemented capability accurately even when full standards conformance or production readiness is unverified. Keep negative claims as narrow and evidence-based as positive claims.

- **Implemented:** reviewed executing path and supporting behavioral tests establish the named scoped behavior.
- **Partially Implemented:** meaningful behavior is verified but an identified part remains incomplete.
- **Confirmed Gap:** inspected logic or asset concretely lacks the named behavior; not inferred from a stale TODO.
- **Unverified:** insufficient current evidence; neither a confirmed defect nor an implementation/compliance claim.

Classification applies to the named behavior, not the entire subsystem. Historical assessments remain dated records. Their baseline descriptions do not override newer implementation evidence. Original baseline content is recoverable at [the audited commit](https://github.com/aurelianware/cloudhealthoffice/tree/d5af561d490f1c440c7cd7fb1098fa3e18d8521c); current-status annotations below identify corrections. No application, licensing, infrastructure, deployment configuration, public HTML or runtime assistant changes are included. Messaging source documents are corrected; published/runtime copy must be refreshed and reviewed separately before the marketing launch.

## Traceable capability matrix

All paths are repository-relative. In FHIR rows, shorthand `Controllers/`, `Services/`, `Security/` and `Integration/` test paths refer to `tests/CloudHealthOffice.FhirService.Tests/`; implementation shorthand after a full path stays within its named service. Test-run IDs resolve to exact project paths and commands below. All rows were reviewed on **2026-10-19** against the baseline above.

| ID | Status | Historical claim and original assessment | Current implementation | Supporting tests / execution | Verified behavior and remaining limits | Documentation correction |
| --- | --- | --- | --- | --- | --- | --- |
| CAP-01 | Implemented | **PAS durable submission / D-01,D-02,D-03** — docs/epa-gap-matrix.md §1, §2.1, §3 D-01–03; docs/compliance/CMS-0057-F-READINESS-MATRIX.md PAS row | src/services/fhir-service/Controllers/PasController.cs:RecordAndRespondAsync; src/services/fhir-service/Services/Pas/PasAuthorizationRecorder.cs; src/services/authorization-service/Controllers/AutomatedDecisionsController.cs | tests/CloudHealthOffice.FhirService.Tests/Integration/PasSubmitPersistenceSeamTests.cs; Controllers/PasSubmitRecordingTests.cs (F1) | Stores mapped request and decision before answering; persistence failure produces OperationOutcome without preAuthRef; inquire round-trips the stored outcome. Full PAS profile validation and production readiness are separate. | Correct current PAS summaries; retain dated defect descriptions. |
| CAP-02 | Implemented | **PAS service-line outcomes / D-06** — docs/epa-gap-matrix.md §1, §2.1, §4.1 reviewAction row; readiness matrix denial row | src/services/authorization-service/Backends/AuthorizationDecisionPolicy.cs:Prepare/Apply; Controllers/AuthorizationsController.cs; src/services/fhir-service/Services/PasResponseBuilder.cs:AddRequestedServices | tests/CloudHealthOffice.AuthorizationService.Tests/AuthorizationDecisionIntegrityTests.cs (A2); src/services/authorization-service/AuthorizationService.Tests/Persistence/AuthorizationRepositoryMongoConcurrencyTests.cs:MixedDecision_SurvivesMongoReloadWithCodedLineReasonsAndHistory (A1); FHIR Services/PasResponseBuilderTests.cs (F1) | Stable line numbers, quantities, A1/A2/A3/A4 outcomes and coded/human-readable adverse reasons are validated before mutation and survive Mongo reload. Item projection is implemented; complete STU2 semantics and X12 reason-code conformance are not established. | Credit line outcomes and denial validation; remove blanket missing-line language. |
| CAP-03 | Implemented | **CRD uncertainty / first part of D-21** — docs/epa-gap-matrix.md §1; readiness matrix CRD row | src/services/fhir-service/Services/CrdService.cs:BuildRequirementsUnknownCard; Controllers/CrdController.cs exception handling | tests/CloudHealthOffice.FhirService.Tests/Services/CrdFailClosedTests.cs (F1) | Unknown/uncoded input and failed/timed-out evaluation return uncertainty rather than no-PA-needed. An explicit classification can permit no-PA-needed. This is not full CRD conformance; hardcoded context and structured coverage actions are separate limitations. | Replace fail-open summary with precise uncertainty behavior. |
| CAP-04 | Partially Implemented | **Durable DTR / D-22,G-53** — docs/epa-gap-matrix.md §1, §4.3; readiness matrix DTR row | src/services/fhir-service/Program.cs IDtrResourceStore binding; Services/MongoDtrResourceStore.cs; Services/DtrService.cs:GetQuestionnaireAsync/CreateQuestionnaireAsync/UpdateQuestionnaireAsync/SearchQuestionnairesAsync | tests/CloudHealthOffice.FhirService.Tests/Services/MongoDtrResourceStoreTests.cs (F1) | Creates, response submission and id-based reads use Mongo; real-store tests prove reload across new service instances. UpdateQuestionnaireAsync does not call the durable store; search/existence/update indexes remain in-process. Do not claim all DTR CRUD is durable. | Replace wholly-in-memory wording, preserve update/search limitations. Correct the earlier marketing ledger overstatement. |
| CAP-05 | Partially Implemented | **SMART launch context and approval revocation** — docs/epa-gap-matrix.md §1, §4.6; readiness matrix SMART row; docs/architecture/smart-oauth-trust.md introduction | src/services/smart-auth-service/Controllers/LaunchContextController.cs; Services/MongoLaunchContextStore.cs; Services/SmartTokenContextResolver.cs; Services/SmartAppApprovals.cs; Controllers/AccountController.cs | tests/CloudHealthOffice.SmartAuth.Tests/LaunchContextStoreTests.cs; SmartLaunchFlowTests.cs; SmartConsentRevocationTests.cs; SmartAuthToFhirEndToEndTests.cs (S1) | Tenant/client-bound launch consumption, token patient/encounter context and withdrawal of approval affecting refresh are exercised. Do not equate this with every SMART response field, revocation/introspection mechanism, v2 scope or asymmetric-auth requirement. Deployment-specific production validation is unverified. | Credit launch/withdrawal; qualify scope rather than claiming no launch/revocation or universal readiness. |
| CAP-06 | Partially Implemented | **CMS reporting / D-27,G-20** — docs/epa-gap-matrix.md §1, §4.4; readiness matrix public metrics row | src/services/authorization-service/Services/Cms0057/Cms0057Reporting.cs:ComputeMetrics/Group; Controllers/AuthorizationsController.cs | src/services/authorization-service/AuthorizationService.Tests/Services/Cms0057ReportingTests.cs (A1) | Computes standard/expedited counts, rates, mean/median and timeliness; NotYetTracked explicitly lists approved-after-appeal and extended-then-approved. Does not establish complete annual public reporting. Legacy repository summaries are not proof that the new calculator is missing. | Replace missing-median/split language; retain exact untracked metrics and publication requirements. |
| CAP-07 | Implemented | **Receipt-based deadlines / D-08,D-09,G-18** — readiness matrix decision timeline row; docs/epa-gap-matrix.md D-08 tail, §4.4 timer row | src/services/authorization-service/Services/AuthorizationDeadlinePolicy.cs; Services/SlaWatchdogService.cs; Repositories/AuthorizationRepositoryMongo.cs:EnsureDecisionDeadlineAsync; Consumers/RfaiDocsReceivedConsumer.cs; FHIR Services/Pas/PasAuthorizationMapper.cs | src/services/authorization-service/AuthorizationService.Tests/Services/AuthorizationDeadlinePolicyTests.cs; Persistence/AuthorizationRepositoryMongoConcurrencyTests.cs:LegacyDeadlineInitialization_IsTenantAndRevisionFenced_AndOneConcurrentWriterWins; Persistence/SlaWatchdogRealStoreTests.cs (A1); F1 receipt seam tests | CHO receipt-v1 persists UTC receipt/due timestamps and urgent/standard targets; RFAI does not restart the clock; worker uses the persisted deadline. Program-specific extensions, tighter policies, notification and per-tenant-database worker support remain separate. | Remove nonfunctional-watchdog/dropped-urgency/read-only-deadline assertions. |
| CAP-08 | Implemented | **PAS tenant authority / D-12** — docs/epa-gap-matrix.md §1 tenant summary (D-12 itself already says fixed) | src/services/fhir-service/Controllers/PasController.cs; Services/PasAutoAdjudicator.cs authenticated tenant argument | tests/CloudHealthOffice.FhirService.Tests/Security/PasRuleSetTenantTests.cs (F2) | Token-derived tenant is used; body tags are not authority. An unbuilt reference copy is not a deployed production path. | Correct tenant summary, retain original defect as historical. |
| CAP-09 | Implemented | **DTR search isolation / D-14** — docs/epa-gap-matrix.md §1 tenant summary | src/services/fhir-service/Services/DtrService.cs:VisibleQuestionnaires/EnsureNotDefaultLibrary; Controllers/DtrController.cs | tests/CloudHealthOffice.FhirService.Tests/Security/DtrQuestionnaireTenantIsolationTests.cs; Security/DtrQuestionnaireWriteAccessTests.cs (F1) | Own tenant plus un-overridden read-only defaults are visible; writes to reserved default library refused. Search completeness across restarts remains CAP-04. | Remove cross-tenant-search defect from current summary, preserve durability limitation. |
| CAP-10 | Partially Implemented | **Payer-to-payer client/payer binding / D-13** — docs/epa-gap-matrix.md §1 tenant summary and §3 D-13 | src/services/fhir-service/Controllers/FhirControllerBase.cs:TryBindReceivingPayer; Services/Identity/CallerIdentityResolver.cs; src/services/smart-auth-service/Controllers/Admin/SmartClientRegistrationsController.cs | tests/CloudHealthOffice.FhirService.Tests/Security/PayerToPayerReceivingPayerBindingTests.cs (F2); tests/CloudHealthOffice.SmartAuth.Tests/SmartAuthToFhirEndToEndTests.cs (S1) | System-context registered-payer binding and mismatched-body rejection implemented. This does not prove receiving-payer-specific consent or recipient-scoped member-match. | Credit authenticated binding; keep consent limitation distinct. |
| CAP-11 | Implemented | **SMART scp handling / D-32** — docs/epa-gap-matrix.md §3 D-32 | src/services/fhir-service/Services/Identity/SmartTokenValidation.cs; CallerIdentityResolver.cs; SmartScopeClaims.cs | tests/CloudHealthOffice.FhirService.Tests/Security/SmartScpClaimTests.cs (F2) | Exercises scope/scp handling through HTTP; no inference of all issuer integrations. | Current annotation links tested resolution; retain historical mapping defect. |
| CAP-12 | Implemented | **Authorization uniqueness and tenant/status index / D-39,D-37** — docs/epa-gap-matrix.md §3 D-37,D-39 (fixed rows) and stale D-01 tail | src/services/authorization-service/Repositories/AuthorizationIndexes.cs; AuthorizationRepositoryMongo.cs; Controllers/AutomatedDecisionsController.cs | src/services/authorization-service/AuthorizationService.Tests/Persistence/AuthorizationRepositoryMongoConcurrencyTests.cs (A1) | Real-Mongo tests cover concurrent insert, cross-tenant number reuse, index creation and legacy duplicate startup failure. No deletion/renumbering implied; current supported Mongo backend only. | Mark old no-index/concurrent-duplicate statements superseded; preserve operational duplicate remediation requirement. |
| CAP-13 | Implemented | **Claims PA lookup fails open / D-04** — docs/epa-gap-matrix.md D-04 historical finding | src/services/claims-service/Services/Adjudication/Stages/BenefitCalculationStage.cs; Services/Resolution/HttpAuthorizationValidationClient.cs | tests/CloudHealthOffice.ClaimsService.Tests/Services/Adjudication/Stages/BenefitCalculationStagePriorAuthLookupTests.cs; Security/PriorAuthLookupCarriesServiceTokenTests.cs (C1) | Unavailable lookup pends before benefit engine; definitive invalid/not-found denies; PA lookup carries service identity. Does not prove every claim requires PA or fix unrelated coverage/plan token propagation. | Credit failure handling and service identity; keep floor/flag/EDI scope caveats. |
| CAP-14 | Confirmed Gap | **Bulk export scaffold / D-28** — docs/epa-gap-matrix.md G-34; readiness matrix bulk row | src/services/fhir-service/Services/BulkExportService.cs:InitiateExportAsync | tests/CloudHealthOffice.FhirService.Tests/Services/BulkExportServiceTests.cs (F1) | Current implementation immediately completes zero-count jobs and manufactures output URLs without performing export. Passing scaffold tests are not a real export. | Preserve this specific limitation; do not generalize to every FHIR/data-access workflow. |
| CAP-15 | Confirmed Gap | **DTR complete package/prepopulation** — docs/epa-gap-matrix.md G-54,G-55 | src/services/fhir-service/Services/DtrService.cs:GetQuestionnairePackageAsync | Code inspected; package behavior not independently exercised by this focused run | Returns Questionnaire-only collection and does not use patientId for prepopulation. Complete package semantics and clinical logic are not implemented by this method. | Keep narrow package limitation; credit existing forms/storage. |
| CAP-16 | Confirmed Gap | **CRD classification persistence portion of D-22** — docs/epa-gap-matrix.md D-22 | src/services/fhir-service/Services/CrdClassificationStore.cs | Code inspected; no restart integration test executed | Tenant classifications use expiring IMemoryCache; durable DTR creation does not resolve CRD classification persistence. | Split shared historical defect instead of marking the whole row fixed. |
| CAP-17 | Confirmed Gap | **QNXT authorization adapter** — docs/compliance/CMS0057-ACCEPTANCE-INVENTORY.md Augment boundary | src/services/authorization-service/Backends/QnxtAuthorizationBackend.cs | Methods inspected; no live QNXT test or credentials used | Production methods throw NotImplementedException. CHO-native persistence is not evidence of a live external-core adapter. | Preserve integration-specific limitation without downgrading Replace capability. |
| CAP-18 | Confirmed Gap | **Trained fraud weights** — src/ml/README.md | src/ml/claim-fraud-v1.pt | Asset inspected; no trained-model evaluation executed | Named asset is a text placeholder. Separate rejection-suggestion/advisory-examiner implementations still exist. | Keep model-specific limitation; no broad AI-is-missing statement. |
| CAP-19 | Unverified | **Full standards conformance and production readiness** — Historical complete/production-ready labels in docs/features/FHIR-IMPLEMENTATION-SUMMARY.md and roadmap/release/marketing assessments | Focused implementation review is not full IG validation, certified interoperability, deployment diligence or payer acceptance | No complete conformance/production campaign executed | Neither confirmed absence nor completed certification inferred. Requires specification/version, workflow, environment and dated results. | Annotate historical claims, qualify current copy and require dated evidence before stronger claims. |
| CAP-20 | Confirmed Gap | **FFS build/container coverage** — docs/deployment/KNOWN-GAPS.md | src/services/ffs-service/ directory; .github/workflows/deploy-azure-aks.yml; .github/workflows/docker-build.yml | Read-only inventory; no build/deployment performed | Service directory still lacks Dockerfile and named workflow entries. Keep as a build gap, distinct from fee engine functionality. | Retain assessment with dated verification; no configuration changes. |
| CAP-21 | Implemented | **Authorization updates are last-write-wins** — docs/architecture/prior-authorization.md Limitations | src/services/authorization-service/Repositories/AuthorizationRepositoryMongo.cs:RevisionIs/UpdateAsync; Exceptions/AuthorizationConcurrencyException.cs | src/services/authorization-service/AuthorizationService.Tests/Persistence/AuthorizationRepositoryMongoConcurrencyTests.cs:TwoConcurrentUpdates_FromTheSameRead_ExactlyOneWins_TheOtherConflicts; AReplaceBasedOnAReadBeforeAnSlaEscalation_Conflicts_InsteadOfUndoingIt (A1) | Real Mongo conditional writes use the read Revision; exactly one concurrent writer succeeds and stale updates cannot undo escalation. Scoped to the current Mongo repository; not a claim of distributed transactions across services. | Replace stale no-concurrency limitation with dated revision-fencing evidence. |
| CAP-22 | Confirmed Gap | **Default FHIR reads are all live payer-backed** — readiness matrix adapter warnings / ePA G-30 | src/services/fhir-service/Program.cs default IFhirDataAdapter and IPatientAccessDataProvider registrations | Host registration inspected; no live payer integration test executed | Default bindings still use MockFhirDataAdapter and MockPatientAccessDataProvider. This does not imply every FHIR endpoint or CHO-owned authorization record is mocked. | Preserve resource-specific demo/live labels; do not imply that PAS persistence resolves all source-system integration. |

## Repository-wide discovery inventory

Searched Markdown throughout the repository (excluding Git, dependencies, build outputs), READMEs, capability/readiness/roadmap/assessment documents and messaging sources for gap/TODO/placeholder and broad maturity language. Also inspected relevant implementation TODO/NotImplementedException paths for the scoped capabilities. Search hits identify candidates, not confirmed defects. This bounded reconciliation does not certify every historical claim or every code TODO. Unreviewed assertions below remain **Unverified**, not silently resolved.

| Source | Candidate lines at audited commit | Disposition |
| --- | --- | --- |
| `.github/ISSUE_TEMPLATE/v4-availity-integration.md` | 11 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `.github/ISSUE_TEMPLATE/v5-exchange-aptc-csr.md` | 233, 240 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `.github/ISSUE_TEMPLATE/v5-exchange-qhp-ehb.md` | 516 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `.github/instructions/scripts.instructions.md` | 284 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `AUDIT/investor-readiness-2026-09.md` | 442, 516, 614 | Current-status annotation / scoped corrections; capability rows above govern only verified claims. |
| `CHANGELOG.md` | 1499, 1920, 2082, 2145, 2279 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/announcements/v3.0.0-announcement.md` | 178 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/architecture/BRANCHING-STRATEGY.md` | 33, 44, 57, 792 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/architecture/accumulator-service.md` | 100, 119 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/architecture/appeals-case-model.md` | 29 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/architecture/benefit-plan-adapter-pattern.md` | 69, 70, 71, 132, 134, 136, 138, 141, 142 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/architecture/benefits-viewer.md` | 31, 111 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/architecture/claim-adapter-pattern.md` | 96, 97, 98, 180 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/architecture/claim-adjudication-pipeline.md` | 436, 465 | Current-status annotation / scoped corrections; capability rows above govern only verified claims. |
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
| `docs/compliance/CMS0057-ACCEPTANCE-INVENTORY.md` | 33, 36, 193, 194, 195, 196, 198 | Current-status annotation / scoped corrections; capability rows above govern only verified claims. |
| `docs/compliance/README.md` | 34 | Current-status annotation / scoped corrections; capability rows above govern only verified claims. |
| `docs/deployment/DEPLOYMENT-GATES-GUIDE.md` | 788 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/deployment/DEPLOYMENT-SECRETS-SETUP.md` | 102, 103, 444, 445, 566, 646, 686, 687 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/diligence/ASSESSMENT-CLAIMS-AUDIT.md` | 26 | Current-status annotation / scoped corrections; capability rows above govern only verified claims. |
| `docs/diligence/README.md` | 45, 46 | Current-status annotation / scoped corrections; capability rows above govern only verified claims. |
| `docs/epa-gap-matrix.md` | 22, 73, 74, 75, 78, 94, 103, 145, 169, 182, 264, 434 … | Current-status annotation / scoped corrections; capability rows above govern only verified claims. |
| `docs/features/276-277-IMPLEMENTATION-COMPLETE.md` | 3, 551 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/features/834-IMPLEMENTATION-SUMMARY.md` | 13, 20, 452 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/features/DEPLOYMENT-PIPELINE-FIX-SUMMARY.md` | 345 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/features/EDI-WORKFLOWS-COMPLETE.md` | 3 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/features/EDI-WORKFLOWS-DEPLOYMENT-SUMMARY.md` | 29, 227 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/features/FHIR-IMPLEMENTATION-SUMMARY.md` | 7, 14, 121, 201, 221, 320, 513 | Current-status annotation / scoped corrections; capability rows above govern only verified claims. |
| `docs/features/GATED-RELEASE-IMPLEMENTATION-SUMMARY.md` | 566 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/features/IMPLEMENTATION-SUMMARY.md` | 247, 315 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/features/MICROSERVICES-IMPLEMENTATION-STATUS.md` | 63, 64, 66, 67, 68, 69, 70, 71, 72, 75 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/features/MONITORING-IMPLEMENTATION-SUMMARY.md` | 116, 315, 335, 377 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/features/RELEASE-v4.0.0.md` | 48 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/features/ROADMAP.md` | 20 | Current-status annotation / scoped corrections; capability rows above govern only verified claims. |
| `docs/features/SAAS-LAUNCH-READINESS.md` | 11, 21 | Current-status annotation / scoped corrections; capability rows above govern only verified claims. |
| `docs/features/UPDATES-SUMMARY.md` | 64, 121 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/features/V4-LAUNCH-ROADMAP.md` | 9 | Current-status annotation / scoped corrections; capability rows above govern only verified claims. |
| `docs/features/VALUEADDS277-IMPLEMENTATION-COMPLETE.md` | 10, 336 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/features/WEBSITE-PHASE2-COMPLETE.md` | 10, 80, 95, 101, 102, 157 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/features/WEBSITE-UPDATES-FINAL.md` | 99 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/features/questionnaires/examples/qre-core-admin-adapter-example.md` | 6 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/guides/FEATURES.md` | 228, 304, 370, 489, 595, 697 | Current-status annotation / scoped corrections; capability rows above govern only verified claims. |
| `docs/implementation/PRIOR-AUTH-IMPLEMENTATION-SUMMARY.md` | 511 | Current-status annotation / scoped corrections; capability rows above govern only verified claims. |
| `docs/operations/NACHA-BANK-TRANSMISSION-RUNBOOK.md` | 308 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/security/argo-service-tokens.md` | 315 | Unverified outside scoped matrix; retain historical record, revalidate before reuse. |
| `docs/status/POSITIONING-AUDIT.md` | 30, 31, 33, 91, 97, 99, 107, 115, 160, 162, 168, 170 … | Current-status annotation / scoped corrections; capability rows above govern only verified claims. |
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
| D-23 | Unverified in this reconciliation; retain historical evidence pending focused review. |
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
| D-36 | Unverified in this reconciliation; retain historical evidence pending focused review. |
| D-37 | CAP-12 |
| D-38 | Unverified in this reconciliation; retain historical evidence pending focused review. |
| D-39 | CAP-12 |
| D-40 | Unverified in this reconciliation; retain historical evidence pending focused review. |
| G-60 | Unverified in this reconciliation; retain historical evidence pending focused review. |

## Executed checks — October 19, 2026

All focused test runs below executed against the fresh main-based checkout at the stated baseline, not the earlier authorization-lifecycle branch. Each command used `dotnet test <project> -m:1 --filter '<filter>' --verbosity quiet`. Later FHIR security run also used `--no-restore`. Existing SDK/package caches were used; no source fixes or new tests were introduced. Tests are scoped behavioral evidence, not full regulatory or production validation.

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
