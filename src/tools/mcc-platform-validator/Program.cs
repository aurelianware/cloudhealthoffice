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

var options = ValidatorOptions.Parse(args);
if (options.ShowHelp)
{
    PrintUsage();
    return;
}

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
Console.WriteLine($"  Claims:      {options.Claims:N0}");
Console.WriteLine($"  Seed:        {options.Seed}");
Console.WriteLine($"  Parallelism: {options.Parallelism:N0}");
Console.WriteLine();

await RequireHealthyAsync(http, $"{options.ClaimsUrl}/health", "claims-service");
await RequireHealthyAsync(http, $"{options.BenefitUrl}/health", "benefit-plan-service");

await SeedNcciAsync(http, options, json);

var validationPlanId = Guid.NewGuid();
await CreateValidationPlanAsync(http, options, validationPlanId, json);

var claims = await GenerateClaimsAsync(options.Claims, options.Seed);
Console.WriteLine($"Generated {claims.Count:N0} MCC claims in memory");

var results = new ConcurrentBag<ClaimValidationResult>();
var total = Stopwatch.StartNew();
var completed = 0;
var succeeded = 0;
var progressLock = new object();

await Parallel.ForEachAsync(
    claims,
    new ParallelOptions { MaxDegreeOfParallelism = options.Parallelism },
    async (claim, _) =>
    {
        var result = await ProcessClaimAsync(http, options, claim, validationPlanId, json);
        results.Add(result);

        var done = Interlocked.Increment(ref completed);
        if (result.Success)
        {
            Interlocked.Increment(ref succeeded);
        }

        if (done % options.ProgressEvery == 0 || done == claims.Count)
        {
            lock (progressLock)
            {
                done = Volatile.Read(ref completed);
                var ok = Volatile.Read(ref succeeded);
                Console.Write($"\r  Processed: {done:N0}/{claims.Count:N0}  success={ok:N0}  failed={done - ok:N0}");
            }
        }
    });

total.Stop();
Console.WriteLine();
Console.WriteLine();

var orderedResults = results
    .OrderBy(r => r.GeneratedClaimId, StringComparer.Ordinal)
    .ToList();

WriteSummary(orderedResults, total.Elapsed);

if (orderedResults.Any(r => !r.Success))
{
    Environment.ExitCode = 1;
}

