using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.BenchmarkClaimGenerator;
using CloudHealthOffice.BenchmarkClaimGenerator.Configuration;
using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using CloudHealthOffice.BenchmarkClaimGenerator.Output;
using CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;
using CloudHealthOffice.Tools.MccPlatformValidator;

var options = ValidatorOptions.Parse(args);
if (options.ShowHelp)
{
    PrintUsage();
    return;
}

var runId = Guid.NewGuid().ToString("N");
var runStartedAtUtc = DateTimeOffset.UtcNow;
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
};

using var http = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
};
http.DefaultRequestHeaders.Add("X-Tenant-ID", options.TenantId);
http.DefaultRequestHeaders.Add("X-Correlation-Id", $"mcc-validation-{Guid.NewGuid():N}");

Console.WriteLine("Million Claim Challenge - Platform Validator");
Console.WriteLine($"  Tenant:      {options.TenantId}");
Console.WriteLine($"  Claims URL:  {options.ClaimsUrl}");
Console.WriteLine($"  Benefit URL: {options.BenefitUrl}");
Console.WriteLine($"  Member URL:  {options.MemberUrl}");
Console.WriteLine($"  Coverage URL:{options.CoverageUrl}");
Console.WriteLine($"  Provider URL:{options.ProviderUrl}");
Console.WriteLine($"  Claims:      {options.Claims:N0}");
Console.WriteLine($"  Seed:        {options.Seed}");
Console.WriteLine($"  Parallelism: {options.Parallelism:N0}");
Console.WriteLine($"  LOB:         {LineOfBusinessName(options.LineOfBusiness)} ({options.LineOfBusiness})");
Console.WriteLine($"  PA scenarios:{(options.PriorAuthScenariosEnabled ? $" enabled ({options.PriorAuthScenarioRate:P0})" : " disabled")}");
Console.WriteLine($"  Pend observe:{(options.PendObservationEnabled ? $" enabled ({options.PendObservationTimeoutSeconds}s/{options.PendObservationIntervalMilliseconds}ms)" : " disabled")}");
Console.WriteLine($"  Pend diag:   {(options.PendDiagnosticsPath is not null ? $"enabled -> {options.PendDiagnosticsPath}" : "disabled")}");
Console.WriteLine();

var lifecycleTimings = new List<MassAdjudicationLifecycleTiming>();

await MeasureLifecyclePhaseAsync(lifecycleTimings, "Service health checks", "Preparation", async () =>
{
    await RequireHealthyAsync(http, $"{options.ClaimsUrl}/health", "claims-service");
    await RequireHealthyAsync(http, $"{options.BenefitUrl}/health", "benefit-plan-service");
    if (options.SeedMembers)
    {
        await RequireHealthyAsync(http, $"{options.MemberUrl}/health", "member-service");
        await RequireHealthyAsync(http, $"{options.CoverageUrl}/health", "coverage-service");
    }
    if (options.SeedProviders)
    {
        await RequireHealthyAsync(http, $"{options.ProviderUrl}/health", "provider-service");
    }
});

await MeasureLifecyclePhaseAsync(lifecycleTimings, "Reference rule seeding", "Preparation", async () =>
{
    await SeedNcciAsync(http, options, json);
    await SeedPriorAuthRulesAsync(http, options, json);
});

var validationPlanId = Guid.NewGuid();
await MeasureLifecyclePhaseAsync(lifecycleTimings, "Validation plan setup", "Preparation", async () =>
{
    await CreateValidationPlanAsync(http, options, validationPlanId, json);
});

var claims = await MeasureLifecycleValuePhaseAsync(
    lifecycleTimings,
    "Corpus generation",
    "Preparation",
    () => GenerateClaimsAsync(options));

var providerPool = await MeasureLifecycleValuePhaseAsync(lifecycleTimings, "Fixture normalization", "Preparation", () =>
{
    NormalizePriorAuthEdgeCases(claims, options);
    NormalizeValidationProviderProfiles(claims, options.Seed, validationPlanId);
    MccFixtureIsolation.IsolateValidationMembers(claims, options.Seed, validationPlanId);
    MccCleanPaidFixture.NormalizeClaims(claims);
    return Task.FromResult(MccProviderFixturePool.Apply(claims, options.Seed, validationPlanId));
});
Console.WriteLine($"Generated {claims.Count:N0} MCC claims in memory");
Console.WriteLine(
    $"Provider fixture pool: {providerPool.ProvidersBefore:N0} -> {providerPool.ProvidersAfter:N0} distinct NPIs " +
    $"({providerPool.ReusedAssignments:N0} assignments reused, {providerPool.ProtectedClaims:N0} provider-sensitive claims preserved)");
var answerKey = MccAnswerKey.FromClaims(claims);

var memberFixtures = MemberFixturePreparation.Empty;
var cobCoverageFixtures = FixtureCount.Empty;
if (options.SeedMembers)
{
    (memberFixtures, cobCoverageFixtures) = await MeasureLifecycleValuePhaseAsync(
        lifecycleTimings,
        "Member and coverage seeding",
        "Preparation",
        async () =>
    {
        var members = await SeedMembersAsync(http, options, claims, json);
        var coverage = await SeedCoverageAsync(http, options, claims, validationPlanId, json);
        return (members, coverage);
    });
}

var providerNetworkFixtures = FixtureCount.Empty;
var providerFixtures = FixtureCount.Empty;
if (options.SeedProviders)
{
    (providerNetworkFixtures, providerFixtures) = await MeasureLifecycleValuePhaseAsync(
        lifecycleTimings,
        "Provider seeding",
        "Preparation",
        async () =>
    {
        var networks = await SeedProviderNetworksAsync(http, options, json);
        var providers = await SeedProvidersAsync(http, options, claims, validationPlanId, json);
        return (networks, providers);
    });
}

var fixturePreparation = new MassAdjudicationFixturePreparation(
    claims.Count,
    providerPool.ProvidersBefore,
    providerPool.ProvidersAfter,
    providerPool.ReusedAssignments,
    providerPool.ProtectedClaims,
    memberFixtures.Created,
    memberFixtures.Existing,
    memberFixtures.StatusAligned,
    cobCoverageFixtures.Created,
    cobCoverageFixtures.Existing,
    providerNetworkFixtures.Created,
    providerNetworkFixtures.Existing,
    providerFixtures.Created,
    providerFixtures.Existing);

var results = new ConcurrentBag<ClaimValidationResult>();
var total = new Stopwatch();
var completed = 0;
var platformFailures = 0;
var lastProgressPublishTicks = 0L;
var progressLock = new object();
var progressPublishGate = new SemaphoreSlim(1, 1);
Task? latestProgressPublishTask = null;
var latestProgressPublishLock = new object();

if (!options.NoPublishSummary)
{
    var initialProgress = BuildProgressSummary(
        results.ToList(),
        TimeSpan.Zero,
        options,
        runId,
        runStartedAtUtc,
        "Running",
        "Processing claims",
        fixturePreparation);
    await PublishSummaryAsync(http, options, initialProgress, json, quiet: true);
}

var processingStartedAtUtc = DateTimeOffset.UtcNow;
total.Start();
await Parallel.ForEachAsync(
    claims,
    new ParallelOptions { MaxDegreeOfParallelism = options.Parallelism },
    async (claim, _) =>
    {
        var result = await ProcessClaimAsync(http, options, claim, validationPlanId, answerKey, json);
        results.Add(result);

        var done = Interlocked.Increment(ref completed);
        if (result.Outcome is ClaimValidationOutcome.PlatformFailure)
        {
            Interlocked.Increment(ref platformFailures);
        }

        if (done % options.ProgressEvery == 0 || done == claims.Count)
        {
            lock (progressLock)
            {
                var currentDone = Volatile.Read(ref completed);
                var failures = Math.Min(Volatile.Read(ref platformFailures), currentDone);
                Console.Write($"\r  Processed: {currentDone:N0}/{claims.Count:N0}  processed={currentDone - failures:N0}  platformFailures={failures:N0}");
            }

            if (!options.NoPublishSummary
                && ShouldPublishProgress(done, claims.Count, ref lastProgressPublishTicks))
            {
                if (progressPublishGate.Wait(0))
                {
                    try
                    {
                        var progressSummary = BuildProgressSummary(
                            results.ToList(),
                            total.Elapsed,
                            options,
                            runId,
                            runStartedAtUtc,
                            "Running",
                            "Processing claims",
                            fixturePreparation);
                        var publishTask = PublishProgressSummaryAsync(progressPublishGate, http, options, progressSummary, json);
                        lock (latestProgressPublishLock)
                        {
                            latestProgressPublishTask = publishTask;
                        }
                    }
                    catch
                    {
                        progressPublishGate.Release();
                        throw;
                    }
                }
            }
        }
    });

total.Stop();
AddLifecycleTiming(lifecycleTimings, "Timed adjudication", "Processing", processingStartedAtUtc, DateTimeOffset.UtcNow, total.Elapsed);
Task? pendingProgressPublish;
lock (latestProgressPublishLock)
{
    pendingProgressPublish = latestProgressPublishTask;
}

if (pendingProgressPublish is not null)
{
    await pendingProgressPublish;
}
Console.WriteLine();
Console.WriteLine();

var orderedResults = results
    .OrderBy(r => r.GeneratedClaimId, StringComparer.Ordinal)
    .ToList();

if (options.PendObservationEnabled)
{
    await MeasureLifecyclePhaseAsync(lifecycleTimings, "Expected-pend observation", "Observation", async () =>
    {
        orderedResults = await ObserveExpectedPendResultsAsync(http, options, orderedResults);
    });
    await MeasureLifecyclePhaseAsync(lifecycleTimings, "False-pend sweep", "Observation", async () =>
    {
        orderedResults = await DetectUnexpectedPendResultsAsync(http, options, orderedResults);
    });
}

if (!string.IsNullOrWhiteSpace(options.PendDiagnosticsPath))
{
    await MeasureLifecyclePhaseAsync(lifecycleTimings, "Pend diagnostics", "Diagnostics", async () =>
    {
        var diagnosticsReport = await PendDiagnostics.CollectAsync(http, options, orderedResults);
        await PendDiagnostics.WriteReportAsync(options.PendDiagnosticsPath, diagnosticsReport, json);
        PendDiagnostics.PrintAggregateTable(diagnosticsReport);
    });
}

var summary = MccRunSummaryBuilder.Build(
    orderedResults,
    total.Elapsed,
    options,
    runStartedAtUtc,
    DateTimeOffset.UtcNow,
    runId,
    "Completed",
    options.Claims,
    CreateProgress(orderedResults, total.Elapsed, options, "Completed"),
    publishClaimResults: true,
    lifecycleTimings: lifecycleTimings,
    fixturePreparation: fixturePreparation);
WriteSummary(summary);

if (!string.IsNullOrWhiteSpace(options.SummaryJsonPath))
{
    await WriteSummaryJsonAsync(options.SummaryJsonPath, summary, json);
}

if (!options.NoPublishSummary)
{
    await PublishSummaryAsync(http, options, summary, json);
}

if (orderedResults.Any(r => r.Outcome is ClaimValidationOutcome.PlatformFailure))
{
    Environment.ExitCode = 1;
}

static async Task MeasureLifecyclePhaseAsync(
    ICollection<MassAdjudicationLifecycleTiming> timings,
    string label,
    string category,
    Func<Task> action)
{
    await MeasureLifecycleValuePhaseAsync(timings, label, category, async () =>
    {
        await action();
        return true;
    });
}

static async Task<T> MeasureLifecycleValuePhaseAsync<T>(
    ICollection<MassAdjudicationLifecycleTiming> timings,
    string label,
    string category,
    Func<Task<T>> action)
{
    var startedAtUtc = DateTimeOffset.UtcNow;
    var sw = Stopwatch.StartNew();
    try
    {
        return await action();
    }
    finally
    {
        sw.Stop();
        AddLifecycleTiming(timings, label, category, startedAtUtc, DateTimeOffset.UtcNow, sw.Elapsed);
    }
}

static void AddLifecycleTiming(
    ICollection<MassAdjudicationLifecycleTiming> timings,
    string label,
    string category,
    DateTimeOffset startedAtUtc,
    DateTimeOffset completedAtUtc,
    TimeSpan duration)
{
    timings.Add(new MassAdjudicationLifecycleTiming(
        label,
        category,
        Math.Max(0, duration.TotalMilliseconds),
        startedAtUtc,
        completedAtUtc));
}

