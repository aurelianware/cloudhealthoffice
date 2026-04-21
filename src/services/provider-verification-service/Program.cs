namespace CloudHealthOffice.ProviderVerificationService;

using CloudHealthOffice.ProviderVerificationEngine;
using CloudHealthOffice.ProviderVerificationEngine.DataSources;
using CloudHealthOffice.ProviderVerificationEngine.DataSources.Nppes;
using CloudHealthOffice.ProviderVerificationEngine.Models;
using CloudHealthOffice.ProviderVerificationEngine.Scoring;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Observability;

// TODO(refactor): consider converting this Program to top-level statements for
// consistency with the rest of the service fleet (out of scope for A.7.4).
public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        // Secret provider (Azure Key Vault / none)
        builder.Services.AddSecretProvider(builder.Configuration);
        builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

        // ── Configuration ────────────────────────────────────────
        builder.Services.Configure<VerificationOptions>(
            builder.Configuration.GetSection(VerificationOptions.SectionName));
        builder.Services.Configure<ScoringWeights>(
            builder.Configuration.GetSection(ScoringWeights.SectionName));

        // ── Data Source Adapters ─────────────────────────────────
        // Tier 1: NPPES (free, no auth)
        builder.Services.AddHttpClient<INppesAdapter, NppesHttpAdapter>(client =>
        {
            client.BaseAddress = new Uri(
                builder.Configuration["ProviderVerification:NppesApiBaseUrl"]
                ?? "https://npiregistry.cms.hhs.gov/api/");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddStandardResilienceHandler(); // Polly retry + circuit breaker via MS.Ext.Http.Resilience

        // Tier 1: NLM Taxonomy Crosswalk (free, no auth)
        // TODO: Register NlmTaxonomyCrosswalkAdapter

        // Tier 2: Exclusion screening (LEIE bulk + SAM.gov API)
        // TODO: Register ExclusionScreeningAdapter

        // Tier 2: PECOS (bulk CSV sync)
        // TODO: Register PecosAdapter

        // Tier 2: Open Payments (data.cms.gov SODA API)
        // TODO: Register OpenPaymentsAdapter

        // Tier 2: Medicare Utilization (data.cms.gov SODA API)
        // TODO: Register MedicareUtilizationAdapter

        // Tier 3: FSMB (paid, conditional registration)
        // TODO: Register FsmbAdapter if configured

        // ── Placeholder registrations for unimplemented adapters ─
        builder.Services.AddSingleton<INlmTaxonomyCrosswalkAdapter, NullNlmAdapter>();
        builder.Services.AddSingleton<IExclusionScreeningAdapter, NullExclusionAdapter>();
        builder.Services.AddSingleton<IPecosAdapter, NullPecosAdapter>();
        builder.Services.AddSingleton<IOpenPaymentsAdapter, NullOpenPaymentsAdapter>();
        builder.Services.AddSingleton<IMedicareUtilizationAdapter, NullUtilizationAdapter>();
        builder.Services.AddSingleton<IFsmbAdapter, NullFsmbAdapter>();

        // ── Engine + Orchestrator ────────────────────────────────
        builder.Services.AddSingleton<IntegrityScoreCalculator>();
        builder.Services.AddScoped<ProviderVerificationOrchestrator>();

        // ── Background Services ──────────────────────────────────
        // TODO: builder.Services.AddHostedService<NppesBulkSyncWorker>();
        // TODO: builder.Services.AddHostedService<LeieSyncWorker>();
        // TODO: builder.Services.AddHostedService<PecosSyncWorker>();

        // ── Health checks ────────────────────────────────────────
        var healthChecks = builder.Services.AddHealthChecks();

        if (builder.Configuration.GetValue("HealthChecks:EnableExternalNppesCheck", true))
        {
            healthChecks.AddUrlGroup(
                new Uri("https://npiregistry.cms.hhs.gov/api/?version=2.1&number=1234567893"),
                name: "nppes-api",
                tags: ["readiness"],
                timeout: TimeSpan.FromSeconds(10));
        }

        // ── OpenAPI / Swagger ────────────────────────────────────
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new()
            {
                Title = "Cloud Health Office Provider Verification API",
                Version = "v1",
                Description = "Multi-source provider verification and integrity scoring. " +
                              "Aggregates NPPES, OIG/LEIE, PECOS, Open Payments, and FSMB data.",
                Contact = new() { Name = "Aurelianware", Url = new("https://cloudhealthoffice.com") }
            });
        });

        builder.Services.AddChoObservability(builder.Configuration);

        var app = builder.Build();

        app.UseChoObservability();

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        // Liveness: no external dependencies — pod is alive if the process responds
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false // no checks — just confirms the app is running
        });

        // Readiness: includes external dependency checks (NPPES when enabled)
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("readiness")
        });

        // Backward compat alias — maps to liveness
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            Predicate = _ => false
        });

        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        // API Endpoints
        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

        var api = app.MapGroup("/api/v1/providers")
            .WithTags("Provider Verification");

        // ── Verify a single provider ─────────────────────────────
        api.MapGet("/{npi}/verify", async (
            string npi,
            [FromQuery] VerificationTier? tier,
            IOptions<VerificationOptions> verificationOptions,
            ProviderVerificationOrchestrator orchestrator,
            CancellationToken ct) =>
        {
            var result = await orchestrator.VerifyProviderAsync(
                npi,
                tier ?? verificationOptions.Value.DefaultTier,
                ct);

            return result.Status == VerificationStatus.Failed
                ? Results.NotFound(new { error = "NPI not found", npi })
                : Results.Ok(result);
        })
        .WithName("VerifyProvider")
        .WithSummary("Full multi-source provider verification")
        .WithDescription(
            "Runs NPPES validation, exclusion screening (LEIE/SAM), " +
            "PECOS enrollment check, Open Payments conflict analysis, " +
            "and optionally FSMB license verification. Returns a composite " +
            "integrity score with per-dimension breakdowns and flags.")
        .Produces<ProviderVerificationRecord>()
        .Produces(404);

        // ── NPPES lookup only (lightweight) ──────────────────────
        api.MapGet("/{npi}/nppes", async (
            string npi,
            INppesAdapter nppes,
            CancellationToken ct) =>
        {
            var result = await nppes.LookupByNpiAsync(npi, ct);
            return result is null
                ? Results.NotFound(new { error = "NPI not found in NPPES", npi })
                : Results.Ok(result);
        })
        .WithName("NppesLookup")
        .WithSummary("Direct NPPES NPI lookup")
        .Produces<NppesProviderData>()
        .Produces(404);

        // ── NPPES search ─────────────────────────────────────────
        api.MapGet("/search/nppes", async (
            [AsParameters] NppesSearchCriteria criteria,
            INppesAdapter nppes,
            CancellationToken ct) =>
        {
            var results = await nppes.SearchAsync(criteria, ct);
            return Results.Ok(new { count = results.Count, results });
        })
        .WithName("NppesSearch")
        .WithSummary("Search NPPES by name, location, taxonomy");

        // ── Integrity score only (for claims pre-check) ──────────
        api.MapGet("/{npi}/integrity-score", async (
            string npi,
            [FromQuery] VerificationTier? tier,
            IOptions<VerificationOptions> verificationOptions,
            ProviderVerificationOrchestrator orchestrator,
            CancellationToken ct) =>
        {
            var result = await orchestrator.VerifyProviderAsync(
                npi,
                tier ?? verificationOptions.Value.DefaultTier,
                ct);

            return Results.Ok(new
            {
                npi,
                result.IntegrityScore.CompositeScore,
                result.IntegrityScore.Rating,
                result.Status,
                result.IntegrityScore.Flags,
                verifiedAt = result.LastVerifiedAt
            });
        })
        .WithName("IntegrityScore")
        .WithSummary("Lightweight integrity score for claims adjudication pre-check");

        // ── Batch verification (POST) ────────────────────────────
        api.MapPost("/verify/batch", async (
            [FromBody] BatchVerificationRequest? request,
            ProviderVerificationOrchestrator orchestrator,
            CancellationToken ct) =>
        {
            if (request is null || request.Npis is null || request.Npis.Count == 0)
                return Results.BadRequest(new { error = "Request body must include a non-empty 'npis' array." });

            if (request.Npis.Count > 100)
                return Results.BadRequest(new { error = "Batch size exceeds the 100-NPI limit.", count = request.Npis.Count });

            var invalidNpis = request.Npis
                .Select((npi, i) => new { npi, index = i })
                .Where(x => string.IsNullOrWhiteSpace(x.npi))
                .Select(x => x.index)
                .ToList();

            if (invalidNpis.Count > 0)
                return Results.BadRequest(new { error = "One or more NPIs are null or empty.", invalidIndices = invalidNpis });

            var results = new List<ProviderVerificationRecord>();
            await foreach (var record in orchestrator.BatchVerifyAsync(
                request.Npis, request.Tier ?? VerificationTier.Standard, ct))
            {
                results.Add(record);
            }

            return Results.Ok(new
            {
                count = results.Count,
                summary = new
                {
                    verified = results.Count(r => r.Status == VerificationStatus.Verified),
                    warnings = results.Count(r => r.Status == VerificationStatus.VerifiedWithWarnings),
                    excluded = results.Count(r => r.Status == VerificationStatus.Excluded),
                    failed = results.Count(r => r.Status == VerificationStatus.Failed),
                    manualReview = results.Count(r => r.Status == VerificationStatus.ManualReviewRequired)
                },
                results
            });
        })
        .WithName("BatchVerify")
        .WithSummary("Batch verify multiple providers")
        .WithDescription("Accepts up to 100 NPIs per request. " +
                         "For full-network re-verification, use the scheduled background job.");

        app.Run();
    }
}

