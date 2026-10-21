using CloudHealthOffice.Infrastructure.Edi.Interchange;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Extensions;
using CloudHealthOffice.Infrastructure.Messaging;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Security;
using ClaimsService.Adapters;
using ClaimsService.EDI.Florida;
using ClaimsService.Fhir;
using ClaimsService.HostedServices;
using ClaimsService.Models.Adjudication;
using ClaimsService.Models.Messaging;
using ClaimsService.Repositories;
using ClaimsService.Services;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.PlanEdits;
using ClaimsService.Services.Adjudication.Stages;
using ClaimsService.Services.Migrations;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.ClaimsScrubEngine.Configuration;
using CloudHealthOffice.NcciEngine.Configuration;

var builder = WebApplication.CreateBuilder(args);
// Secret provider (Azure Key Vault / none)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

// Shared infrastructure: health checks, CORS, Swagger, database, tenant middleware
builder.Services.AddChoInfrastructure(builder.Configuration, options =>
{
    options.ServiceName = "Claims Service";
    options.ServiceDescription = "Healthcare claims processing for Cloud Health Office. " +
                                 "Handles 837 claim submission, 835 remittance, 277 status updates, and adjudication results.";
});

// CHO token authentication: tenant from the token, actor from the token,
// default deny. Reads need claims:read, writes claims:work; void, adjust,
// override approval and work-queue assignment are annotated with stricter
// permissions on their actions.
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = "claims:read";
    auth.DefaultWritePermission = "claims:work";
});

// Edit override policy (claim-review-workbench §2.3): CHO defaults, with
// per-tenant changes from Claims:EditOverridePolicy:Tenants:{tenant}:{category}.
builder.Services.Configure<ClaimsService.Services.EditOverridePolicyOptions>(
    builder.Configuration.GetSection(ClaimsService.Services.EditOverridePolicyOptions.SectionName));
builder.Services.AddSingleton<ClaimsService.Services.EditOverridePolicy>();

// Claim access log: every read of claim data is recorded (who, what, when,
// allowed or denied). The filter records reads that reach the action; the
// authorization result handler records the ones refused before it.
builder.Services.AddScoped<ClaimsService.Audit.ClaimAccessAuditWriter>();
builder.Services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(o => o.Filters.Add<ClaimsService.Audit.ClaimAccessAuditFilter>());
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, ClaimsService.Audit.ClaimAccessDenialRecorder>();

// Repository — MongoDB is the only claim store (ADR 006); the database client
// is registered by the shared lib.
if (string.IsNullOrEmpty(builder.Configuration["MongoDb:ConnectionString"]))
{
    throw new InvalidOperationException(
        "Claims Service requires a database. Configure MongoDb:ConnectionString.");
}

builder.Services.AddScoped<IClaimRepository, ClaimRepositoryMongo>();
builder.Services.AddHostedService<ClaimIndexInitializer>();
builder.Services.AddScoped<IAiExaminationAuditRepository, AiExaminationAuditRepositoryMongo>();
builder.Services.AddScoped<IClaimNoteRepository, ClaimNoteRepositoryMongo>();
builder.Services.AddScoped<IClaimAccessAuditRepository, ClaimAccessAuditRepositoryMongo>();
builder.Services.AddScoped<IMassAdjudicationRunRepository, MassAdjudicationRunRepositoryMongo>();
builder.Services.AddScoped<IClaimImportTransactionRepository, ClaimImportTransactionRepositoryMongo>();
builder.Services.AddHostedService<MassAdjudicationRunIndexInitializer>();

// Claim version event publisher (5.1) — Mongo append-only stream is the
// system-of-record for the version chain. Mirrors
// MongoProviderVersionEventPublisher / MongoPlanVersionEventPublisher.
builder.Services.AddScoped<IClaimVersionEventPublisher, MongoClaimVersionEventPublisher>();
builder.Services.AddScoped<IClaimVersionEventReader, MongoClaimVersionEventReader>();
builder.Services.AddHostedService<ClaimVersionEventIndexInitializer>();