static async Task<ClaimValidationResult> ProcessClaimAsync(
    HttpClient http,
    ValidatorOptions options,
    SyntheticClaim claim,
    Guid validationPlanId,
    MccAnswerKey answerKey,
    JsonSerializerOptions json)
{
    var sw = Stopwatch.StartNew();
    var submitElapsed = TimeSpan.Zero;
    var adjudicationElapsed = TimeSpan.Zero;
    var updateElapsed = TimeSpan.Zero;
    var failureStage = "unknown";
    var expectedValidation = answerKey.ExpectedValidationFor(claim);
    var expectedPlanPayment = expectedValidation.ExpectedOutcome is not ClaimValidationOutcome.Paid
        ? null
        : claim.ExpectedOutcome?.ExpectedPaidAmount;

    try
    {
        var networkTier = NetworkTier(claim);
        failureStage = "submit";
        var stage = Stopwatch.StartNew();
        var submitted = await SubmitClaimAsync(http, options, claim, validationPlanId, json);
        stage.Stop();
        submitElapsed = stage.Elapsed;

        failureStage = "adjudicate";
        stage.Restart();
        var (adjudicated, adjudicationRawBody) = await AdjudicateClaimAsync(http, options, claim, submitted.Id, validationPlanId, networkTier, json);
        stage.Stop();
        adjudicationElapsed = stage.Elapsed;

        var skipSynchronousWriteback = options.SkipClaimUpdate
            || expectedValidation.ExpectedOutcome is ClaimValidationOutcome.Pended;
        var outcome = adjudicated.Success
            ? ClaimValidationOutcome.Paid
            : ClaimValidationOutcome.BusinessDenial;

        if (!skipSynchronousWriteback)
        {
            failureStage = "writeback";
            stage.Restart();
            var writebackOutcome = await UpdateClaimAdjudicationAsync(http, options, submitted.Id, networkTier, adjudicated, json);
            if (writebackOutcome is not null)
            {
                outcome = writebackOutcome.Value;
            }
            stage.Stop();
            updateElapsed = stage.Elapsed;
        }

        sw.Stop();
        var businessDenialCode = NormalizeBusinessDenialCode(adjudicated.BusinessDenialCode
            ?? adjudicated.DenialReasonCode
            ?? (outcome is ClaimValidationOutcome.BusinessDenial ? "ADJUDICATION_DENIAL" : null));

        // Diagnostics-only capture (Deliverable 1): only retained when --pend-diagnostics
        // is on, and only for expected-pend claims or the NCCI/MUE denial sample — never
        // parsed on the hot path otherwise, so benchmark timing is unaffected when off.
        JsonElement? syncAdjudicationSnapshot = null;
        if (!string.IsNullOrWhiteSpace(options.PendDiagnosticsPath)
            && (expectedValidation.ExpectedOutcome is ClaimValidationOutcome.Pended
                || string.Equals(businessDenialCode, PendDiagnostics.NcciMueDenialCode, StringComparison.Ordinal)))
        {
            using var rawDocument = JsonDocument.Parse(adjudicationRawBody);
            syncAdjudicationSnapshot = rawDocument.RootElement.Clone();
        }

        return new ClaimValidationResult(
            claim.ClaimId,
            submitted.Id,
            claim.ClaimType,
            expectedValidation.Scenario,
            expectedValidation.ExpectedOutcome?.ToString(),
            expectedValidation.ExpectedBusinessDenialCode,
            MccWorkflowValidation.ValidationStatus(expectedValidation, outcome, businessDenialCode),
            outcome,
            adjudicated.Success,
            adjudicated.Totals.PlanPayment,
            expectedPlanPayment,
            sw.Elapsed,
            submitElapsed,
            adjudicationElapsed,
            updateElapsed,
            adjudicated.Timings ?? new Dictionary<string, double>(),
            businessDenialCode,
            null,
            null,
            syncAdjudicationSnapshot);
    }
    catch (Exception ex)
    {
        sw.Stop();
        return new ClaimValidationResult(
            claim.ClaimId,
            null,
            claim.ClaimType,
            expectedValidation.Scenario,
            expectedValidation.ExpectedOutcome?.ToString(),
            expectedValidation.ExpectedBusinessDenialCode,
            MccWorkflowValidation.ValidationStatus(expectedValidation, ClaimValidationOutcome.PlatformFailure, null),
            ClaimValidationOutcome.PlatformFailure,
            false,
            null,
            expectedPlanPayment,
            sw.Elapsed,
            submitElapsed,
            adjudicationElapsed,
            updateElapsed,
            new Dictionary<string, double>(),
            null,
            failureStage,
            ex.Message);
    }
}

static async Task RequireHealthyAsync(HttpClient http, string url, string serviceName)
{
    using var response = await http.GetAsync(url);
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"{serviceName} health check failed: {(int)response.StatusCode} {response.ReasonPhrase}");
    }

    Console.WriteLine($"healthy: {serviceName}");
}

static async Task SeedNcciAsync(HttpClient http, ValidatorOptions options, JsonSerializerOptions json)
{
    using var response = await http.PostAsync($"{options.BenefitUrl}/api/v1/ncci/seed", JsonContent.Create(new { }, options: json));
    if (!response.IsSuccessStatusCode)
    {
        var body = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException($"NCCI seed failed: {(int)response.StatusCode} {body}");
    }

    Console.WriteLine("seeded: NCCI baseline");
}

static async Task SeedPriorAuthRulesAsync(HttpClient http, ValidatorOptions options, JsonSerializerOptions json)
{
    using var response = await http.PostAsync($"{options.BenefitUrl}/api/v1/prior-auth-rules/seed-platform", JsonContent.Create(new { }, options: json));
    if (!response.IsSuccessStatusCode)
    {
        var body = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException($"prior-auth rule seed failed: {(int)response.StatusCode} {body}");
    }

    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var seeded = document.RootElement.TryGetProperty("seededRules", out var seededElement)
        ? seededElement.GetInt32()
        : 0;

    Console.WriteLine($"seeded: prior-auth platform rules ({seeded:N0} new)");
}

static async Task CreateValidationPlanAsync(HttpClient http, ValidatorOptions options, Guid planGuid, JsonSerializerOptions json)
{
    var plan = new
    {
        id = planGuid.ToString(),
        tenantId = options.TenantId,
        planId = $"MCC-LOCAL-{DateTime.UtcNow:yyyyMMddHHmmss}",
        planName = "MCC Local Validation PPO",
        payer = "Cloud Health Office MCC",
        effectiveDate = "2025-01-01T00:00:00Z",
        planType = "PPO",
        lineOfBusiness = LineOfBusinessName(options.LineOfBusiness),
        isActive = true,
        networkTiers = new[]
        {
            new { tierName = "In-Network", tierLevel = 1, networkId = "mcc-local-network" },
            new { tierName = "Out-of-Network", tierLevel = 2, networkId = "mcc-local-out-network" }
        },
        costSharing = new
        {
            individualDeductible = 1500.00m,
            familyDeductible = 3000.00m,
            individualOutOfPocketMax = 5000.00m,
            familyOutOfPocketMax = 10000.00m,
            inNetworkDeductible = 1500.00m,
            outOfNetworkDeductible = 3000.00m,
            inNetworkOutOfPocketMax = 5000.00m,
            outOfNetworkOutOfPocketMax = 10000.00m
        },
        benefits = new object[]
        {
            new
            {
                benefitType = "medical",
                serviceCategory = "98",
                description = "Professional Office Visit",
                cptCodes = new[] { "99202", "99203", "99204", "99205", "99211", "99212", "99213", "99214", "99215" },
                inNetworkCopay = 30.00m,
                deductibleApplies = false,
                oopApplies = true,
                priorAuthRequired = false
            },
            new
            {
                benefitType = "medical",
                serviceCategory = "73",
                description = "Diagnostic Lab",
                cptCodes = new[] { "80048", "80053", "85025", "36415" },
                inNetworkCoinsurance = 0.20m,
                deductibleApplies = true,
                oopApplies = true,
                priorAuthRequired = false
            },
            new
            {
                benefitType = "medical",
                serviceCategory = "48",
                description = "Hospital Inpatient",
                cptCodes = Array.Empty<string>(),
                inNetworkCoinsurance = 0.20m,
                deductibleApplies = true,
                oopApplies = true,
                priorAuthRequired = true
            }
        }
    };

    using var response = await http.PostAsJsonAsync($"{options.BenefitUrl}/api/v1/plans", plan, json);
    if (!response.IsSuccessStatusCode)
    {
        var body = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException($"Validation plan creation failed: {(int)response.StatusCode} {body}");
    }

    Console.WriteLine($"created: validation benefit plan {planGuid}");
}

static async Task<List<SyntheticClaim>> GenerateClaimsAsync(ValidatorOptions options)
{
    var profile = BuildCorpusProfile(options.Claims, options.Seed);
    var writer = new InMemoryCorpusWriter();
    var generator = new ClaimCorpusGenerator(new InMemoryReferenceDataProvider());

    await using (writer)
    {
        await generator.GenerateCorpusAsync(profile, writer);
    }

    var claims = writer.Claims
        .OrderBy(c => c.ClaimId, StringComparer.Ordinal)
        .ToList();
    MccClaimDateNormalizer.NormalizeClaimDates(claims, options.Seed);
    InjectCleanPaidScenarios(claims);
    InjectExcludedProviderScenarios(claims, options.Seed);
    InjectUncoveredServiceScenarios(claims);
    InjectPriorAuthScenarios(claims, options);
    return claims;
}

static void NormalizeValidationProviderProfiles(List<SyntheticClaim> claims, int seed, Guid runId)
{
    var scenarioIndex = 0;
    foreach (var claim in claims.OrderBy(c => c.ClaimId, StringComparer.Ordinal))
    {
        var expected = MccWorkflowValidation.ExpectedValidationFor(claim);
        if (expected.ExpectedOutcome is null)
        {
            continue;
        }

        ForceAdjudicatableProviderProfile(claim.BillingProvider, claim.DateOfService);
        claim.BillingProvider.Npi = BuildSyntheticValidationProviderNpi(seed, runId, scenarioIndex, role: 0);

        if (!string.Equals(
                expected.ExpectedBusinessDenialCode,
                MccWorkflowValidation.ProviderExcludedCode,
                StringComparison.OrdinalIgnoreCase))
        {
            ForceAdjudicatableProviderProfile(claim.RenderingProvider, claim.DateOfService);
            claim.RenderingProvider.Npi = BuildSyntheticValidationProviderNpi(seed, runId, scenarioIndex, role: 1);
        }
        else
        {
            claim.RenderingProvider.Npi = BuildSyntheticValidationProviderNpi(seed, runId, scenarioIndex, role: 2);
        }

        scenarioIndex++;
    }
}

static void InjectCleanPaidScenarios(List<SyntheticClaim> claims)
{
    var candidates = claims
        .Where(c => c.ClaimType.Equals("Professional", StringComparison.OrdinalIgnoreCase))
        .OrderBy(c => c.ClaimId, StringComparer.Ordinal)
        .ToList();

    if (candidates.Count == 0)
    {
        Console.WriteLine("Clean paid scenarios: skipped (no professional claims generated)");
        return;
    }

    var requested = Math.Max(1, (int)Math.Round(claims.Count * 0.02, MidpointRounding.AwayFromZero));
    var injected = 0;

    foreach (var claim in candidates.Take(Math.Min(requested, candidates.Count)))
    {
        ForceCleanProfessionalPaidScenario(claim);
        injected++;
    }

    Console.WriteLine($"Clean paid scenarios: injected {injected:N0} professional claims expected to pay");
}