static async Task<ClaimValidationResult> ProcessClaimAsync(
    HttpClient http,
    ValidatorOptions options,
    SyntheticClaim claim,
    Guid validationPlanId,
    JsonSerializerOptions json)
{
    var sw = Stopwatch.StartNew();
    var submitElapsed = TimeSpan.Zero;
    var adjudicationElapsed = TimeSpan.Zero;
    var updateElapsed = TimeSpan.Zero;
    var failureStage = "unknown";

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
        var adjudicated = await AdjudicateClaimAsync(http, options, claim, submitted.Id, validationPlanId, networkTier, json);
        stage.Stop();
        adjudicationElapsed = stage.Elapsed;

        if (!options.SkipClaimUpdate)
        {
            failureStage = "writeback";
            stage.Restart();
            await UpdateClaimAdjudicationAsync(http, options, submitted.Id, networkTier, adjudicated, json);
            stage.Stop();
            updateElapsed = stage.Elapsed;
        }

        sw.Stop();
        return new ClaimValidationResult(
            claim.ClaimId,
            submitted.Id,
            claim.ClaimType,
            true,
            adjudicated.Success,
            adjudicated.Totals.PlanPayment,
            claim.ExpectedOutcome?.ExpectedPaidAmount,
            sw.Elapsed,
            submitElapsed,
            adjudicationElapsed,
            updateElapsed,
            null,
            null);
    }
    catch (Exception ex)
    {
        sw.Stop();
        return new ClaimValidationResult(
            claim.ClaimId,
            null,
            claim.ClaimType,
            false,
            false,
            null,
            claim.ExpectedOutcome?.ExpectedPaidAmount,
            sw.Elapsed,
            submitElapsed,
            adjudicationElapsed,
            updateElapsed,
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
        lineOfBusiness = "Commercial",
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
                serviceCategory = "47",
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

static async Task<List<SyntheticClaim>> GenerateClaimsAsync(int claimCount, int seed)
{
    var profile = BuildCorpusProfile(claimCount, seed);
    var writer = new InMemoryCorpusWriter();
    var generator = new ClaimCorpusGenerator(new InMemoryReferenceDataProvider());

    await using (writer)
    {
        await generator.GenerateCorpusAsync(profile, writer);
    }

    var claims = writer.Claims
        .OrderBy(c => c.ClaimId, StringComparer.Ordinal)
        .ToList();
    NormalizeClaimDates(claims, seed);
    return claims;
}

static void NormalizeClaimDates(List<SyntheticClaim> claims, int seed)
{
    var random = new Random(seed);
    var anchorDate = DateTime.UtcNow.Date.AddDays(-45);

    foreach (var claim in claims)
    {
        var originalServiceDate = claim.DateOfService.Date;
        var normalizedServiceDate = anchorDate.AddDays(random.Next(0, 30));
        var dateShift = normalizedServiceDate - originalServiceDate;

        claim.DateOfService = claim.DateOfService.Date.Add(dateShift);

        foreach (var line in claim.Lines)
        {
            line.ServiceDate = line.ServiceDate.Date.Add(dateShift);
            if (line.ServiceEndDate.HasValue)
            {
                line.ServiceEndDate = line.ServiceEndDate.Value.Date.Add(dateShift);
            }
        }

        var latestServiceDate = claim.Lines
            .Select(line => line.ServiceEndDate ?? line.ServiceDate)
            .DefaultIfEmpty(claim.DateOfService)
            .Max();
        claim.DateReceived = latestServiceDate.Date.AddDays(random.Next(1, 15));
    }
}

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
        lineOfBusiness = 1,
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

static async Task<AdjudicationResponseDto> AdjudicateClaimAsync(
    HttpClient http,
    ValidatorOptions options,
    SyntheticClaim claim,
    string submittedClaimId,
    Guid validationPlanId,
    string networkTier,
    JsonSerializerOptions json)
{
    var payload = new
    {
        claimId = submittedClaimId,
        memberId = claim.Member.MemberId,
        subscriberId = claim.Member.SubscriberId,
        benefitPlanId = validationPlanId,
        serviceDate = DateOnly.FromDateTime(claim.DateOfService),
        providerNpi = claim.RenderingProvider.Npi,
        networkTier,
        lineOfBusiness = 1,
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
        throw new InvalidOperationException($"adjudication failed ({claim.ClaimId}): {(int)response.StatusCode} {body}");
    }

    var result = JsonSerializer.Deserialize<AdjudicationResponseDto>(body, json);
    return result ?? throw new InvalidOperationException($"adjudication returned empty response ({claim.ClaimId})");
}

static async Task UpdateClaimAdjudicationAsync(
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

    using var response = await http.PutAsJsonAsync($"{options.ClaimsUrl}/api/claims/{submittedClaimId}/adjudication", payload, json);
    if (!response.IsSuccessStatusCode)
    {
        var body = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException($"claim adjudication update failed ({submittedClaimId}): {(int)response.StatusCode} {body}");
    }
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
            pointerNumber = index + 1
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

static void WriteSummary(List<ClaimValidationResult> results, TimeSpan elapsed)
{
    var succeeded = results.Count(r => r.Success);
    var adjudicated = results.Count(r => r.AdjudicationSuccess);
    var failed = results.Count - succeeded;
    var orderedDurations = results.Select(r => r.Elapsed.TotalMilliseconds).Order().ToArray();
    var p95 = Percentile(orderedDurations, 0.95);
    var p99 = Percentile(orderedDurations, 0.99);
    var throughput = results.Count / Math.Max(0.001, elapsed.TotalSeconds);

    Console.WriteLine("Validation summary");
    Console.WriteLine($"  Total claims:       {results.Count:N0}");
    Console.WriteLine($"  Processed:          {succeeded:N0}");
    Console.WriteLine($"  Adjudicated:        {adjudicated:N0}");
    Console.WriteLine($"  Failed:             {failed:N0}");
    Console.WriteLine($"  Elapsed:            {elapsed:mm\\:ss\\.fff}");
    Console.WriteLine($"  Throughput:         {throughput:N2} claims/sec");
    Console.WriteLine($"  P95 latency:        {p95:N0} ms");
    Console.WriteLine($"  P99 latency:        {p99:N0} ms");
    WriteStageTiming("Submit", results.Select(r => r.SubmitElapsed));
    WriteStageTiming("Adjudicate", results.Select(r => r.AdjudicationElapsed));
    WriteStageTiming("Writeback", results.Select(r => r.UpdateElapsed));

    var comparable = results
        .Where(r => r.ActualPlanPayment.HasValue && r.ExpectedPlanPayment.HasValue)
        .ToList();
    if (comparable.Count > 0)
    {
        var avgDelta = comparable
            .Average(r => Math.Abs(r.ActualPlanPayment!.Value - r.ExpectedPlanPayment!.Value));
        Console.WriteLine($"  Avg payment delta:  ${avgDelta:N2}");
    }

    foreach (var failure in results.Where(r => !r.Success).Take(5))
    {
        Console.WriteLine($"  Failure: {failure.GeneratedClaimId} [{failure.FailureStage}] {failure.Error}");
    }
}

static void WriteStageTiming(string label, IEnumerable<TimeSpan> durations)
{
    var values = durations
        .Select(d => d.TotalMilliseconds)
        .Where(ms => ms > 0)
        .Order()
        .ToArray();

    if (values.Length == 0)
    {
        return;
    }

    Console.WriteLine($"  {label,-12} avg/p95: {values.Average():N0} ms / {Percentile(values, 0.95):N0} ms");
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
      --skip-claim-update        Do not write adjudication projection back to claims-service
      --timeout <seconds>        Per-request timeout (default: 60)
      --progress-every <count>   Report progress every N claims (default: 25)
      --parallelism <count>      Number of claims to process concurrently (default: 4)
      -h, --help                 Show help

    Example:
      dotnet run --project src/tools/mcc-platform-validator -- --claims 100 --parallelism 4 --tenant demo
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

internal sealed record ValidatorOptions(
    int Claims,
    int Seed,
    string TenantId,
    string ClaimsUrl,
    string BenefitUrl,
    bool SkipClaimUpdate,
    int TimeoutSeconds,
    int ProgressEvery,
    int Parallelism,
    bool ShowHelp)
{
    public const int MaxClaims = 10_000;
    public const int MaxParallelism = 64;

    public static ValidatorOptions Parse(string[] args)
    {
        var options = new MutableOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--claims" or "-n" when i + 1 < args.Length:
                    options.Claims = int.Parse(args[++i]);
                    break;
                case "--seed" or "-s" when i + 1 < args.Length:
                    options.Seed = int.Parse(args[++i]);
                    break;
                case "--tenant" when i + 1 < args.Length:
                    options.TenantId = args[++i];
                    break;
                case "--claims-url" when i + 1 < args.Length:
                    options.ClaimsUrl = args[++i].TrimEnd('/');
                    break;
                case "--benefit-url" when i + 1 < args.Length:
                    options.BenefitUrl = args[++i].TrimEnd('/');
                    break;
                case "--skip-claim-update":
                    options.SkipClaimUpdate = true;
                    break;
                case "--timeout" when i + 1 < args.Length:
                    options.TimeoutSeconds = int.Parse(args[++i]);
                    break;
                case "--progress-every" when i + 1 < args.Length:
                    options.ProgressEvery = int.Parse(args[++i]);
                    break;
                case "--parallelism" or "-p" when i + 1 < args.Length:
                    options.Parallelism = int.Parse(args[++i]);
                    break;
                case "--help" or "-h":
                    options.ShowHelp = true;
                    break;
            }
        }

        if (options.Claims > MaxClaims)
        {
            Console.Error.WriteLine($"warning: capping --claims to {MaxClaims:N0} to avoid excessive in-memory allocation");
            options.Claims = MaxClaims;
        }

        if (options.Parallelism > MaxParallelism)
        {
            Console.Error.WriteLine($"warning: capping --parallelism to {MaxParallelism:N0} for local validation");
            options.Parallelism = MaxParallelism;
        }

        return new ValidatorOptions(
            Math.Max(1, options.Claims),
            options.Seed,
            options.TenantId,
            options.ClaimsUrl.TrimEnd('/'),
            options.BenefitUrl.TrimEnd('/'),
            options.SkipClaimUpdate,
            Math.Max(5, options.TimeoutSeconds),
            Math.Max(1, options.ProgressEvery),
            Math.Max(1, options.Parallelism),
            options.ShowHelp);
    }

    private sealed class MutableOptions
    {
        public int Claims { get; set; } = 25;
        public int Seed { get; set; } = 42;
        public string TenantId { get; set; } = "demo";
        public string ClaimsUrl { get; set; } = "http://localhost:5001";
        public string BenefitUrl { get; set; } = "http://localhost:5002";
        public bool SkipClaimUpdate { get; set; }
        public int TimeoutSeconds { get; set; } = 60;
        public int ProgressEvery { get; set; } = 10;
        public int Parallelism { get; set; } = 4;
        public bool ShowHelp { get; set; }
    }
}

internal sealed record SubmittedClaim(string Id);

internal sealed record ClaimValidationResult(
    string GeneratedClaimId,
    string? SubmittedClaimId,
    string ClaimType,
    bool Success,
    bool AdjudicationSuccess,
    decimal? ActualPlanPayment,
    decimal? ExpectedPlanPayment,
    TimeSpan Elapsed,
    TimeSpan SubmitElapsed,
    TimeSpan AdjudicationElapsed,
    TimeSpan UpdateElapsed,
    string? FailureStage,
    string? Error);

internal sealed record AdjudicationResponseDto(
    string ClaimId,
    bool Success,
    string? DenialReasonCode,
    string? DenialReasonDescription,
    AdjudicationTotalsDto Totals);

internal sealed record AdjudicationTotalsDto(
    decimal BilledAmount,
    decimal AllowedAmount,
    decimal ContractualAdjustment,
    decimal DeductibleAmount,
    decimal CopayAmount,
    decimal CoinsuranceAmount,
    decimal MemberResponsibility,
    decimal PlanPayment);