// 5.12a — ClaimAdjustment aggregate persistence (Mongo per Gap 4
// ratification). Indexes (chain-uniqueness for depth=1, idempotency,
// status+createdAt for ReversalRun batch queries) are created once
// at startup by ClaimAdjustmentIndexInitializer so scoped repository
// resolution stays side-effect free.
builder.Services.AddScoped<IClaimAdjustmentRepository, ClaimAdjustmentRepositoryMongo>();
builder.Services.AddHostedService<ClaimAdjustmentIndexInitializer>();

// FHIR R4 ExplanationOfBenefit projector — hand-built JsonObject to avoid
// the Hl7.Fhir.R4 transitive dep; used by the v1 member-scoped claims endpoint.
builder.Services.AddSingleton<IExplanationOfBenefitProjector, ExplanationOfBenefitProjector>();

// 277CA acknowledgment generator
builder.Services.AddScoped<IClaimAcknowledgmentService, ClaimAcknowledgmentService>();

// X12 interchange control: ISA/IEA validation, TA1 generation/storage,
// duplicate ISA13 detection and outbound TA1 tracking (shared library).
builder.Services.AddChoX12Interchange(builder.Configuration);
builder.Services.AddScoped<IDiagnosisDescriptionLookup, DiagnosisDescriptionLookup>();
builder.Services.AddScoped<IClaimDiagnosisMetadataEnricher, ClaimDiagnosisMetadataEnricher>();

// 5.10 — claim finalization (Approved/PartiallyPaid → Paid). Owns the
// idempotent Paid transition with version-event chain advancement and
// Kafka claims.finalized.v1 emission. Backed by the existing
// IClaimRepository / IClaimVersionEventPublisher / IClaimEventPublisher
// triple — no new infrastructure surfaces. The
// ClaimsController.ProcessRemittance endpoint delegates here for every
// remittance, $0 included; DELETE {id}, PUT {id}/status (Voided) and
// POST {id}/void all void through VoidAsync.
builder.Services.AddScoped<IClaimFinalizationService, ClaimFinalizationService>();

// ClaimsController application services: the examiner resolution of a pended
// claim (work-queue/{id}/resolve and /override), the operational status
// transitions of PUT {id}/status, and the void actions (through VoidAsync).
// Factory registrations on purpose: their dependencies are resolved when the
// controller is activated, exactly as when the controller took them itself,
// and not at host build (ValidateOnBuild checks typed registrations only),
// so a host that omits a dependency only the controller uses still starts.
builder.Services.AddScoped<IClaimVoidService>(sp => ActivatorUtilities.CreateInstance<ClaimVoidService>(sp));
builder.Services.AddScoped<IClaimStatusTransitionService>(sp =>
    ActivatorUtilities.CreateInstance<ClaimStatusTransitionService>(sp));
builder.Services.AddScoped<IClaimResolutionService>(sp => ActivatorUtilities.CreateInstance<ClaimResolutionService>(sp));

// 5.12a — Adjustment workflow service. Owns the supersession transition,
// invokes IClaimSubmissionService for re-adjudication, emits
// ClaimVersionSuperseded + ClaimVersionReversed events. Lifecycle ratified
// by Decision 18: AwaitingReadjudication → PendingReversal → Active.
// 5.12b's payment-service ReversalRunService consumes the resulting
// PendingReversal rows.
builder.Services.AddScoped<IClaimAdjustmentService, ClaimAdjustmentService>();
// Inbound 837 replacements (7) and voids (8) amend the claim named in REF*F8.
builder.Services.AddScoped<IInboundClaimCorrectionService, InboundClaimCorrectionService>();

// Inter-service HTTP clients
builder.Services.AddHttpClient("ProviderService", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:ProviderService"]
        ?? "http://provider-service:8080");
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));

builder.Services.AddHttpClient("ReferenceDataService", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:ReferenceDataService"]
        ?? "http://reference-data-service:8080");
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));

builder.Services.AddHttpClient(UpstreamClientNames.TerminologyService, client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:TerminologyServiceUrl"]
        ?? builder.Configuration["Services:TerminologyService"]
        ?? "http://terminology-service");
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));