static void InjectExcludedProviderScenarios(List<SyntheticClaim> claims, int seed)
{
    var candidates = claims
        .Where(c =>
            c.ClaimType.Equals("Professional", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(c.BenefitPlanId, MccWorkflowValidation.CleanProfessionalPaidPlanId, StringComparison.Ordinal))
        .OrderBy(c => c.ClaimId, StringComparer.Ordinal)
        .ToList();

    if (candidates.Count == 0)
    {
        Console.WriteLine("Excluded provider scenarios: skipped (no remaining professional claims generated)");
        return;
    }

    var requested = Math.Max(1, (int)Math.Round(claims.Count * 0.02, MidpointRounding.AwayFromZero));
    var injected = 0;

    foreach (var claim in candidates.Take(Math.Min(requested, candidates.Count)))
    {
        ForceExcludedProviderScenario(claim, seed, injected);
        injected++;
    }

    Console.WriteLine($"Excluded provider scenarios: injected {injected:N0} professional claims expected to deny");
}

static void InjectUncoveredServiceScenarios(List<SyntheticClaim> claims)
{
    var candidates = claims
        .Where(c =>
            c.ClaimType.Equals("Professional", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(c.BenefitPlanId, MccWorkflowValidation.CleanProfessionalPaidPlanId, StringComparison.Ordinal)
            && !string.Equals(c.BenefitPlanId, MccWorkflowValidation.ExcludedProviderPlanId, StringComparison.Ordinal))
        .OrderBy(c => c.ClaimId, StringComparer.Ordinal)
        .ToList();

    if (candidates.Count == 0)
    {
        Console.WriteLine("Uncovered service scenarios: skipped (no remaining professional claims generated)");
        return;
    }

    var requested = Math.Max(1, (int)Math.Round(claims.Count * 0.02, MidpointRounding.AwayFromZero));
    var injected = 0;

    foreach (var claim in candidates.Take(Math.Min(requested, candidates.Count)))
    {
        ForceUncoveredServiceScenario(claim);
        injected++;
    }

    Console.WriteLine($"Uncovered service scenarios: injected {injected:N0} professional claims expected to deny");
}

static void InjectPriorAuthScenarios(List<SyntheticClaim> claims, ValidatorOptions options)
{
    if (!options.PriorAuthScenariosEnabled)
    {
        return;
    }

    if (options.LineOfBusiness is not (3 or 4))
    {
        Console.WriteLine("PA scenarios: skipped (TX Medicaid/CHIP rule scenarios require --line-of-business 3 or 4)");
        return;
    }

    var candidates = claims
        .Where(c => c.ClaimType.Equals("Institutional", StringComparison.OrdinalIgnoreCase))
        .OrderBy(c => c.ClaimId, StringComparer.Ordinal)
        .ToList();

    if (candidates.Count == 0)
    {
        Console.WriteLine("PA scenarios: skipped (no institutional claims generated)");
        return;
    }

    var requested = Math.Max(0, (int)Math.Round(claims.Count * options.PriorAuthScenarioRate, MidpointRounding.AwayFromZero));
    var injected = 0;

    foreach (var claim in candidates.Take(Math.Min(requested, candidates.Count)))
    {
        ForceTexasMedicaidInpatientPriorAuthScenario(claim);
        injected++;
    }

    Console.WriteLine($"PA scenarios: injected {injected:N0} TX STAR inpatient claims without auth");
}

static void NormalizePriorAuthEdgeCases(List<SyntheticClaim> claims, ValidatorOptions options)
{
    if (!options.PriorAuthScenariosEnabled || options.LineOfBusiness is not (3 or 4))
    {
        return;
    }

    foreach (var claim in claims.Where(claim => claim.EdgeCase is EdgeCaseScenario.PriorAuthRequired_NoAuth))
    {
        ForceTexasMedicaidInpatientPriorAuthScenario(claim);
    }
}

static void ForceCleanProfessionalPaidScenario(SyntheticClaim claim)
{
    var serviceDate = claim.DateOfService.Date;
    claim.BenefitPlanId = MccWorkflowValidation.CleanProfessionalPaidPlanId;
    claim.PlaceOfService = "11";
    claim.FrequencyCode = "1";
    claim.BillType = null;
    claim.DrgCode = null;
    claim.PriorAuthStatus = "NotRequired";
    claim.PriorAuthNumber = null;
    claim.PrimaryDiagnosisCode = "Z00.00";
    claim.SecondaryDiagnosisCodes.Clear();
    ForceCleanProfessionalPaidProviderProfile(claim.RenderingProvider, serviceDate);
    ForceCleanProfessionalPaidProviderProfile(claim.BillingProvider, serviceDate);

    claim.Lines = new List<ClaimLine>
    {
        new()
        {
            LineNumber = 1,
            ProcedureCode = "99213",
            Description = "Office/outpatient established patient visit",
            Modifiers = new List<string>(),
            RevenueCode = null,
            DiagnosisPointers = new List<int> { 1 },
            Units = 1,
            ChargeAmount = 180.00m,
            ServiceDate = serviceDate,
            ServiceEndDate = serviceDate,
            PlaceOfService = "11"
        }
    };
    claim.TotalCharges = 180.00m;
    claim.ExpectedOutcome = new ExpectedOutcome
    {
        Disposition = "Paid",
        ExpectedAllowedAmount = 180.00m,
        ExpectedPaidAmount = 150.00m,
        ExpectedMemberLiability = 30.00m,
        ExpectedCopay = 30.00m,
        ExpectedCoinsurance = 0.00m,
        ExpectedDeductible = 0.00m,
        ExpectedFhirCompliant = true,
        ExpectedPriorAuthDecision = "N/A",
        LineOutcomes = new List<LineOutcome>
        {
            new()
            {
                LineNumber = 1,
                Disposition = "Paid",
                AllowedAmount = 180.00m,
                PaidAmount = 150.00m
            }
        }
    };
}

static void ForceExcludedProviderScenario(SyntheticClaim claim, int seed, int index)
{
    var serviceDate = claim.DateOfService.Date;
    claim.BenefitPlanId = MccWorkflowValidation.ExcludedProviderPlanId;
    claim.PlaceOfService = "11";
    claim.FrequencyCode = "1";
    claim.BillType = null;
    claim.DrgCode = null;
    claim.PriorAuthStatus = "NotRequired";
    claim.PriorAuthNumber = null;
    claim.PrimaryDiagnosisCode = "Z00.00";
    claim.SecondaryDiagnosisCodes.Clear();

    ForceCleanProfessionalPaidProviderProfile(claim.RenderingProvider, serviceDate);
    ForceCleanProfessionalPaidProviderProfile(claim.BillingProvider, serviceDate);
    ForceExcludedProviderProfile(claim.RenderingProvider, seed, index);

    claim.Lines = new List<ClaimLine>
    {
        new()
        {
            LineNumber = 1,
            ProcedureCode = "99213",
            Description = "Office/outpatient established patient visit",
            Modifiers = new List<string>(),
            RevenueCode = null,
            DiagnosisPointers = new List<int> { 1 },
            Units = 1,
            ChargeAmount = 180.00m,
            ServiceDate = serviceDate,
            ServiceEndDate = serviceDate,
            PlaceOfService = "11"
        }
    };
    claim.TotalCharges = 180.00m;
    claim.ExpectedOutcome = new ExpectedOutcome
    {
        Disposition = "Denied",
        DenialReasonCode = MccWorkflowValidation.ProviderExcludedCode,
        ExpectedAllowedAmount = 0.00m,
        ExpectedPaidAmount = 0.00m,
        ExpectedMemberLiability = 0.00m,
        ExpectedCopay = 0.00m,
        ExpectedCoinsurance = 0.00m,
        ExpectedDeductible = 0.00m,
        ExpectedFhirCompliant = true,
        ExpectedPriorAuthDecision = "N/A",
        LineOutcomes = new List<LineOutcome>
        {
            new()
            {
                LineNumber = 1,
                Disposition = "Denied",
                AllowedAmount = 0.00m,
                PaidAmount = 0.00m,
                ReasonCode = MccWorkflowValidation.ProviderExcludedCode
            }
        }
    };
}

static void ForceUncoveredServiceScenario(SyntheticClaim claim)
{
    var serviceDate = claim.DateOfService.Date;
    claim.BenefitPlanId = MccWorkflowValidation.UncoveredServicePlanId;
    claim.PlaceOfService = "31";
    claim.FrequencyCode = "1";
    claim.BillType = null;
    claim.DrgCode = null;
    claim.PriorAuthStatus = "NotRequired";
    claim.PriorAuthNumber = null;
    claim.PrimaryDiagnosisCode = "Z00.00";
    claim.SecondaryDiagnosisCodes.Clear();

    ForceCleanProfessionalPaidProviderProfile(claim.RenderingProvider, serviceDate);
    ForceCleanProfessionalPaidProviderProfile(claim.BillingProvider, serviceDate);

    claim.Lines = new List<ClaimLine>
    {
        new()
        {
            LineNumber = 1,
            ProcedureCode = "99283",
            Description = "Emergency department visit, moderate severity",
            Modifiers = new List<string>(),
            RevenueCode = null,
            DiagnosisPointers = new List<int> { 1 },
            Units = 1,
            ChargeAmount = 850.00m,
            ServiceDate = serviceDate,
            ServiceEndDate = serviceDate,
            PlaceOfService = "31"
        }
    };
    claim.TotalCharges = 850.00m;
    claim.ExpectedOutcome = new ExpectedOutcome
    {
        Disposition = "Denied",
        DenialReasonCode = MccWorkflowValidation.UncoveredServiceCode,
        ExpectedAllowedAmount = 0.00m,
        ExpectedPaidAmount = 0.00m,
        ExpectedMemberLiability = 0.00m,
        ExpectedCopay = 0.00m,
        ExpectedCoinsurance = 0.00m,
        ExpectedDeductible = 0.00m,
        ExpectedFhirCompliant = true,
        ExpectedPriorAuthDecision = "N/A",
        LineOutcomes = new List<LineOutcome>
        {
            new()
            {
                LineNumber = 1,
                Disposition = "Denied",
                AllowedAmount = 0.00m,
                PaidAmount = 0.00m,
                ReasonCode = MccWorkflowValidation.UncoveredServiceCode
            }
        }
    };
}

static void ForceCleanProfessionalPaidProviderProfile(SyntheticProvider provider, DateTime serviceDate)
{
    MccValidationProviderProfile.ForceCleanProfessionalPaid(provider, serviceDate);
}

static void ForceAdjudicatableProviderProfile(SyntheticProvider provider, DateTime serviceDate)
{
    MccValidationProviderProfile.ForceAdjudicatable(provider, serviceDate);
}

static void ForceExcludedProviderProfile(SyntheticProvider provider, int seed, int index)
{
    provider.Npi = BuildSyntheticExcludedProviderNpi(seed, index);
    provider.FirstName = "Excluded";
    provider.LastName = $"Provider{index + 1:D2}";
    provider.CredentialingStatus = "Excluded";
    provider.NetworkStatus = "Excluded";
    provider.IsParticipating = true;
    provider.AcceptingNewPatients = false;
}

static string BuildSyntheticExcludedProviderNpi(int seed, int index)
{
    unchecked
    {
        var combined = (uint)(seed * 1_000_003 + index);
        var value = combined % 1_000_000;
        var baseNineDigits = $"900{value:D6}";
        return $"{baseNineDigits}{CalculateNpiCheckDigit(baseNineDigits)}";
    }
}

static string BuildSyntheticValidationProviderNpi(int seed, Guid runId, int index, int role)
{
    return MccValidationProviderIdentity.BuildNpi(seed, runId, index, role);
}

static int CalculateNpiCheckDigit(string baseNineDigits)
{
    const string npiPrefix = "80840";
    var candidate = $"{npiPrefix}{baseNineDigits}0";
    var sum = 0;
    var doubleDigit = false;

    for (var i = candidate.Length - 1; i >= 0; i--)
    {
        var digit = candidate[i] - '0';
        if (doubleDigit)
        {
            digit *= 2;
            if (digit > 9)
            {
                digit -= 9;
            }
        }

        sum += digit;
        doubleDigit = !doubleDigit;
    }

    return (10 - (sum % 10)) % 10;
}

static async Task<MemberFixturePreparation> SeedMembersAsync(
    HttpClient http,
    ValidatorOptions options,
    IReadOnlyCollection<SyntheticClaim> claims,
    JsonSerializerOptions json)
{
    var members = claims
        .Select(claim => claim.Member)
        .Where(member => !string.IsNullOrWhiteSpace(member.MemberId))
        .GroupBy(member => member.MemberId, StringComparer.Ordinal)
        .Select(group => group.First())
        .OrderBy(member => member.MemberId, StringComparer.Ordinal)
        .ToList();

    var created = 0;
    var existing = 0;
    var statusAligned = 0;

    foreach (var member in members)
    {
        if (await MemberExistsAsync(http, options, member.MemberId))
        {
            existing++;
            if (await UpdateMemberSeedStatusAsync(http, options, member, json))
            {
                statusAligned++;
            }
            continue;
        }

        var didCreate = await CreateMemberAsync(http, options, member, json);
        if (didCreate)
        {
            created++;
        }
        else
        {
            existing++;
        }

        if (await UpdateMemberSeedStatusAsync(http, options, member, json))
        {
            statusAligned++;
        }
    }

    Console.WriteLine($"seeded: {created:N0} synthetic members ({existing:N0} already present, {statusAligned:N0} status-aligned)");
    return new MemberFixturePreparation(created, existing, statusAligned);
}

static async Task<bool> MemberExistsAsync(HttpClient http, ValidatorOptions options, string memberId)
{
    using var response = await http.GetAsync($"{options.MemberUrl}/api/v1/members/{Uri.EscapeDataString(memberId)}");
    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        return false;
    }

    if (response.IsSuccessStatusCode)
    {
        return true;
    }

    var body = await response.Content.ReadAsStringAsync();
    throw new InvalidOperationException($"member lookup failed ({memberId}): {(int)response.StatusCode} {body}");
}

static async Task<bool> CreateMemberAsync(
    HttpClient http,
    ValidatorOptions options,
    SyntheticMember member,
    JsonSerializerOptions json)
{
    var effectiveDate = DateTime.SpecifyKind(member.CoverageEffectiveDate == default
        ? DateTime.UtcNow.Date.AddYears(-1)
        : member.CoverageEffectiveDate.Date, DateTimeKind.Utc);

    var payload = new
    {
        memberId = member.MemberId,
        ssn = member.SSN,
        groupNumber = NullIfWhiteSpace(member.GroupNumber) ?? "MCC-GRP-001",
        isSubscriber = member.IsSubscriber,
        subscriberMemberId = (string?)null,
        relationshipCode = NullIfWhiteSpace(member.RelationshipCode) ?? (member.IsSubscriber ? "18" : null),
        firstName = NullIfWhiteSpace(member.FirstName) ?? "MCC",
        lastName = NullIfWhiteSpace(member.LastName) ?? "Member",
        middleName = NullIfWhiteSpace(member.MiddleName),
        dateOfBirth = DateTime.SpecifyKind(member.DateOfBirth.Date, DateTimeKind.Utc),
        gender = NullIfWhiteSpace(member.Gender),
        address = NullIfWhiteSpace(member.Address),
        city = NullIfWhiteSpace(member.City),
        state = NullIfWhiteSpace(member.State),
        zipCode = NullIfWhiteSpace(member.ZipCode),
        phone = NullIfWhiteSpace(member.Phone),
        email = NullIfWhiteSpace(member.Email),
        effectiveDate,
        terminationDate = member.CoverageTermDate,
        maintenanceTypeCode = NullIfWhiteSpace(member.MaintenanceTypeCode) ?? "021",
        eventId = $"mcc-validator-member-created:{options.Seed}:{member.MemberId}"
    };

    using var response = await http.PostAsJsonAsync($"{options.MemberUrl}/api/v1/members", payload, json);
    var body = await response.Content.ReadAsStringAsync();
    if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
    {
        return false;
    }

    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"member seed failed ({member.MemberId}): {(int)response.StatusCode} {body}");
    }

    return true;
}

