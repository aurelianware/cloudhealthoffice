using System.Diagnostics;
using CloudHealthOffice.BenchmarkClaimGenerator.Configuration;
using CloudHealthOffice.BenchmarkClaimGenerator.Generators;
using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using CloudHealthOffice.BenchmarkClaimGenerator.Output;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Seeding;

/// <summary>
/// Orchestrates the full benchmark data generation and seeding pipeline.
/// Enforces dependency ordering: plans → fee schedules → providers → contracts →
/// members → coverage → accumulators → claims.
/// </summary>
public class SeedingOrchestrator
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeedingOrchestrator"/> class.
    /// </summary>
    public SeedingOrchestrator(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Result of the full seeding orchestration.
    /// </summary>
    public class SeedingResult
    {
        /// <summary>Whether all steps completed successfully.</summary>
        public bool Success { get; set; }

        /// <summary>Step that failed (null if all succeeded).</summary>
        public string? FailedStep { get; set; }

        /// <summary>Error message if a step failed.</summary>
        public string? ErrorMessage { get; set; }

        /// <summary>Total elapsed time.</summary>
        public TimeSpan ElapsedTime { get; set; }

        /// <summary>Generated benefit plans.</summary>
        public List<SyntheticBenefitPlan> BenefitPlans { get; set; } = new();

        /// <summary>Generated fee schedules.</summary>
        public List<SyntheticFeeSchedule> FeeSchedules { get; set; } = new();

        /// <summary>Generated providers.</summary>
        public List<SyntheticProvider> Providers { get; set; } = new();

        /// <summary>Generated provider contracts.</summary>
        public List<SyntheticProviderContract> Contracts { get; set; } = new();

        /// <summary>Generated members (subscribers with dependents).</summary>
        public List<SyntheticMember> Members { get; set; } = new();

        /// <summary>Generated accumulators.</summary>
        public List<SyntheticAccumulator> Accumulators { get; set; } = new();

        /// <summary>Records seeded per step.</summary>
        public Dictionary<string, int> RecordsSeeded { get; set; } = new();
    }

    /// <summary>
    /// Generate all reference data in dependency order, optionally seeding to a backend.
    /// </summary>
    /// <param name="memberProfile">Member generation parameters.</param>
    /// <param name="providerProfile">Provider generation parameters.</param>
    /// <param name="seed">Master random seed.</param>
    /// <param name="seeder">Optional data seeder (Cosmos DB, etc.).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Seeding result with all generated data pools.</returns>
    public async Task<SeedingResult> GenerateAndSeedAsync(
        MemberPoolProfile memberProfile,
        ProviderPoolProfile providerProfile,
        int seed = 42,
        IBenchmarkDataSeeder? seeder = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new SeedingResult();
        string currentStep = "";

        try
        {
            // Step 1: Benefit Plans (no dependencies)
            currentStep = "BenefitPlans";
            _logger.LogInformation("Step 1/8: Generating benefit plans...");
            result.BenefitPlans = SyntheticBenefitPlanGenerator.Generate(seed);
            if (seeder != null)
                result.RecordsSeeded[currentStep] = await seeder.SeedBenefitPlansAsync(result.BenefitPlans, cancellationToken);
            _logger.LogInformation("Step 1/8 complete: {Count} benefit plans", result.BenefitPlans.Count);

            // Step 2: Fee Schedules (no dependencies)
            currentStep = "FeeSchedules";
            _logger.LogInformation("Step 2/8: Generating fee schedules...");
            result.FeeSchedules = SyntheticFeeScheduleGenerator.Generate(seed);
            if (seeder != null)
                result.RecordsSeeded[currentStep] = await seeder.SeedFeeSchedulesAsync(result.FeeSchedules, cancellationToken);
            _logger.LogInformation("Step 2/8 complete: {Count} fee schedules", result.FeeSchedules.Count);

            // Step 3: Providers (no dependencies)
            currentStep = "Providers";
            _logger.LogInformation("Step 3/8: Generating providers...");
            var providerGen = new SyntheticProviderGenerator(_logger);
            result.Providers = await providerGen.GenerateAsync(providerProfile, cancellationToken);
            if (seeder != null)
                result.RecordsSeeded[currentStep] = await seeder.SeedProvidersAsync(result.Providers, cancellationToken);
            _logger.LogInformation("Step 3/8 complete: {Count:N0} providers", result.Providers.Count);

            // Step 4: Provider Contracts (depends on providers + fee schedules)
            currentStep = "ProviderContracts";
            _logger.LogInformation("Step 4/8: Generating provider contracts...");
            result.Contracts = GenerateProviderContracts(result.Providers, result.FeeSchedules, seed);
            if (seeder != null)
                result.RecordsSeeded[currentStep] = await seeder.SeedProviderContractsAsync(result.Contracts, cancellationToken);
            _logger.LogInformation("Step 4/8 complete: {Count:N0} contracts", result.Contracts.Count);

            // Step 5 & 6: Members + Coverage (depends on benefit plans, providers for PCP)
            currentStep = "Members";
            _logger.LogInformation("Step 5/8: Generating members...");
            var pcpProviders = result.Providers
                .Where(p => p.IsParticipating && p.ProviderType == "Individual" &&
                       (p.SpecialtyCode.StartsWith("207Q") || p.SpecialtyCode.StartsWith("208D") ||
                        p.SpecialtyCode.StartsWith("207R") || p.SpecialtyCode.StartsWith("2083")))
                .ToList();
            var memberGen = new SyntheticMemberGenerator(_logger);
            result.Members = await memberGen.GenerateAsync(memberProfile, result.BenefitPlans, pcpProviders, cancellationToken);
            if (seeder != null)
            {
                result.RecordsSeeded["Members"] = await seeder.SeedMembersAsync(result.Members, cancellationToken);
                _logger.LogInformation("Step 6/8: Seeding coverages...");
                result.RecordsSeeded["Coverages"] = await seeder.SeedCoveragesAsync(result.Members, cancellationToken);
            }
            _logger.LogInformation("Step 5-6/8 complete: {Count:N0} subscribers", result.Members.Count);

            // Step 7: Accumulators (depends on members, coverage, benefit plans)
            currentStep = "Accumulators";
            _logger.LogInformation("Step 7/8: Generating accumulators...");
            var accGen = new SyntheticAccumulatorGenerator(_logger);
            result.Accumulators = accGen.Generate(result.Members, result.BenefitPlans, seed, tenantId: memberProfile.TenantId);
            if (seeder != null)
                result.RecordsSeeded[currentStep] = await seeder.SeedAccumulatorsAsync(result.Accumulators, cancellationToken);
            _logger.LogInformation("Step 7/8 complete: {Count:N0} accumulators", result.Accumulators.Count);

            // Step 8 is reserved for claims corpus generation (done externally)
            _logger.LogInformation("Step 8/8: Reference data generation complete. Ready for claims corpus.");

            result.Success = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.Success = false;
            result.FailedStep = currentStep;
            result.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Seeding failed at step {Step}", currentStep);
        }

        stopwatch.Stop();
        result.ElapsedTime = stopwatch.Elapsed;

        _logger.LogInformation("Seeding orchestration {Status} in {Elapsed}",
            result.Success ? "completed" : "FAILED", result.ElapsedTime);

        return result;
    }

    /// <summary>
    /// Generate provider contracts for all in-network providers.
    /// </summary>
    internal static List<SyntheticProviderContract> GenerateProviderContracts(
        List<SyntheticProvider> providers,
        List<SyntheticFeeSchedule> feeSchedules,
        int seed)
    {
        var random = new Random(seed + 100);
        var contracts = new List<SyntheticProviderContract>();
        int seq = 0;

        var medicaidFs = feeSchedules.FirstOrDefault(fs => fs.FeeScheduleId == "FS-MEDICAID");
        var oonFs = feeSchedules.FirstOrDefault(fs => fs.FeeScheduleId == "FS-OON");
        var capFs = feeSchedules.FirstOrDefault(fs => fs.FeeScheduleId == "FS-CAPITATION");

        foreach (var provider in providers)
        {
            if (!provider.IsParticipating)
                continue;

            seq++;
            var feeScheduleId = provider.ContractType switch
            {
                "Capitation" => capFs?.FeeScheduleId ?? "FS-CAPITATION",
                _ => medicaidFs?.FeeScheduleId ?? "FS-MEDICAID",
            };

            var paymentMethodology = provider.ContractType switch
            {
                "Capitation" => "FullCapitation",
                "PerDiem" => "FeeForService",
                _ => "FeeForService",
            };

            var contract = new SyntheticProviderContract
            {
                ContractId = $"MCC-CTR-{seq:D7}",
                TenantId = provider.TenantId,
                ContractNumber = $"CTR-{provider.Npi}-2024",
                ProviderNpi = provider.Npi,
                ProviderName = provider.FullName,
                ProviderType = provider.ProviderType,
                LineOfBusiness = "Medicaid",
                FeeScheduleId = feeScheduleId,
                ContractType = provider.ContractType,
                PaymentMethodology = paymentMethodology,
                NetworkStatus = "Participating",
                EffectiveDate = provider.EffectiveDate,
                TermDate = provider.TermDate,
                AutoRenews = true,
                Status = "Active",
                ReimbursementMethod = provider.ContractType switch
                {
                    "Capitation" => "PMPM",
                    "PerDiem" => "PerDiem",
                    _ => "PercentOfFeeSchedule",
                },
            };

            // Set PMPM for capitated contracts
            if (provider.ContractType == "Capitation" && capFs != null)
            {
                var rate = capFs.CapitationRates.FirstOrDefault();
                contract.CapitationPmpm = rate?.PmpmRate ?? 250m;
            }

            // Link contract back to provider
            provider.ContractId = contract.ContractId;
            provider.FeeScheduleId = feeScheduleId;

            contracts.Add(contract);
        }

        return contracts;
    }

    /// <summary>
    /// Generate output files (834, provider CSV, fee schedule CSV) to a directory.
    /// </summary>
    public async Task GenerateOutputFilesAsync(
        SeedingResult data,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);

        var tasks = new List<Task>();

        // Generate 834 enrollment files
        var ediDir = Path.Combine(outputDirectory, "834");
        var ediWriter = new X12_834Writer(ediDir, _logger);
        tasks.Add(ediWriter.WriteAsync(data.Members, cancellationToken));

        // Generate provider import CSVs
        var provDir = Path.Combine(outputDirectory, "providers");
        var provWriter = new ProviderImportCsvWriter(provDir, _logger);
        tasks.Add(provWriter.WriteAsync(data.Providers, cancellationToken));

        // Generate fee schedule CSVs
        var fsDir = Path.Combine(outputDirectory, "fee-schedules");
        var fsWriter = new FeeScheduleImportCsvWriter(fsDir, _logger);
        tasks.Add(fsWriter.WriteAsync(data.FeeSchedules, cancellationToken));

        await Task.WhenAll(tasks);

        _logger.LogInformation("All output files generated in {Dir}", outputDirectory);
    }
}