// FL FMMIS encounter submission pipeline
builder.Services.AddScoped<IProviderService, HttpProviderService>();
builder.Services.AddScoped<ITenantComplianceConfigService, HttpTenantComplianceConfigService>();
builder.Services.AddScoped<FmmisClaimTransformer>();
builder.Services.AddScoped<FmmisFileBuilder>();

// FL SMMC 3.0 MPIP rate enhancement
builder.Services.AddScoped<IMpipRateClient, MpipRateClient>();
builder.Services.AddScoped<IMpipAdjudicationEnhancer, MpipAdjudicationEnhancer>();

// Claim lifecycle event publisher (Kafka).
// Always registered: when Kafka:BootstrapServers is unset nothing is queued
// or published and a warning is logged, so dev/test environments without
// Kafka remain functional.
// The shared CHO producer (idempotent, acks=all, bounded delivery timeout).
builder.Services.AddSingleton<CloudHealthOffice.Events.Kafka.IChoEventProducer>(sp =>
{
    var kafka = sp.GetRequiredService<IConfiguration>().GetSection("Kafka");
    return new CloudHealthOffice.Events.Kafka.ChoKafkaProducer(new CloudHealthOffice.Events.Kafka.ChoKafkaProducerSettings
    {
        BootstrapServers = kafka["BootstrapServers"],
        ClientId = kafka["ClientId"] ?? "claims-service",
        SaslUsername = kafka["SaslUsername"],
        SaslPassword = kafka["SaslPassword"],
    });
});
// claims.finalized.v1 goes through the claim-event outbox: the event is stored
// on the claim with the status change and published (inline, then retried by
// the dispatcher) until Kafka acknowledges it.
builder.Services.Configure<ClaimEventOutboxOptions>(builder.Configuration.GetSection(ClaimEventOutboxOptions.SectionName));
builder.Services.AddSingleton<ClaimEventOutboxDrainer>();
builder.Services.AddHostedService<ClaimEventOutboxDispatcher>();
builder.Services.AddSingleton<ClaimEventPublisher>();
builder.Services.AddSingleton<IClaimEventPublisher>(sp => sp.GetRequiredService<ClaimEventPublisher>());
// Work needed / resolved for work-management-service (claims.work.v1), on the
// same producer. Publishing never blocks or fails a claim write.
builder.Services.AddSingleton<IClaimWorkEventPublisher>(sp => sp.GetRequiredService<ClaimEventPublisher>());

// Claim adapter pattern (5.2 — tenant-routed claims backends). Cache is
// singleton (TTL across requests); adapters and factory are scoped because
// the CHO adapter wraps the scoped IClaimRepository. Tenant-service HTTP
// client uses a 5-second timeout so a flaky tenant-service can't stall claim
// reads. A 401/403 from tenant-service fails the operation; 404, 5xx or an
// unreachable tenant-service use "cho" for that call only (TenantPlatformLookup).
builder.Services.AddHttpClient(ClaimTenantConfigCache.HttpClientName)
    .SetHandlerLifetime(TimeSpan.FromMinutes(5))
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddSingleton<ClaimTenantConfigCache>();
builder.Services.AddScoped<IClaimAdapter, ChoClaimAdapter>();
builder.Services.AddScoped<IClaimAdapter, QnxtClaimAdapter>();
builder.Services.AddScoped<IClaimAdapter, FacetsClaimAdapter>();
builder.Services.AddScoped<IClaimAdapter, HealthEdgeClaimAdapter>();
builder.Services.AddScoped<ClaimAdapterFactory>();

// Canonical claim submission orchestration (5.3). Wraps the
// adapter call with structural validation and ClaimVersionSubmitted
// event emission. Both POST /api/v1/claims and the deprecated
// legacy POST /api/claims route through this single seam so the
// version-event chain has no gaps.
//
// 5.5 modification: ClaimSubmissionService also emits a
// ClaimVersionSubmittedMessage onto the claim-version-events Service
// Bus topic so the adjudication orchestrator picks it up.
builder.Services.AddScoped<IClaimSubmissionService, ClaimSubmissionService>();