static async Task<bool> UpdateMemberSeedStatusAsync(
    HttpClient http,
    ValidatorOptions options,
    SyntheticMember member,
    JsonSerializerOptions json)
{
    var payload = new MemberStatusUpdateDto(
        MccMemberSeedStatus.ToMemberServiceStatus(member.EnrollmentStatus),
        $"mcc-validator-member-status:{options.Seed}:{member.MemberId}");

    using var response = await http.PutAsJsonAsync(
        $"{options.MemberUrl}/api/v1/members/{Uri.EscapeDataString(member.MemberId)}",
        payload,
        json);

    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        return false;
    }

    if (!response.IsSuccessStatusCode)
    {
        var body = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException($"member status alignment failed ({member.MemberId}): {(int)response.StatusCode} {body}");
    }

    return true;
}

static async Task<FixtureCount> SeedCoverageAsync(
    HttpClient http,
    ValidatorOptions options,
    IReadOnlyCollection<SyntheticClaim> claims,
    Guid validationPlanId,
    JsonSerializerOptions json)
{
    var cobClaims = claims
        .Where(IsCobPendScenario)
        .GroupBy(claim => claim.Member.MemberId, StringComparer.Ordinal)
        .Select(group => group.OrderBy(claim => claim.ClaimId, StringComparer.Ordinal).First())
        .OrderBy(claim => claim.Member.MemberId, StringComparer.Ordinal)
        .ToList();

    if (cobClaims.Count == 0)
    {
        Console.WriteLine("seeded: 0 COB coverage rows (no supported COB-pend scenarios)");
        return FixtureCount.Empty;
    }

    var created = 0;
    var existing = 0;
    foreach (var claim in cobClaims)
    {
        if (await CobCoverageExistsAsync(http, options, claim.Member.MemberId))
        {
            existing++;
            continue;
        }

        await CreateCobCoverageAsync(http, options, claim, validationPlanId, json);
        created++;
    }

    Console.WriteLine($"seeded: {created:N0} COB coverage rows ({existing:N0} already present)");
    return new FixtureCount(created, existing);
}

static bool IsCobPendScenario(SyntheticClaim claim)
{
    return claim.EdgeCase is
        EdgeCaseScenario.CobSecondaryPayer or
        EdgeCaseScenario.CobTertiaryPayer or
        EdgeCaseScenario.CobBirthdayRule or
        EdgeCaseScenario.CobGenderRule or
        EdgeCaseScenario.MedicaidDualEligible;
}

static async Task<bool> CobCoverageExistsAsync(HttpClient http, ValidatorOptions options, string memberId)
{
    using var response = await http.GetAsync(
        $"{options.CoverageUrl}/api/v1/coverage/member/{Uri.EscapeDataString(memberId)}/cob");

    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        return false;
    }

    if (response.IsSuccessStatusCode)
    {
        return true;
    }

    var body = await response.Content.ReadAsStringAsync();
    throw new InvalidOperationException($"COB coverage lookup failed ({memberId}): {(int)response.StatusCode} {body}");
}

static async Task CreateCobCoverageAsync(
    HttpClient http,
    ValidatorOptions options,
    SyntheticClaim claim,
    Guid validationPlanId,
    JsonSerializerOptions json)
{
    var member = claim.Member;
    var memberEffectiveDate = member.CoverageEffectiveDate == default
        ? claim.DateOfService.Date.AddYears(-1)
        : member.CoverageEffectiveDate.Date;
    var effectiveDate = DateTime.SpecifyKind(
        memberEffectiveDate <= claim.DateOfService.Date
            ? memberEffectiveDate
            : claim.DateOfService.Date.AddYears(-1),
        DateTimeKind.Utc);

    var isMedicaidDual = claim.EdgeCase is EdgeCaseScenario.MedicaidDualEligible;
    var payload = new
    {
        memberId = member.MemberId,
        groupNumber = NullIfWhiteSpace(member.GroupNumber) ?? "MCC-GRP-001",
        planId = validationPlanId.ToString(),
        coverageLevel = "EMP",
        insuranceLineCode = "HLT",
        effectiveDate,
        terminationDate = (DateTime?)null,
        isCOBRA = false,
        medicareCoverage = isMedicaidDual
            ? new
            {
                medicareBeneficiaryId = $"MCCMED{member.MemberId.Replace("-", "", StringComparison.OrdinalIgnoreCase)}",
                hasPartA = true,
                partAEffectiveDate = effectiveDate,
                hasPartB = true,
                partBEffectiveDate = effectiveDate,
                isPrimaryPayer = true
            }
            : null,
        otherInsurance = isMedicaidDual
            ? null
            : new
            {
                payerName = "MCC Primary Carrier",
                policyNumber = $"MCC-PRIMARY-{member.MemberId}",
                groupNumber = "MCC-OTHER-GRP",
                isPrimaryPayer = true,
                effectiveDate
            },
        maintenanceTypeCode = "021",
        maintenanceReasonCode = "COB"
    };

    using var response = await http.PostAsJsonAsync($"{options.CoverageUrl}/api/v1/coverage", payload, json);
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"COB coverage seed failed ({member.MemberId}): {(int)response.StatusCode} {body}");
    }
}

static void ForceTexasMedicaidInpatientPriorAuthScenario(SyntheticClaim claim)
{
    claim.PlaceOfService = "21";
    claim.BillType = "0111";
    claim.PriorAuthStatus = "Required";
    claim.PriorAuthNumber = null;

    claim.RenderingProvider.State = "TX";
    claim.BillingProvider.State = "TX";

    foreach (var line in claim.Lines)
    {
        line.PlaceOfService = "21";
    }
}

static async Task<FixtureCount> SeedProvidersAsync(
    HttpClient http,
    ValidatorOptions options,
    IReadOnlyCollection<SyntheticClaim> claims,
    Guid validationPlanId,
    JsonSerializerOptions json)
{
    var providers = claims
        .SelectMany(claim => new[] { claim.RenderingProvider, claim.BillingProvider })
        .Where(provider => !string.IsNullOrWhiteSpace(provider.Npi))
        .GroupBy(provider => provider.Npi, StringComparer.Ordinal)
        .Select(group => group.First())
        .OrderBy(provider => provider.Npi, StringComparer.Ordinal)
        .ToList();

    var created = 0;
    var existing = 0;

    foreach (var provider in providers)
    {
        var providerId = await GetProviderIdByNpiAsync(http, options, provider.Npi);
        if (providerId is not null)
        {
            existing++;
        }
        else
        {
            providerId = await CreateProviderAsync(http, options, provider, validationPlanId, json);
            created++;
        }

        if (!IsProviderExcluded(provider))
        {
            await EnsureProviderCredentialingAsync(
                http,
                options,
                providerId,
                EffectiveDateForProvider(provider),
                json);
            await EnsureProviderNetworkParticipationAsync(
                http,
                options,
                providerId,
                provider,
                json);
        }
    }

    Console.WriteLine($"seeded: {created:N0} synthetic providers ({existing:N0} already present)");
    return new FixtureCount(created, existing);
}

static async Task<FixtureCount> SeedProviderNetworksAsync(
    HttpClient http,
    ValidatorOptions options,
    JsonSerializerOptions json)
{
    var networks = new[]
    {
        ("mcc-local-network", "MCC Local In-Network"),
        ("mcc-local-out-network", "MCC Local Out-of-Network")
    };

    var created = 0;
    var existing = 0;
    foreach (var (networkId, name) in networks)
    {
        if (await ProviderNetworkExistsAsync(http, options, networkId))
        {
            existing++;
            continue;
        }

        await CreateProviderNetworkAsync(http, options, networkId, name, json);
        created++;
    }

    Console.WriteLine($"seeded: {created:N0} provider networks ({existing:N0} already present)");
    return new FixtureCount(created, existing);
}

static async Task<bool> ProviderNetworkExistsAsync(HttpClient http, ValidatorOptions options, string networkId)
{
    using var response = await http.GetAsync($"{options.ProviderUrl}/api/v1/networks/{Uri.EscapeDataString(networkId)}");
    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        return false;
    }

    if (response.IsSuccessStatusCode)
    {
        return true;
    }

    var body = await response.Content.ReadAsStringAsync();
    throw new InvalidOperationException($"provider network lookup failed ({networkId}): {(int)response.StatusCode} {body}");
}

static async Task EnsureProviderNetworkParticipationAsync(
    HttpClient http,
    ValidatorOptions options,
    string providerId,
    SyntheticProvider provider,
    JsonSerializerOptions json)
{
    var effectiveDate = EffectiveDateForProvider(provider);
    if (await ProviderHasActiveNetworkParticipationAsync(http, options, provider.Npi, "mcc-local-network", effectiveDate))
    {
        return;
    }

    var payload = new
    {
        planId = (string?)null,
        networkId = "mcc-local-network",
        lineOfBusiness = LineOfBusinessName(options.LineOfBusiness),
        networkTier = "InNetwork",
        effectiveDate,
        terminationDate = (DateTime?)null,
        acceptingNewPatients = true,
        panelLimit = 2500,
        panelAccepted = true,
        acceptedLobs = new[] { LineOfBusinessName(options.LineOfBusiness) },
        minAcceptedAgeYears = (int?)null,
        maxAcceptedAgeYears = (int?)null,
        rates = new
        {
            feeScheduleName = provider.FeeScheduleId ?? "MCC Local Fee Schedule",
            percentOfMedicare = 1.10m,
            pmpm = (decimal?)null,
            caseRate = (decimal?)null
        }
    };

    using var response = await http.PostAsJsonAsync(
        $"{options.ProviderUrl}/api/v1/providers/{Uri.EscapeDataString(providerId)}/network-participations",
        payload,
        json);
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode && response.StatusCode is not System.Net.HttpStatusCode.Conflict)
    {
        throw new InvalidOperationException(
            $"provider network participation seed failed ({providerId}/{provider.Npi}): {(int)response.StatusCode} {body}");
    }
}