public record BatchVerificationRequest(
    List<string> Npis,
    VerificationTier? Tier = null);

// ─────────────────────────────────────────────────────────────────
// Null/placeholder adapters — replaced as each source is implemented
// ─────────────────────────────────────────────────────────────────

internal class NullNlmAdapter : INlmTaxonomyCrosswalkAdapter
{
    public Task<TaxonomyCrosswalkResult?> LookupTaxonomyAsync(string taxonomyCode, CancellationToken ct) =>
        Task.FromResult<TaxonomyCrosswalkResult?>(null);

    public Task<List<TaxonomyCrosswalkResult>> SearchBySpecialtyAsync(string specialty, CancellationToken ct) =>
        Task.FromResult(new List<TaxonomyCrosswalkResult>());
}

internal class NullExclusionAdapter : IExclusionScreeningAdapter
{
    public Task<ExclusionScreeningResult> ScreenProviderAsync(string npi, string? firstName, string? lastName, DateTimeOffset? dateOfBirth, CancellationToken ct) =>
        Task.FromResult(new ExclusionScreeningResult { Source = ExclusionScreeningSource.OigLeie });

    public Task<List<ExclusionScreeningResult>> BatchScreenAsync(IEnumerable<ProviderScreeningRequest> providers, CancellationToken ct) =>
        Task.FromResult(new List<ExclusionScreeningResult>());