// ─── Capability 5.5 — adjudication pipeline ────────────────────────
// Service Bus messaging shared abstraction. Resolves to InMemory in
// Development / when no connection string is configured. In production
// requires Messaging:ServiceBusConnectionString.
builder.Services.AddChoMessaging(builder.Configuration, builder.Environment);

// IMemoryCache backs the resolution decorators. Not previously
// registered in claims-service — see the 5.5 plan, drift C.
builder.Services.AddMemoryCache();

// Per-tenant pipeline configuration. Phase 1 is service-wide; per-tenant
// override is deferred to Phase 2.
builder.Services.Configure<AdjudicationPipelineOptions>(
    builder.Configuration.GetSection(AdjudicationPipelineOptions.SectionName));

// Prior-authorization scope (G-25). Plan-flagged codes are off by default
// until line-level authorization decisions (G-07) land: see
// PriorAuthAdjudicationOptions.UsePlanFlaggedCodes.
builder.Services.Configure<PriorAuthAdjudicationOptions>(
    builder.Configuration.GetSection(PriorAuthAdjudicationOptions.SectionName));

// 5.6 enforcement posture (network membership + credentialing). Phase 1
// is service-wide; per-tenant override deferred to Phase 2 alongside
// the pipeline options.
builder.Services.Configure<TenantEnforcementPolicyOptions>(
    builder.Configuration.GetSection(TenantEnforcementPolicyOptions.SectionName));
builder.Services.Configure<TimelyFilingOptions>(
    builder.Configuration.GetSection(TimelyFilingOptions.SectionName));

var benefitPlanServiceTimeoutSeconds = Math.Clamp(
    builder.Configuration.GetValue<int?>("Services:BenefitPlanServiceTimeoutSeconds") ?? 5,
    1,
    300);

// Resolution clients — typed HttpClient + caching decorator. The benefit-plan
// client also backs the adjudication calculate-benefits shim, so benchmark
// runners can raise this timeout without changing normal fail-fast defaults.
builder.Services.AddHttpClient(HttpBenefitPlanResolver.HttpClientName, client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:BenefitPlanService"]
        ?? "http://benefit-plan-service:8080");
    client.Timeout = TimeSpan.FromSeconds(benefitPlanServiceTimeoutSeconds);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));
// Both resolver layers are stateless/thread-safe. Singleton lifetime lets the
// caching decorator coalesce concurrent first reads for a newly published
// plan across claim-processing scopes in this replica.
builder.Services.AddSingleton<HttpBenefitPlanResolver>();
builder.Services.AddSingleton<IBenefitPlanResolver>(sp =>
    new CachingBenefitPlanResolver(
        sp.GetRequiredService<HttpBenefitPlanResolver>(),
        sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()));

builder.Services.AddHttpClient(HttpMemberResolver.HttpClientName, client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:MemberService"]
        ?? "http://member-service:8080");
    client.Timeout = TimeSpan.FromSeconds(5);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));
builder.Services.AddScoped<HttpMemberResolver>();
builder.Services.AddScoped<IMemberResolver>(sp =>
    new CachingMemberResolver(
        sp.GetRequiredService<HttpMemberResolver>(),
        sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()));

// BenefitCalculationEngine — HTTP shim against benefit-plan-service's
// /api/v1/adjudication/calculate-benefits endpoint. The engine ships as
// a class library (BP 5.10) but its host-side collaborators
// (IBenefitPlanProvider, IAccumulatorService) are wired in
// benefit-plan-service against benefit-plan-service's data stores.
// Standing them up inside claims-service would mean importing the
// entire plan + accumulator data layer — that's a Phase 2 split. The
// HTTP shim consumes the canonical engine through the same surface
// portal/preview features already use.
builder.Services.AddScoped<
    CloudHealthOffice.BenefitEngine.Services.IBenefitCalculationEngine,
    HttpBenefitCalculationEngineClient>();