static async Task<bool> ProviderHasActiveNetworkParticipationAsync(
    HttpClient http,
    ValidatorOptions options,
    string npi,
    string networkId,
    DateTime effectiveDate)
{
    var url =
        $"{options.ProviderUrl}/api/v1/networks/{Uri.EscapeDataString(networkId)}/members/{Uri.EscapeDataString(npi)}" +
        $"?asOf={Uri.EscapeDataString(effectiveDate.ToString("O"))}";
    using var response = await http.GetAsync(url);
    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        return false;
    }

    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException(
            $"provider network membership lookup failed ({networkId}/{npi}): {(int)response.StatusCode} {body}");
    }

    using var document = JsonDocument.Parse(body);
    return document.RootElement.TryGetProperty("isActiveMember", out var active)
        && active.ValueKind == JsonValueKind.True;
}

static async Task CreateProviderNetworkAsync(
    HttpClient http,
    ValidatorOptions options,
    string networkId,
    string name,
    JsonSerializerOptions json)
{
    var effectiveDate = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddYears(-2), DateTimeKind.Utc);
    var payload = new
    {
        tenantId = options.TenantId,
        id = networkId,
        organizationId = networkId,
        name,
        networkType = "custom",
        lineOfBusiness = LineOfBusinessName(options.LineOfBusiness),
        effectiveDate,
        status = "active",
        identifiers = new[]
        {
            new
            {
                system = "urn:cho:network",
                value = networkId,
                type = "NIIP",
                use = "official"
            }
        }
    };

    using var response = await http.PostAsJsonAsync($"{options.ProviderUrl}/api/v1/networks", payload, json);
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode && response.StatusCode is not System.Net.HttpStatusCode.Conflict)
    {
        throw new InvalidOperationException($"provider network seed failed ({networkId}): {(int)response.StatusCode} {body}");
    }
}

static async Task<string?> GetProviderIdByNpiAsync(HttpClient http, ValidatorOptions options, string npi)
{
    using var response = await http.GetAsync($"{options.ProviderUrl}/api/v1/providers/npi/{Uri.EscapeDataString(npi)}");
    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        return null;
    }

    if (response.IsSuccessStatusCode)
    {
        var successBody = await response.Content.ReadAsStringAsync();
        return ExtractProviderId(successBody)
            ?? throw new InvalidOperationException($"provider lookup returned no provider id ({npi}): {successBody}");
    }

    var failureBody = await response.Content.ReadAsStringAsync();
    throw new InvalidOperationException($"provider lookup failed ({npi}): {(int)response.StatusCode} {failureBody}");
}

static string? ExtractProviderId(string body)
{
    using var document = JsonDocument.Parse(body);
    var root = document.RootElement;
    if (root.TryGetProperty("providerId", out var providerIdElement)
        && !string.IsNullOrWhiteSpace(providerIdElement.GetString()))
    {
        return providerIdElement.GetString();
    }

    return root.TryGetProperty("id", out var idElement)
        ? idElement.GetString()
        : null;
}

static async Task EnsureProviderCredentialingAsync(
    HttpClient http,
    ValidatorOptions options,
    string providerId,
    DateTime credentialingDate,
    JsonSerializerOptions json)
{
    var asOfDate = Uri.EscapeDataString(DateTime.UtcNow.ToString("O"));
    using (var response = await http.GetAsync(
               $"{options.ProviderUrl}/api/v1/providers/{Uri.EscapeDataString(providerId)}/credentialing/status-as-of?asOfDate={asOfDate}"))
    {
        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("status", out var statusElement)
                && string.Equals(statusElement.GetString(), "Approved", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
    }

    var credentialingDateUtc = DateTime.SpecifyKind(credentialingDate.Date, DateTimeKind.Utc);
    var recredentialingDueDateUtc = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddYears(2), DateTimeKind.Utc);
    var payload = new
    {
        status = "approved",
        credentialingDate = credentialingDateUtc,
        recredentialingDueDate = recredentialingDueDateUtc
    };

    using var update = await http.PutAsJsonAsync(
        $"{options.ProviderUrl}/api/v1/providers/{Uri.EscapeDataString(providerId)}/credentialing",
        payload,
        json);
    var updateBody = await update.Content.ReadAsStringAsync();
    if (!update.IsSuccessStatusCode)
    {
        throw new InvalidOperationException(
            $"provider credentialing seed failed ({providerId}): {(int)update.StatusCode} {updateBody}");
    }
}

static async Task<string> CreateProviderAsync(
    HttpClient http,
    ValidatorOptions options,
    SyntheticProvider provider,
    Guid validationPlanId,
    JsonSerializerOptions json)
{
    var isOrganization = provider.ProviderType.Equals("Organization", StringComparison.OrdinalIgnoreCase);
    var effectiveDate = EffectiveDateForProvider(provider);
    var now = DateTimeOffset.UtcNow;
    var isProviderExcluded = IsProviderExcluded(provider);

    var payload = new
    {
        tenantId = options.TenantId,
        npi = provider.Npi,
        providerType = isOrganization ? "organization" : "individual",
        taxId = provider.TaxId,
        firstName = isOrganization ? null : NullIfWhiteSpace(provider.FirstName) ?? "MCC",
        lastName = isOrganization ? null : NullIfWhiteSpace(provider.LastName) ?? "Provider",
        organizationName = isOrganization ? NullIfWhiteSpace(provider.OrganizationName) ?? provider.FullName : null,
        credentials = isOrganization ? null : NullIfWhiteSpace(provider.Credentials) ?? "MD",
        primarySpecialty = NullIfWhiteSpace(provider.SpecialtyDescription) ?? provider.TaxonomyCode,
        taxonomyCode = provider.TaxonomyCode,
        address = provider.Address,
        city = provider.City,
        state = provider.State,
        zipCode = provider.ZipCode,
        phone = NullIfWhiteSpace(provider.Phone) ?? "555-0100",
        email = NullIfWhiteSpace(provider.Email),
        credentialingStatus = isProviderExcluded ? "denied" : "approved",
        credentialingDate = effectiveDate,
        recredentialingDueDate = now.UtcDateTime.AddYears(2),
        acceptingNewPatients = provider.AcceptingNewPatients,
        status = "active",
        integrityScore = isProviderExcluded ? 0 : 96,
        integrityRating = isProviderExcluded ? "Blocked" : "Clear",
        lastVerifiedAt = now,
        nextVerificationDue = now.AddDays(30),
        networkParticipations = new[]
        {
            new
            {
                planId = validationPlanId.ToString(),
                networkId = provider.IsParticipating ? "mcc-local-network" : "mcc-local-out-network",
                lineOfBusiness = LineOfBusinessName(options.LineOfBusiness),
                networkTier = provider.IsParticipating ? "InNetwork" : "OutOfNetwork",
                effectiveDate,
                terminationDate = provider.TermDate,
                acceptingNewPatients = provider.AcceptingNewPatients,
                panelLimit = 2500,
                panelAccepted = provider.AcceptingNewPatients,
                acceptedLobs = new[] { LineOfBusinessName(options.LineOfBusiness) },
                minAcceptedAgeYears = (int?)null,
                maxAcceptedAgeYears = (int?)null,
                rates = new
                {
                    feeScheduleName = provider.FeeScheduleId ?? "MCC Local Fee Schedule",
                    percentOfMedicare = provider.IsParticipating ? 1.10m : 0.80m,
                    pmpm = (decimal?)null,
                    caseRate = (decimal?)null
                }
            }
        }
    };

    using var response = await http.PostAsJsonAsync($"{options.ProviderUrl}/api/v1/providers", payload, json);
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"provider seed failed ({provider.Npi}): {(int)response.StatusCode} {body}");
    }

    return ExtractProviderId(body)
        ?? throw new InvalidOperationException($"provider seed returned no provider id ({provider.Npi}): {body}");
}

static DateTime EffectiveDateForProvider(SyntheticProvider provider) =>
    DateTime.SpecifyKind(provider.EffectiveDate == default
        ? DateTime.UtcNow.Date.AddYears(-2)
        : provider.EffectiveDate.Date, DateTimeKind.Utc);

static bool IsProviderExcluded(SyntheticProvider provider) =>
    string.Equals(provider.CredentialingStatus, "Excluded", StringComparison.OrdinalIgnoreCase)
    || string.Equals(provider.NetworkStatus, "Excluded", StringComparison.OrdinalIgnoreCase);

static string? NullIfWhiteSpace(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value;

static async Task<SubmittedClaim> SubmitClaimAsync(
    HttpClient http,
    ValidatorOptions options,
    SyntheticClaim claim,
    Guid validationPlanId,
    JsonSerializerOptions json)
{
    var payload = new
    {
        tenantId = options.TenantId,
        claimNumber = claim.ClaimId,
        memberId = claim.Member.MemberId,
        subscriberId = claim.Member.SubscriberId,
        benefitPlanId = validationPlanId.ToString(),
        subscriberFirstName = claim.Member.FirstName,
        subscriberLastName = claim.Member.LastName,
        patientFirstName = claim.Member.FirstName,
        patientLastName = claim.Member.LastName,
        patientRelationship = claim.Member.Relationship,
        lineOfBusiness = options.LineOfBusiness,
        billingProviderNPI = claim.BillingProvider.Npi,
        billingProviderName = claim.BillingProvider.FullName,
        renderingProviderNPI = claim.RenderingProvider.Npi,
        renderingProviderName = claim.RenderingProvider.FullName,
        placeOfServiceCode = claim.PlaceOfService,
        claimType = ClaimTypeValue(claim),
        claimFrequencyCode = claim.FrequencyCode ?? "1",
        totalChargeAmount = claim.TotalCharges,
        serviceDateFrom = claim.DateOfService,
        serviceDateTo = claim.Lines.Max(l => l.ServiceEndDate ?? l.ServiceDate),
        diagnosisCodes = BuildDiagnosisCodes(claim),
        claimLines = claim.Lines.Select(line => new
        {
            lineNumber = line.LineNumber,
            procedureCode = line.ProcedureCode,
            procedureDescription = line.Description,
            modifiers = line.Modifiers,
            diagnosisPointers = line.DiagnosisPointers.Count == 0 ? new List<int> { 1 } : line.DiagnosisPointers,
            units = line.Units <= 0 ? 1 : line.Units,
            chargeAmount = line.ChargeAmount,
            serviceDateFrom = line.ServiceDate,
            serviceDateTo = line.ServiceEndDate ?? line.ServiceDate,
            placeOfServiceCode = line.PlaceOfService ?? claim.PlaceOfService,
            revenueCode = line.RevenueCode
        }),
        status = 1,
        submittedDate = claim.DateReceived,
        receivedDate = claim.DateReceived,
        priorAuthorizationNumber = claim.PriorAuthNumber
    };

    using var response = await http.PostAsJsonAsync($"{options.ClaimsUrl}/api/v1/claims", payload, json);
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"claim submission failed ({claim.ClaimId}): {(int)response.StatusCode} {body}");
    }

    using var document = JsonDocument.Parse(body);
    var root = document.RootElement;
    var id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;

    if (string.IsNullOrWhiteSpace(id))
    {
        throw new InvalidOperationException($"claim submission returned no id ({claim.ClaimId}): {body}");
    }

    return new SubmittedClaim(id);
}