    public Task<BulkSyncResult> SyncExclusionListsAsync(CancellationToken ct) =>
        Task.FromResult(new BulkSyncResult { Source = "LEIE" });
}

internal class NullPecosAdapter : IPecosAdapter
{
    public Task<PecosEnrollmentStatus?> GetEnrollmentStatusAsync(string npi, CancellationToken ct) =>
        Task.FromResult<PecosEnrollmentStatus?>(null);

    public Task<List<PecosReassignment>> GetReassignmentsAsync(string npi, CancellationToken ct) =>
        Task.FromResult(new List<PecosReassignment>());

    public Task<BulkSyncResult> SyncEnrollmentDataAsync(CancellationToken ct) =>
        Task.FromResult(new BulkSyncResult { Source = "PECOS" });
}

internal class NullOpenPaymentsAdapter : IOpenPaymentsAdapter
{
    public Task<OpenPaymentsSummary?> GetPaymentSummaryAsync(string npi, int? programYear, CancellationToken ct) =>
        Task.FromResult<OpenPaymentsSummary?>(null);

    public Task<BulkSyncResult> SyncPaymentDataAsync(int programYear, CancellationToken ct) =>
        Task.FromResult(new BulkSyncResult { Source = "OpenPayments" });
}

internal class NullUtilizationAdapter : IMedicareUtilizationAdapter
{
    public Task<MedicareUtilizationProfile?> GetUtilizationProfileAsync(string npi, int? calendarYear, CancellationToken ct) =>
        Task.FromResult<MedicareUtilizationProfile?>(null);

    public Task<PartDPrescribingSummary?> GetPartDProfileAsync(string npi, int? calendarYear, CancellationToken ct) =>
        Task.FromResult<PartDPrescribingSummary?>(null);

    public Task<BulkSyncResult> SyncUtilizationDataAsync(int calendarYear, CancellationToken ct) =>
        Task.FromResult(new BulkSyncResult { Source = "MedicareUtilization" });
}

internal class NullFsmbAdapter : IFsmbAdapter
{
    public bool IsConfigured => false;

    public Task<FsmbLicenseVerification?> VerifyProviderAsync(string npi, CancellationToken ct) =>
        Task.FromResult<FsmbLicenseVerification?>(null);

    public Task<List<StateLicense>> GetLicensesAsync(string npi, CancellationToken ct) =>
        Task.FromResult(new List<StateLicense>());

    public Task<List<DisciplinaryAction>> GetDisciplinaryActionsAsync(string npi, CancellationToken ct) =>
        Task.FromResult(new List<DisciplinaryAction>());
}