// Scoped tenant context — the orchestrator pins the tenant id on this
// holder before stages run so the HTTP shim can send X-Tenant-ID
// downstream from a background Service Bus subscription that has no
// HttpContext. Scoped lifetime keeps each orchestrator run isolated.
builder.Services.AddScoped<IAdjudicationTenantContext, AdjudicationTenantContext>();

// 5.6 — enforcement clients (network membership + credentialing status)
// against provider-service. Reuses the existing ProviderService named
// HttpClient registration above. Each client gets a caching decorator;
// TTLs differ by domain (5-min membership vs 1-hour credentialing).
builder.Services.AddScoped<HttpProviderMembershipClient>();
builder.Services.AddScoped<IProviderMembershipClient>(sp =>
    new CachingProviderMembershipClient(
        sp.GetRequiredService<HttpProviderMembershipClient>(),
        sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()));

builder.Services.AddScoped<HttpCredentialingStatusClient>();
builder.Services.AddScoped<ICredentialingStatusClient>(sp =>
    new CachingCredentialingStatusClient(
        sp.GetRequiredService<HttpCredentialingStatusClient>(),
        sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()));

// ProviderIntegrityStage — federal exclusion check via benefit-plan-service's
// provider-integrity endpoint. No caching decorator here: HttpProviderIntegrityGate
// already caches on the benefit-plan-service side (1-hour IMemoryCache,
// never-fail-open contract); a second claims-service-side cache would just
// add staleness risk without a real latency win, since the upstream is
// already fast on a cache hit.
builder.Services.AddScoped<IProviderIntegrityClient, HttpProviderIntegrityClient>();

// PricingStage (Order=250) — fee schedule resolution via benefit-plan-service's
// resolve-rates endpoint (IRateResolutionService). Reuses the BenefitPlanService named HttpClient; never fails open.
builder.Services.AddScoped<IFeeSchedulePricingClient, HttpFeeSchedulePricingClient>();

// 5.4 — Claims Scrub Engine (class library). Default standard rule set;
// per-tenant rule overrides remain a Phase 2 surface.
builder.Services.AddClaimsScrubEngine();

// Raw 837 intake: WEDI SNIP 1–5 validation before parse/map, with a 999.
// Per-level Reject/Warn/Off under ClaimsImport:Snip (see Snip837ValidationOptions).
// An ISnipCodeSetReference, when registered, adds code-set membership checks.
// Validated at start-up: an undefined or misspelt level (global or partner
// override) fails fast instead of being silently dropped by the binder.
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<ClaimsService.EDI.Validation.Snip837ValidationOptions>>(
    sp => new ClaimsService.EDI.Validation.Snip837ValidationOptionsValidator(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddOptions<ClaimsService.EDI.Validation.Snip837ValidationOptions>()
    .Bind(builder.Configuration.GetSection(ClaimsService.EDI.Validation.Snip837ValidationOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<ClaimsService.EDI.Validation.ISnip837Validator>(sp =>
    new ClaimsService.EDI.Validation.X12837SnipValidator(
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ClaimsService.EDI.Validation.Snip837ValidationOptions>>(),
        sp.GetService<ClaimsService.EDI.Validation.ISnipCodeSetReference>()));

// 5.7 — NCCI / MUE engine (class library). Auto-detect repository binds
// to the IMongoDatabase AddChoInfrastructure registered. Seed
// data is operator-controlled (Phase 1 — engine ScrubAsync is
// graceful when a tenant's table is empty, surfacing zero failures
// with telemetry rather than throwing).
builder.Services.AddNcciEngine().UseRepositoryFromConfiguration(builder.Configuration);

// 5.8 — CobEngine services. Pure-calculation, stateless, no I/O —
// Singleton lifetime is correct (plan Decision 1b: no AddCobEngine
// extension exists; direct DI is the established convention for this
// engine). ICobCalculationService registers but is unused in 5.8 stage
// logic — Phase 2 priorEob work exercises it for CHO-secondary
// calculation. CoordinationOfBenefitsStage exercises only
// IPayerOrderService for audit-trail rule labelling.
builder.Services.AddSingleton<
    CloudHealthOffice.CobEngine.Services.ICobCalculationService,
    CloudHealthOffice.CobEngine.Services.CobCalculationService>();
builder.Services.AddSingleton<
    CloudHealthOffice.CobEngine.Services.IPayerOrderService,
    CloudHealthOffice.CobEngine.Services.PayerOrderService>();

// 5.8 — CoverageService HTTP client + cached resolution-client triple.
// 5-minute TTL mirrors network-membership (coverage records can
// terminate without explicit signal — open-enrollment loss, mid-year
// term — vs. credentialing's 1-hour TTL where transitions are explicit
// audit-trailed events). 5-second timeout mirrors the other resolution
// clients so a flaky coverage-service can't stall the pipeline.
builder.Services.AddHttpClient(UpstreamClientNames.CoverageService, client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:CoverageService"]
        ?? "http://coverage-service:8080");
    client.Timeout = TimeSpan.FromSeconds(5);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));
builder.Services.AddScoped<HttpCoverageClient>();
builder.Services.AddScoped<ICoverageClient>(sp =>
    new CachingCoverageClient(
        sp.GetRequiredService<HttpCoverageClient>(),
        sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()));

// Coverage-to-plan resolver — lets a claim that arrives without a
// BenefitPlanId (the X12 837 on-ramp; see X12837ClaimMapper) still find the
// member's active plan before BenefitCalculationStage runs. Reuses the same
// CoverageService named HttpClient as HttpCoverageClient above.
builder.Services.AddScoped<HttpCoverageResolver>();
builder.Services.AddScoped<ICoverageResolver>(sp =>
    new CachingCoverageResolver(
        sp.GetRequiredService<HttpCoverageResolver>(),
        sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()));

// Prior-authorization validation (D-04). Only a definitive answer from
// authorization-service pays or denies; any failed lookup pends the claim
// PAUNAVAIL (owner decision Q-9).
builder.Services.AddHttpClient(UpstreamClientNames.AuthorizationService, client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:AuthorizationService"]
        ?? "http://authorization-service");
    client.Timeout = TimeSpan.FromSeconds(5);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));