static async Task<(AdjudicationResponseDto Response, string RawBody)> AdjudicateClaimAsync(
    HttpClient http,
    ValidatorOptions options,
    SyntheticClaim claim,
    string submittedClaimId,
    Guid validationPlanId,
    string networkTier,
    JsonSerializerOptions json)
{
    var memberEffectiveDate = claim.Member.CoverageEffectiveDate == default
        ? claim.DateOfService.Date.AddYears(-1)
        : claim.Member.CoverageEffectiveDate.Date;

    var payload = new
    {
        claimId = submittedClaimId,
        memberId = claim.Member.MemberId,
        subscriberId = claim.Member.SubscriberId,
        benefitPlanId = validationPlanId,
        serviceDate = DateOnly.FromDateTime(claim.DateOfService),
        memberEffectiveDate = DateOnly.FromDateTime(memberEffectiveDate),
        memberTerminationDate = claim.Member.CoverageTermDate is DateTime termDate
            ? DateOnly.FromDateTime(termDate)
            : (DateOnly?)null,
        memberEnrollmentStatus = claim.Member.EnrollmentStatus,
        providerNpi = claim.RenderingProvider.Npi,
        networkTier,
        lineOfBusiness = options.LineOfBusiness,
        claimType = NormalizeClaimType(claim),
        stateCode = claim.RenderingProvider.State,
        providerTaxonomy = claim.RenderingProvider.TaxonomyCode,
        priorAuthorizationNumber = claim.PriorAuthNumber,
        lines = claim.Lines.Select(line => new
        {
            lineNumber = line.LineNumber,
            procedureCode = line.ProcedureCode,
            codeType = claim.ClaimType.Equals("Dental", StringComparison.OrdinalIgnoreCase) ? "CDT" : "CPT",
            modifiers = line.Modifiers,
            revenueCode = line.RevenueCode,
            placeOfService = line.PlaceOfService ?? claim.PlaceOfService,
            billedAmount = line.ChargeAmount,
            units = line.Units <= 0 ? 1 : line.Units,
            diagnosisCodes = DiagnosisCodesForLine(claim, line)
        })
    };

    using var response = await http.PostAsJsonAsync($"{options.BenefitUrl}/api/v1/adjudication/adjudicate", payload, json);
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity
            && TryParseBusinessDenial(body, json, out var denial))
        {
            return (denial with { ClaimId = submittedClaimId }, body);
        }

        throw new InvalidOperationException($"adjudication failed ({claim.ClaimId}): {(int)response.StatusCode} {body}");
    }

    var result = JsonSerializer.Deserialize<AdjudicationResponseDto>(body, json);
    return (result ?? throw new InvalidOperationException($"adjudication returned empty response ({claim.ClaimId})"), body);
}

static bool TryParseBusinessDenial(
    string body,
    JsonSerializerOptions json,
    out AdjudicationResponseDto denial)
{
    denial = default!;

    try
    {
        var error = JsonSerializer.Deserialize<AdjudicationErrorDto>(body, json);
        if (error?.Error is null || !IsKnownBusinessDenialCode(error.Error))
        {
            return false;
        }

        denial = new AdjudicationResponseDto(
            error.ClaimId ?? string.Empty,
            false,
            error.Carc,
            error.Message,
            new AdjudicationTotalsDto(0, 0, 0, 0, 0, 0, 0, 0),
            error.Error,
            error.Timings);
        return true;
    }
    catch (JsonException)
    {
        return false;
    }
}

static bool IsKnownBusinessDenialCode(string errorCode)
    => NormalizeBusinessDenialCode(errorCode) is
        "SCRUB_VALIDATION_FAILURE" or
        "NCCI_MUE_EDIT_FAILURE" or
        "CARC_27" or
        "CARC_96" or
        "PROVIDER_EXCLUDED" or
        "PRIOR_AUTH_REQUIRED";

static string? NormalizeBusinessDenialCode(string? code)
{
    if (string.IsNullOrWhiteSpace(code))
    {
        return null;
    }

    var trimmed = code.Trim();
    return trimmed.All(char.IsDigit) ? $"CARC_{trimmed}" : trimmed;
}

static string LineOfBusinessName(int lineOfBusiness) => lineOfBusiness switch
{
    1 => "Commercial",
    2 => "Medicare",
    3 => "Medicaid",
    4 => "CHIP",
    5 => "Exchange",
    _ => throw new ArgumentOutOfRangeException(nameof(lineOfBusiness), lineOfBusiness, "Unsupported line-of-business code.")
};

static async Task<ClaimValidationOutcome?> UpdateClaimAdjudicationAsync(
    HttpClient http,
    ValidatorOptions options,
    string submittedClaimId,
    string networkTier,
    AdjudicationResponseDto adjudication,
    JsonSerializerOptions json)
{
    var payload = new
    {
        networkTier,
        allowedAmount = adjudication.Totals.AllowedAmount,
        deductibleAmount = adjudication.Totals.DeductibleAmount,
        coinsuranceAmount = adjudication.Totals.CoinsuranceAmount,
        copayAmount = adjudication.Totals.CopayAmount,
        patientResponsibility = adjudication.Totals.MemberResponsibility,
        payerPayment = adjudication.Totals.PlanPayment,
        denialReasonCode = adjudication.DenialReasonCode,
        denialReason = adjudication.DenialReasonDescription
    };

    using var response = await http.PutAsJsonAsync($"{options.ClaimsUrl}/api/claims/{submittedClaimId}/adjudication-summary", payload, json);
    if (!response.IsSuccessStatusCode)
    {
        var body = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException($"claim adjudication update failed ({submittedClaimId}): {(int)response.StatusCode} {body}");
    }

    if (response.StatusCode != System.Net.HttpStatusCode.OK)
    {
        return null;
    }

    var writeback = await response.Content.ReadFromJsonAsync<AdjudicationSummaryWriteResponseDto>(json);
    if (writeback is not { StatusPreserved: true })
    {
        return null;
    }

    return MccClaimStatusMapping.ToValidationOutcome(writeback.PersistedStatus);
}

static async Task<List<ClaimValidationResult>> ObserveExpectedPendResultsAsync(
    HttpClient http,
    ValidatorOptions options,
    List<ClaimValidationResult> results)
{
    var expectedPendResults = results
        .Where(r => r.ExpectedOutcome == ClaimValidationOutcome.Pended.ToString())
        .ToList();

    if (expectedPendResults.Count == 0)
    {
        return results;
    }

    Console.WriteLine(
        $"Observing {expectedPendResults.Count:N0} expected-pend claims for up to {options.PendObservationTimeoutSeconds:N0}s; benchmark timing excludes this polling window.");

    var observer = new MccClaimStatusObserver(new HttpClaimStatusSource(http, options.ClaimsUrl));
    var observed = new ConcurrentDictionary<string, ClaimValidationResult>(StringComparer.Ordinal);
    var timeout = TimeSpan.FromSeconds(options.PendObservationTimeoutSeconds);
    var interval = TimeSpan.FromMilliseconds(options.PendObservationIntervalMilliseconds);
    var observationParallelism = Math.Min(
        expectedPendResults.Count,
        Math.Max(options.Parallelism, 64));

    await Parallel.ForEachAsync(
        expectedPendResults,
        new ParallelOptions { MaxDegreeOfParallelism = observationParallelism },
        async (result, cancellationToken) =>
        {
            var updated = await observer.ObserveExpectedPendAsync(result, timeout, interval, cancellationToken);
            observed[result.GeneratedClaimId] = updated;
        });

    var pended = observed.Values.Count(r => r.Outcome is ClaimValidationOutcome.Pended);
    var timeouts = observed.Values.Count(r => r.Outcome is ClaimValidationOutcome.ObservationTimeout);
    Console.WriteLine($"  Pend observation:  {pended:N0} pended, {timeouts:N0} timed out");
    Console.WriteLine();

    return results
        .Select(result => observed.TryGetValue(result.GeneratedClaimId, out var updated) ? updated : result)
        .OrderBy(r => r.GeneratedClaimId, StringComparer.Ordinal)
        .ToList();
}

static async Task<List<ClaimValidationResult>> DetectUnexpectedPendResultsAsync(
    HttpClient http,
    ValidatorOptions options,
    List<ClaimValidationResult> results)
{
    var candidates = results
        .Where(r => r.ExpectedOutcome is not null && r.ExpectedOutcome != ClaimValidationOutcome.Pended.ToString())
        .Where(r => r.Outcome is not ClaimValidationOutcome.PlatformFailure)
        .ToList();
    if (candidates.Count == 0) return results;

    Console.WriteLine($"Sweeping {candidates.Count:N0} non-pend claims for unexpected persisted pends.");
    var observer = new MccClaimStatusObserver(new HttpClaimStatusSource(http, options.ClaimsUrl));
    var observed = new ConcurrentDictionary<string, ClaimValidationResult>(StringComparer.Ordinal);
    await Parallel.ForEachAsync(candidates,
        new ParallelOptions { MaxDegreeOfParallelism = Math.Min(candidates.Count, Math.Max(options.Parallelism, 64)) },
        async (result, cancellationToken) =>
            observed[result.GeneratedClaimId] = await observer.DetectUnexpectedPendAsync(result, cancellationToken));

    var falsePends = observed.Values.Count(r => r.FailureStage == "false-pend-observation" && r.Outcome == ClaimValidationOutcome.Pended);
    Console.WriteLine($"  False-pend sweep: {falsePends:N0} unexpected pends");
    Console.WriteLine();
    return results.Select(r => observed.TryGetValue(r.GeneratedClaimId, out var updated) ? updated : r)
        .OrderBy(r => r.GeneratedClaimId, StringComparer.Ordinal).ToList();
}

static List<object> BuildDiagnosisCodes(SyntheticClaim claim)
{
    var codes = new List<string>();
    if (!string.IsNullOrWhiteSpace(claim.PrimaryDiagnosisCode))
    {
        codes.Add(claim.PrimaryDiagnosisCode);
    }

    codes.AddRange(claim.SecondaryDiagnosisCodes.Where(c => !string.IsNullOrWhiteSpace(c)));
    if (codes.Count == 0)
    {
        codes.Add("Z00.00");
    }

    return codes
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select((code, index) => new
        {
            code,
            codeQualifier = index == 0 ? "ABK" : "ABF",
            pointerNumber = index + 1,
            description = DiagnosisCodes.FindDescription(code)
        })
        .Cast<object>()
        .ToList();
}