builder.Services.AddScoped<HttpAuthorizationValidationClient>();
builder.Services.AddScoped<IAuthorizationValidationClient, HttpAuthorizationValidationClient>();

// Stages — registered as IEnumerable<IClaimAdjudicationStage>. Capabilities
// 5.4-5.9 each replace one stub registration with the real stage that
// wraps the corresponding engine. 5.4/5.6/5.7/5.8/5.9 swap the registration
// in place rather than going through services.RemoveAll<>() — the stub
// never shipped to production so there's nothing to remove. 6/6 pipeline
// stages real after 5.9. ProviderIntegrityStage (Order=150) added later,
// closing a gap the original 5.5 stage scope never covered — see the
// stage's own doc comment and docs/architecture/claim-adjudication-pipeline.md.
builder.Services.AddScoped<IClaimAdjudicationStage, ScrubbingStage>();
builder.Services.AddScoped<IClaimAdjudicationStage, TimelyFilingStage>(); // Order=110
builder.Services.AddScoped<IClaimAdjudicationStage, DuplicateClaimStage>(); // Order=120
builder.Services.AddScoped<IClaimAdjudicationStage, ProviderIntegrityStage>();
builder.Services.AddScoped<IClaimAdjudicationStage, NetworkCredentialingStage>();
builder.Services.AddScoped<IClaimAdjudicationStage, PricingStage>();
builder.Services.AddScoped<IClaimAdjudicationStage, BenefitCalculationStage>();
builder.Services.AddScoped<IClaimAdjudicationStage, NcciEditsStage>();
// The plan's own edit service (ADR 019), Order=450. Passes straight through
// for a tenant with no Adjudication:PlanEdits:Tenants entry.
builder.Services.AddPlanEditService(builder.Configuration, builder.Environment);
builder.Services.AddScoped<IClaimAdjudicationStage, PlanEditsStage>();
builder.Services.AddScoped<IClaimAdjudicationStage, CoordinationOfBenefitsStage>();
builder.Services.AddScoped<IClaimAdjudicationStage, AiExaminationStage>();
builder.Services.AddScoped<IClaimAdjudicationStage, PersistenceStage>();

// Accumulator outbox: a finally adjudicated claim's engine commit (or a
// denial's terminal reversal) is written on the claim with the write that
// finalizes it, attempted at once, and re-driven by this dispatcher with
// backoff until the engine store has it (see claim-cob-pipeline.md).
builder.Services.Configure<AccumulatorOutboxOptions>(builder.Configuration.GetSection(AccumulatorOutboxOptions.SectionName));
builder.Services.AddScoped<IAccumulatorOutboxProcessor, AccumulatorOutboxProcessor>();
builder.Services.AddHostedService<AccumulatorOutboxDispatcher>();
// The dispatcher scans the base claims database only: Degraded (and a
// critical log at start-up) when tenant-scoped databases are configured.
builder.Services.AddHealthChecks().AddCheck<AccumulatorOutboxScopeHealthCheck>(
    AccumulatorOutboxScopeHealthCheck.Name, tags: ["ready"]);
// Operator tools: the post-rollout sweep and the ACCUMCOMMIT bulk re-run.
builder.Services.AddScoped<IAccumulatorCommitLookup, HttpBenefitCalculationEngineClient>();
builder.Services.AddScoped<AccumulatorOperationsService>();
// One-time Pay/Deny lifecycle backfill (Denied-at-adjudication -> Deny); opt-in.
builder.Services.Configure<ClaimsService.Services.Migrations.DenyStatusBackfillOptions>(
    builder.Configuration.GetSection(ClaimsService.Services.Migrations.DenyStatusBackfillOptions.SectionName));
builder.Services.AddScoped<ClaimsService.Services.Migrations.DenyStatusBackfillService>();

builder.Services.AddScoped<ClaimAdjudicationOrchestrator>();
builder.Services.AddScoped<IClaimAdjudicationOrchestrator>(sp => sp.GetRequiredService<ClaimAdjudicationOrchestrator>());
// Examiner approval of a pended claim re-runs the pipeline (accumulators committed after the final write).
builder.Services.AddScoped<IClaimApprovalReadjudicator>(sp => sp.GetRequiredService<ClaimAdjudicationOrchestrator>());

var adjudicationMaxConcurrentCalls = Math.Max(
    1,
    builder.Configuration.GetValue<int?>("Messaging:AdjudicationMaxConcurrentCalls") ?? 16);

// Subscription hosted service — the orchestrator's Service Bus
// trigger. Factory shape defers subscription creation to ExecuteAsync
// so the bus is fully initialised before we Subscribe.
builder.Services.AddHostedService(sp =>
    new SubscriptionHostedService(
        services => services.GetRequiredService<IMessageBus>().Subscribe<ClaimVersionSubmittedMessage>(
            ClaimVersionEventTopics.TopicName,
            async (msg, ctx, ct) =>
            {
                using var scope = services.CreateScope();
                var orchestrator = scope.ServiceProvider
                    .GetRequiredService<IClaimAdjudicationOrchestrator>();
                await orchestrator.AdjudicateAsync(msg, ctx, ct);
            },
            new SubscriptionOptions(
                MaxConcurrentCalls: adjudicationMaxConcurrentCalls,
                SubscriptionName: ClaimVersionEventTopics.AdjudicationSubscriptionName,
                RequiredProperties: new Dictionary<string, string>
                {
                    [ClaimVersionEventTopics.MessageTypeProperty] = ClaimVersionMessageTypes.Submitted
                })),
        sp,
        sp.GetRequiredService<ILogger<SubscriptionHostedService>>()));

builder.Services.AddChoObservability(builder.Configuration);

var app = builder.Build();

app.UseChoObservability();

// Shared middleware pipeline: exception handling, Swagger (dev), CORS, then
// UseChoAuthentication (authentication, tenant from the token, authorization), health checks
app.UseChoInfrastructure(builder.Configuration);

app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.MapControllers();
app.MapChoX12InterchangeEndpoints("api/v1/claims/interchange");

app.Run();

// Expose Program class for WebApplicationFactory in integration tests
public partial class Program { }