static List<string> DiagnosisCodesForLine(SyntheticClaim claim, ClaimLine line)
{
    var allCodes = new List<string>();
    if (!string.IsNullOrWhiteSpace(claim.PrimaryDiagnosisCode))
    {
        allCodes.Add(claim.PrimaryDiagnosisCode);
    }

    allCodes.AddRange(claim.SecondaryDiagnosisCodes.Where(c => !string.IsNullOrWhiteSpace(c)));
    if (allCodes.Count == 0)
    {
        allCodes.Add("Z00.00");
    }

    var pointers = line.DiagnosisPointers.Count == 0 ? new List<int> { 1 } : line.DiagnosisPointers;
    return pointers
        .Select(pointer => pointer - 1)
        .Where(index => index >= 0 && index < allCodes.Count)
        .Select(index => allCodes[index])
        .DefaultIfEmpty(allCodes[0])
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static string NormalizeClaimType(SyntheticClaim claim)
{
    if (claim.ClaimType.Equals("EdgeCase", StringComparison.OrdinalIgnoreCase))
    {
        return "Professional";
    }

    return claim.ClaimType;
}

static int ClaimTypeValue(SyntheticClaim claim)
{
    var claimType = NormalizeClaimType(claim);
    return claimType.Equals("Institutional", StringComparison.OrdinalIgnoreCase) ? 2 :
        claimType.Equals("Dental", StringComparison.OrdinalIgnoreCase) ? 3 :
        1;
}

static string NetworkTier(SyntheticClaim claim)
    => claim.RenderingProvider.IsParticipating ? "InNetwork" : "OutOfNetwork";

static CorpusProfile BuildCorpusProfile(int claimCount, int seed)
{
    var majorCounts = AllocateCounts(claimCount, new[]
    {
        ("professional", 0.60),
        ("institutional", 0.25),
        ("dental", 0.10),
        ("edge", 0.05)
    }).ToDictionary(x => x.Item, x => x.Count);

    var edgeCounts = AllocateCounts(majorCounts["edge"], new[]
    {
        ("cob", 12.0 / 50.0),
        ("retroEligibility", 8.0 / 50.0),
        ("newborn", 6.0 / 50.0),
        ("priorAuth", 8.0 / 50.0),
        ("subrogation", 4.0 / 50.0),
        ("behavioralHealth", 6.0 / 50.0),
        ("medicaid", 6.0 / 50.0)
    }).ToDictionary(x => x.Item, x => x.Count);

    return new CorpusProfile
    {
        TotalClaims = claimCount,
        Seed = seed,
        Professional = new ProfessionalDistribution
        {
            Count = majorCounts["professional"],
            OfficeVisitFraction = 0.40,
            MultiLineProcedureFraction = 0.20,
            GlobalSurgeryFraction = 0.10,
            BilateralFraction = 0.05,
            AssistantSurgeonFraction = 0.05,
            TelemedicineFraction = 0.10,
            LabPathologyFraction = 0.10
        },
        Institutional = new InstitutionalDistribution
        {
            Count = majorCounts["institutional"],
            InpatientDrgFraction = 0.40,
            OutpatientPerDiemFraction = 0.25,
            EmergencyFraction = 0.15,
            ObservationFraction = 0.10,
            StopLossOutlierFraction = 0.05,
            SkilledNursingFraction = 0.05
        },
        Dental = new DentalDistribution
        {
            Count = majorCounts["dental"],
            PreventiveFraction = 0.40,
            RestorativeFraction = 0.25,
            EndodonticsFraction = 0.10,
            PeriodonticsFraction = 0.10,
            OrthodonticsFraction = 0.10,
            OralSurgeryFraction = 0.05
        },
        EdgeCases = new EdgeCaseDistribution
        {
            Count = majorCounts["edge"],
            CobCount = GetCount(edgeCounts, "cob"),
            RetroEligibilityCount = GetCount(edgeCounts, "retroEligibility"),
            NewbornCount = GetCount(edgeCounts, "newborn"),
            PriorAuthCount = GetCount(edgeCounts, "priorAuth"),
            SubrogationCount = GetCount(edgeCounts, "subrogation"),
            BehavioralHealthCount = GetCount(edgeCounts, "behavioralHealth"),
            MedicaidCount = GetCount(edgeCounts, "medicaid")
        }
    };
}

static int GetCount(IReadOnlyDictionary<string, int> counts, string key)
    => counts.TryGetValue(key, out var count) ? count : 0;

static IReadOnlyList<(T Item, int Count)> AllocateCounts<T>(int total, IReadOnlyList<(T Item, double Fraction)> weightedItems)
{
    if (total <= 0 || weightedItems.Count == 0)
    {
        return Array.Empty<(T, int)>();
    }

    var allocations = weightedItems
        .Select(item =>
        {
            var exact = total * Math.Max(0, item.Fraction);
            var floor = (int)Math.Floor(exact);
            return new { item.Item, Count = floor, Remainder = exact - floor };
        })
        .ToList();

    var assigned = allocations.Sum(x => x.Count);
    var remaining = total - assigned;
    var counts = allocations.Select(x => x.Count).ToArray();

    foreach (var index in allocations
        .Select((value, index) => new { value.Remainder, index })
        .OrderByDescending(x => x.Remainder)
        .ThenBy(x => x.index)
        .Take(Math.Max(0, remaining))
        .Select(x => x.index))
    {
        counts[index]++;
    }

    return weightedItems
        .Select((item, index) => (item.Item, counts[index]))
        .ToList();
}

static void WriteSummary(MassAdjudicationRunSummary summary)
{
    Console.WriteLine("Validation summary");
    Console.WriteLine($"  Total claims:       {summary.TotalClaims:N0}");
    Console.WriteLine($"  Processed:          {summary.Processed:N0}");
    Console.WriteLine($"  Paid/adjudicated:   {summary.Paid:N0}");
    Console.WriteLine($"  Pended:             {summary.Pended:N0}");
    Console.WriteLine($"  Business denials:   {summary.BusinessDenials:N0}");
    Console.WriteLine($"  Platform failures:  {summary.PlatformFailures:N0}");
    Console.WriteLine($"  Observation timeout: {summary.ObservationTimeouts:N0}");
    Console.WriteLine($"  Workflow checks:    {summary.WorkflowMatches:N0}/{summary.WorkflowScenarios:N0} matched ({summary.WorkflowMismatches:N0} mismatched, {summary.WorkflowUnsupported:N0} unsupported, {summary.WorkflowObservationTimeouts:N0} observation timeouts)");
    Console.WriteLine($"  Elapsed:            {summary.Elapsed:mm\\:ss\\.fff}");
    Console.WriteLine($"  Throughput:         {summary.ThroughputClaimsPerSecond:N2} claims/sec");
    Console.WriteLine($"  P95 latency:        {summary.P95LatencyMilliseconds:N0} ms");
    Console.WriteLine($"  P99 latency:        {summary.P99LatencyMilliseconds:N0} ms");
    WriteLifecycleTimings(summary.LifecycleTimings);
    WriteStageTiming(summary.SubmitTiming);
    WriteStageTiming(summary.AdjudicateTiming);
    WriteStageTiming(summary.WritebackTiming);
    foreach (var timing in summary.AdjudicationStepTimings)
    {
        WriteStageTiming(timing);
    }

    if (summary.AveragePaymentDelta.HasValue)
    {
        Console.WriteLine($"  Avg payment delta:  ${summary.AveragePaymentDelta:N2}");
        Console.WriteLine($"  Payment gate:       {summary.PaymentMatches:N0}/{summary.PaymentComparisons:N0} within ${summary.PaymentTolerance:N2} ({summary.PaymentMismatches:N0} mismatched, max ${summary.MaximumPaymentDelta:N2})");
        foreach (var bucket in summary.PaymentDeltaDistribution.Where(b => b.Count > 0))
        {
            Console.WriteLine($"  Payment delta:      {bucket.Label} ({bucket.Count:N0})");
        }
    }

    foreach (var denialGroup in summary.BusinessDenialBreakdown.Take(5))
    {
        Console.WriteLine($"  Business denial: {denialGroup.Code} ({denialGroup.Count:N0})");
    }

    foreach (var scenario in summary.WorkflowScenarioBreakdown)
    {
        Console.WriteLine($"  Scenario: {scenario.Scenario} {scenario.Matches:N0}/{scenario.Total:N0} matched ({scenario.Mismatches:N0} mismatched, {scenario.Unsupported:N0} unsupported, {scenario.ObservationTimeouts:N0} observation timeouts, {scenario.Unspecified:N0} unspecified)");
    }

    foreach (var failure in summary.SampleFailures)
    {
        Console.WriteLine($"  Failure: {failure.GeneratedClaimId} [{failure.Stage}] {failure.Error}");
    }
}

static void WriteStageTiming(MassAdjudicationStageTiming? timing)
{
    if (timing is null)
    {
        return;
    }

    Console.WriteLine($"  {timing.Label,-12} avg/p95: {timing.AverageMilliseconds:N0} ms / {timing.P95Milliseconds:N0} ms");
}

static void WriteLifecycleTimings(IReadOnlyList<MassAdjudicationLifecycleTiming> timings)
{
    if (timings.Count == 0)
    {
        return;
    }

    var preparationMilliseconds = timings
        .Where(t => string.Equals(t.Category, "Preparation", StringComparison.OrdinalIgnoreCase))
        .Sum(t => t.DurationMilliseconds);
    var lifecycleMilliseconds = timings.Sum(t => t.DurationMilliseconds);

    Console.WriteLine($"  Preparation time:   {FormatDuration(TimeSpan.FromMilliseconds(preparationMilliseconds))}");
    Console.WriteLine($"  Tracked lifecycle:  {FormatDuration(TimeSpan.FromMilliseconds(lifecycleMilliseconds))}");
    foreach (var timing in timings)
    {
        Console.WriteLine($"  Lifecycle.{timing.Label,-28} {FormatDuration(TimeSpan.FromMilliseconds(timing.DurationMilliseconds))}");
    }
}

static string FormatDuration(TimeSpan duration)
    => duration.TotalHours >= 1
        ? duration.ToString("h\\:mm\\:ss")
        : duration.ToString("m\\:ss\\.fff");

static async Task WriteSummaryJsonAsync(string path, MassAdjudicationRunSummary summary, JsonSerializerOptions json)
{
    var directory = Path.GetDirectoryName(Path.GetFullPath(path));
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }

    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(summary, json));
    Console.WriteLine($"  Summary JSON:       {path}");
}

static MassAdjudicationRunSummary BuildProgressSummary(
    List<ClaimValidationResult> results,
    TimeSpan elapsed,
    ValidatorOptions options,
    string runId,
    DateTimeOffset runStartedAtUtc,
    string status,
    string phase,
    MassAdjudicationFixturePreparation fixturePreparation)
{
    var runCompletedAtUtc = DateTimeOffset.UtcNow;
    var processed = results.Count(r => r.Outcome is not ClaimValidationOutcome.PlatformFailure);
    var paid = results.Count(r => r.Outcome is ClaimValidationOutcome.Paid);
    var pended = results.Count(r => r.Outcome is ClaimValidationOutcome.Pended);
    var businessDenials = results.Count(r => r.Outcome is ClaimValidationOutcome.BusinessDenial);
    var observationTimeouts = results.Count(r => r.Outcome is ClaimValidationOutcome.ObservationTimeout);
    var platformFailures = results.Count(r => r.Outcome is ClaimValidationOutcome.PlatformFailure);
    var workflowScenarios = results.Count(r => r.ValidationStatus is not "Unspecified");
    var workflowMatches = results.Count(r => r.ValidationStatus == MccWorkflowValidation.MatchedStatus);
    var workflowMismatches = results.Count(r => r.ValidationStatus == MccWorkflowValidation.MismatchedStatus);
    var workflowUnsupported = results.Count(r => r.ValidationStatus == MccWorkflowValidation.UnsupportedStatus);
    var workflowObservationTimeouts = results.Count(r => r.ValidationStatus == MccWorkflowValidation.ObservationTimeoutStatus);
    var progress = CreateProgress(results, elapsed, options, phase);

    return new MassAdjudicationRunSummary(
        runId,
        status,
        new MassAdjudicationRun(
            options.TenantId,
            options.Claims,
            options.Seed,
            options.Parallelism,
            options.ClaimsUrl,
            options.BenefitUrl,
            options.MemberUrl,
            options.CoverageUrl,
            options.ProviderUrl,
            options.SeedMembers,
            options.SeedProviders,
            options.SkipClaimUpdate,
            options.LineOfBusiness,
            runStartedAtUtc,
            runCompletedAtUtc),
        options.Claims,
        processed,
        paid,
        pended,
        businessDenials,
        observationTimeouts,
        platformFailures,
        workflowScenarios,
        workflowMatches,
        workflowMismatches,
        workflowUnsupported,
        workflowObservationTimeouts,
        elapsed,
        progress.CurrentThroughputClaimsPerSecond,
        progress.RollingP95LatencyMilliseconds,
        progress.RollingP99LatencyMilliseconds,
        null,
        null,
        null,
        Array.Empty<MassAdjudicationStageTiming>(),
        Array.Empty<MassAdjudicationLifecycleTiming>(),
        fixturePreparation,
        null,
        MccRunSummaryBuilder.PaymentTolerance,
        0,
        0,
        0,
        null,
        Array.Empty<MassAdjudicationPaymentDeltaBucket>(),
        Array.Empty<MassAdjudicationBusinessDenialSummary>(),
        Array.Empty<MassAdjudicationWorkflowScenarioSummary>(),
        Array.Empty<MassAdjudicationFailureSummary>(),
        Array.Empty<MassAdjudicationClaimResult>(),
        runStartedAtUtc.UtcDateTime,
        runCompletedAtUtc.UtcDateTime,
        progress);
}

static MassAdjudicationRunProgress CreateProgress(
    IReadOnlyCollection<ClaimValidationResult> results,
    TimeSpan elapsed,
    ValidatorOptions options,
    string phase)
{
    var orderedDurations = results
        .Take(500)
        .Select(r => r.Elapsed.TotalMilliseconds)
        .Order()
        .ToArray();
    var completedClaims = results.Count;
    var processedClaims = results.Count(r => r.Outcome is not ClaimValidationOutcome.PlatformFailure);
    var platformFailures = results.Count(r => r.Outcome is ClaimValidationOutcome.PlatformFailure);
    var throughput = completedClaims / Math.Max(0.001, elapsed.TotalSeconds);

    return new MassAdjudicationRunProgress(
        phase,
        options.Claims,
        completedClaims,
        processedClaims,
        platformFailures,
        options.Claims <= 0 ? 0 : Math.Clamp((double)completedClaims / options.Claims * 100, 0, 100),
        throughput,
        Percentile(orderedDurations, 0.95),
        Percentile(orderedDurations, 0.99),
        DateTimeOffset.UtcNow);
}

static async Task PublishProgressSummaryAsync(
    SemaphoreSlim gate,
    HttpClient http,
    ValidatorOptions options,
    MassAdjudicationRunSummary summary,
    JsonSerializerOptions json)
{
    try
    {
        await PublishSummaryAsync(http, options, summary, json, quiet: true);
    }
    finally
    {
        gate.Release();
    }
}

static bool ShouldPublishProgress(int completedClaims, int totalClaims, ref long lastPublishTicks)
{
    if (completedClaims >= totalClaims)
    {
        return true;
    }

    var nowTicks = DateTimeOffset.UtcNow.Ticks;
    var previousTicks = Interlocked.Read(ref lastPublishTicks);
    if (previousTicks > 0 && nowTicks - previousTicks < TimeSpan.FromSeconds(2).Ticks)
    {
        return false;
    }

    return Interlocked.CompareExchange(ref lastPublishTicks, nowTicks, previousTicks) == previousTicks;
}

static double Percentile(double[] values, double percentile)
{
    if (values.Length == 0)
    {
        return 0;
    }

    var index = (int)Math.Ceiling(percentile * values.Length) - 1;
    return values[Math.Clamp(index, 0, values.Length - 1)];
}

static async Task PublishSummaryAsync(
    HttpClient http,
    ValidatorOptions options,
    MassAdjudicationRunSummary summary,
    JsonSerializerOptions json,
    bool quiet = false)
{
    try
    {
        using var response = await http.PostAsJsonAsync($"{options.ClaimsUrl}/api/mass-adjudication/runs", summary, json);
        if (response.IsSuccessStatusCode)
        {
            if (!quiet)
            {
                Console.WriteLine("  Dashboard summary: published");
            }

            return;
        }

        var body = await response.Content.ReadAsStringAsync();
        if (!quiet)
        {
            Console.WriteLine($"  Dashboard summary: publish skipped ({(int)response.StatusCode} {body})");
        }
    }
    catch (Exception ex)
    {
        if (!quiet)
        {
            Console.WriteLine($"  Dashboard summary: publish skipped ({ex.Message})");
        }
    }
}

static void PrintUsage()
{
    Console.WriteLine("""
    Million Claim Challenge - Platform Validator

    Usage:
      dotnet run --project src/tools/mcc-platform-validator -- [options]

    Options:
      -n, --claims <count>       Number of MCC claims to process (default: 25)
      -s, --seed <seed>          Random seed for reproducibility (default: 42)
      --tenant <tenant-id>       Tenant header to use (default: demo)
      --claims-url <url>         Claims service URL (default: http://localhost:5001)
      --benefit-url <url>        Benefit plan service URL (default: http://localhost:5002)
      --member-url <url>         Member service URL (default: http://localhost:5003)
      --coverage-url <url>       Coverage service URL (default: http://localhost:5005)
      --provider-url <url>       Provider service URL (default: http://localhost:5004)
      --no-seed-members          Skip synthetic member seeding
      --no-seed-providers        Skip synthetic provider seeding
      --skip-claim-update        Do not write adjudication projection back to claims-service
      --no-pend-observation      Do not poll claims-service for expected-pend claim status
      --pend-observation-timeout <seconds>
                                 Max wait for expected-pend claims after benchmark timing stops (default: 45)
      --pend-observation-interval-ms <ms>
                                 Poll interval for expected-pend claim status (default: 1000)
      --pend-diagnostics <path>  Write a per-claim pend diagnostic report (JSON) for expected-pend
                                 scenarios and a sample of NCCI/MUE denials; prints an aggregate
                                 scenario table to the run summary. Off by default; adds one
                                 claims-service read per diagnosed claim AFTER the timed benchmark
                                 window closes — a diagnostics-on run is not a throughput benchmark.
      --pend-diagnostics-ncci-sample <n>
                                 Max NCCI/MUE-denied claims (outside expected-pend) to include in
                                 the diagnostic report (default: 200)
      --timeout <seconds>        Per-request timeout (default: 60)
      --progress-every <count>   Report progress every N claims (default: 10)
      -p, --parallelism <count>  Number of claims to process concurrently (default: 10)
      --max-claims <count>       Maximum accepted --claims value before local safety capping (default: 10000)
      --line-of-business <code>  Adjudication line of business: 1 Commercial, 2 Medicare, 3 Medicaid, 4 CHIP, 5 Exchange (default: 3)
      --no-prior-auth-scenarios  Disable deterministic PA-required claim scenarios
      --prior-auth-rate <rate>   Fraction of generated claims forced into PA-required scenarios (default: 0.02)
      --summary-json <path>      Write machine-readable validation summary JSON
      --no-publish-summary       Do not publish the completed run to claims-service
      --claim-results-limit <n>  Number of per-claim results to publish with the run (default: 1000)
      -h, --help                 Show help

    Example:
      dotnet run --project src/tools/mcc-platform-validator -- --claims 100 --parallelism 10 --tenant demo
    """);
}

internal sealed class InMemoryCorpusWriter : ICorpusWriter
{
    public List<SyntheticClaim> Claims { get; } = new();

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task WriteClaimAsync(SyntheticClaim claim, CancellationToken cancellationToken = default)
    {
        Claims.Add(claim);
        return Task.CompletedTask;
    }

    public Task FinalizeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}


internal sealed record SubmittedClaim(string Id);

public enum ClaimValidationOutcome
{
    Paid,
    BusinessDenial,
    Pended,
    ObservationTimeout,
    PlatformFailure
}

internal sealed record ClaimValidationResult(
    string GeneratedClaimId,
    string? SubmittedClaimId,
    string ClaimType,
    string? ValidationScenario,
    string? ExpectedOutcome,
    string? ExpectedBusinessDenialCode,
    string ValidationStatus,
    ClaimValidationOutcome Outcome,
    bool AdjudicationSuccess,
    decimal? ActualPlanPayment,
    decimal? ExpectedPlanPayment,
    TimeSpan Elapsed,
    TimeSpan SubmitElapsed,
    TimeSpan AdjudicationElapsed,
    TimeSpan UpdateElapsed,
    IReadOnlyDictionary<string, double> AdjudicationStepTimings,
    string? BusinessDenialCode,
    string? FailureStage,
    string? Error,
    JsonElement? SyncAdjudicationSnapshot = null);

internal sealed record MassAdjudicationRunSummary(
    string Id,
    string Status,
    MassAdjudicationRun Run,
    int TotalClaims,
    int Processed,
    int Paid,
    int Pended,
    int BusinessDenials,
    int ObservationTimeouts,
    int PlatformFailures,
    int WorkflowScenarios,
    int WorkflowMatches,
    int WorkflowMismatches,
    int WorkflowUnsupported,
    int WorkflowObservationTimeouts,
    TimeSpan Elapsed,
    double ThroughputClaimsPerSecond,
    double P95LatencyMilliseconds,
    double P99LatencyMilliseconds,
    MassAdjudicationStageTiming? SubmitTiming,
    MassAdjudicationStageTiming? AdjudicateTiming,
    MassAdjudicationStageTiming? WritebackTiming,
    IReadOnlyList<MassAdjudicationStageTiming> AdjudicationStepTimings,
    IReadOnlyList<MassAdjudicationLifecycleTiming> LifecycleTimings,
    MassAdjudicationFixturePreparation? FixturePreparation,
    decimal? AveragePaymentDelta,
    decimal PaymentTolerance,
    int PaymentComparisons,
    int PaymentMatches,
    int PaymentMismatches,
    decimal? MaximumPaymentDelta,
    IReadOnlyList<MassAdjudicationPaymentDeltaBucket> PaymentDeltaDistribution,
    IReadOnlyList<MassAdjudicationBusinessDenialSummary> BusinessDenialBreakdown,
    IReadOnlyList<MassAdjudicationWorkflowScenarioSummary> WorkflowScenarioBreakdown,
    IReadOnlyList<MassAdjudicationFailureSummary> SampleFailures,
    IReadOnlyList<MassAdjudicationClaimResult> ClaimResults,
    DateTime CreatedAtUtc,
    DateTime LastUpdatedAtUtc,
    MassAdjudicationRunProgress? Progress);

internal sealed record MassAdjudicationRun(
    string TenantId,
    int RequestedClaims,
    int Seed,
    int Parallelism,
    string ClaimsUrl,
    string BenefitUrl,
    string MemberUrl,
    string CoverageUrl,
    string ProviderUrl,
    bool SeedMembers,
    bool SeedProviders,
    bool SkipClaimUpdate,
    int LineOfBusiness,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc);

internal sealed record MassAdjudicationRunProgress(
    string Phase,
    int RequestedClaims,
    int CompletedClaims,
    int ProcessedClaims,
    int PlatformFailures,
    double PercentComplete,
    double CurrentThroughputClaimsPerSecond,
    double RollingP95LatencyMilliseconds,
    double RollingP99LatencyMilliseconds,
    DateTimeOffset LastPublishedAtUtc);

internal sealed record MassAdjudicationStageTiming(
    string Label,
    double AverageMilliseconds,
    double P95Milliseconds);

internal sealed record MassAdjudicationLifecycleTiming(
    string Label,
    string Category,
    double DurationMilliseconds,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc);

internal sealed record MassAdjudicationFixturePreparation(
    int GeneratedClaims,
    int ProviderPoolDistinctBefore,
    int ProviderPoolDistinctAfter,
    int ProviderPoolReusedAssignments,
    int ProviderPoolProtectedClaims,
    int MembersCreated,
    int MembersExisting,
    int MemberStatusesAligned,
    int CobCoverageCreated,
    int CobCoverageExisting,
    int ProviderNetworksCreated,
    int ProviderNetworksExisting,
    int ProvidersCreated,
    int ProvidersExisting);

internal sealed record MassAdjudicationPaymentDeltaBucket(
    string Label,
    decimal? LowerBoundExclusive,
    decimal? UpperBoundInclusive,
    int Count);

internal sealed record MemberFixturePreparation(int Created, int Existing, int StatusAligned)
{
    public static MemberFixturePreparation Empty { get; } = new(0, 0, 0);
}

internal sealed record FixtureCount(int Created, int Existing)
{
    public static FixtureCount Empty { get; } = new(0, 0);
}

internal sealed record MassAdjudicationBusinessDenialSummary(
    string Code,
    int Count);

internal sealed record MassAdjudicationWorkflowScenarioSummary(
    string Scenario,
    int Total,
    int Matches,
    int Mismatches,
    int Unsupported,
    int ObservationTimeouts,
    int Unspecified);

internal sealed record MassAdjudicationFailureSummary(
    string GeneratedClaimId,
    string? Stage,
    string? Error);

internal sealed record MassAdjudicationClaimResult(
    string GeneratedClaimId,
    string? SubmittedClaimId,
    string ClaimType,
    string? ValidationScenario,
    string? ExpectedOutcome,
    string? ExpectedBusinessDenialCode,
    string ValidationStatus,
    string Outcome,
    bool AdjudicationSuccess,
    string? BusinessDenialCode,
    string? FailureStage,
    string? Error,
    decimal? ActualPlanPayment,
    decimal? ExpectedPlanPayment,
    decimal? PaymentDelta,
    double ElapsedMilliseconds,
    double SubmitMilliseconds,
    double AdjudicationMilliseconds,
    double WritebackMilliseconds,
    IReadOnlyDictionary<string, double> AdjudicationStepMilliseconds);

internal sealed record AdjudicationResponseDto(
    string ClaimId,
    bool Success,
    string? DenialReasonCode,
    string? DenialReasonDescription,
    AdjudicationTotalsDto Totals,
    string? BusinessDenialCode = null,
    IReadOnlyDictionary<string, double>? Timings = null);

internal sealed record AdjudicationSummaryWriteResponseDto(
    bool StatusPreserved,
    int PersistedStatus);

internal sealed record MemberStatusUpdateDto(
    string Status,
    string EventId);

internal sealed record AdjudicationTotalsDto(
    decimal BilledAmount,
    decimal AllowedAmount,
    decimal ContractualAdjustment,
    decimal DeductibleAmount,
    decimal CopayAmount,
    decimal CoinsuranceAmount,
    decimal MemberResponsibility,
    decimal PlanPayment);

internal sealed record AdjudicationErrorDto(
    string? ClaimId,
    string? Error,
    string? Message,
    string? Carc,
    IReadOnlyDictionary<string, double>? Timings = null);
