using System.Diagnostics;
using System.Threading.Channels;
using CloudHealthOffice.BenchmarkClaimGenerator.Configuration;
using CloudHealthOffice.BenchmarkClaimGenerator.Generators;
using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using CloudHealthOffice.BenchmarkClaimGenerator.Output;
using CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.BenchmarkClaimGenerator;

/// <summary>
/// Orchestrates parallel generation of the Million Claim Challenge corpus.
/// Uses System.Threading.Channels to pipeline claim generation and output writing.
/// </summary>
public class ClaimCorpusGenerator
{
    private readonly IReferenceDataProvider _refData;
    private readonly ILogger<ClaimCorpusGenerator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClaimCorpusGenerator"/> class.
    /// </summary>
    /// <param name="refData">Reference data provider for code sets and entities.</param>
    /// <param name="logger">Optional logger. Uses NullLogger if not provided.</param>
    public ClaimCorpusGenerator(IReferenceDataProvider refData, ILogger<ClaimCorpusGenerator>? logger = null)
    {
        _refData = refData;
        _logger = logger ?? NullLogger<ClaimCorpusGenerator>.Instance;
    }

    /// <summary>
    /// Generate the complete corpus according to the specified profile.
    /// Claims are generated in parallel across generators and streamed to the writer.
    /// </summary>
    /// <param name="profile">Distribution profile controlling claim counts and stratification.</param>
    /// <param name="writer">Output writer for serializing claims.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="CorpusResult"/> with generation statistics.</returns>
    public async Task<CorpusResult> GenerateCorpusAsync(
        CorpusProfile profile,
        ICorpusWriter writer,
        IProgress<CorpusProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        await writer.InitializeAsync(cancellationToken);

        var channel = Channel.CreateBounded<SyntheticClaim>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = true
        });

        var professionalGen = new ProfessionalClaimGenerator(_refData);
        var institutionalGen = new InstitutionalClaimGenerator(_refData);
        var dentalGen = new DentalClaimGenerator(_refData);
        var edgeCaseGen = new EdgeCaseClaimGenerator(_refData);

        // Start producer tasks
        var producers = new List<Task>
        {
            Task.Run(() => ProduceProfessionalClaims(
                professionalGen, profile, channel.Writer, profile.Seed, cancellationToken), cancellationToken),
            Task.Run(() => ProduceInstitutionalClaims(
                institutionalGen, profile, channel.Writer, profile.Seed + 1, cancellationToken), cancellationToken),
            Task.Run(() => ProduceDentalClaims(
                dentalGen, profile, channel.Writer, profile.Seed + 2, cancellationToken), cancellationToken),
            Task.Run(() => ProduceEdgeCaseClaims(
                edgeCaseGen, profile, channel.Writer, profile.Seed + 3, cancellationToken), cancellationToken)
        };

        // Complete the channel when all producers finish
        _ = Task.WhenAll(producers).ContinueWith(_ => channel.Writer.TryComplete(), cancellationToken);

        // Consumer: read from channel and write
        int totalWritten = 0;
        int professionalCount = 0, institutionalCount = 0, dentalCount = 0, edgeCaseCount = 0;

        await foreach (var claim in channel.Reader.ReadAllAsync(cancellationToken))
        {
            await writer.WriteClaimAsync(claim, cancellationToken);
            totalWritten++;

            switch (claim.ClaimType)
            {
                case "Professional": professionalCount++; break;
                case "Institutional": institutionalCount++; break;
                case "Dental": dentalCount++; break;
                case "EdgeCase": edgeCaseCount++; break;
            }

            if (totalWritten % 10_000 == 0)
            {
                progress?.Report(new CorpusProgress
                {
                    ClaimsGenerated = totalWritten,
                    TotalClaims = profile.TotalClaims,
                    ElapsedTime = stopwatch.Elapsed
                });
                _logger.LogInformation("Generated {Count:N0} / {Total:N0} claims",
                    totalWritten, profile.TotalClaims);
            }
        }

        // Wait for all producers to complete (handle exceptions)
        await Task.WhenAll(producers);

        await writer.FinalizeAsync(cancellationToken);
        stopwatch.Stop();

        var result = new CorpusResult
        {
            TotalClaims = totalWritten,
            ProfessionalCount = professionalCount,
            InstitutionalCount = institutionalCount,
            DentalCount = dentalCount,
            EdgeCaseCount = edgeCaseCount,
            ElapsedTime = stopwatch.Elapsed,
            Seed = profile.Seed
        };

        _logger.LogInformation(
            "Corpus generation complete: {Total:N0} claims in {Elapsed}",
            result.TotalClaims, result.ElapsedTime);

        return result;
    }

    private async Task ProduceProfessionalClaims(
        ProfessionalClaimGenerator generator,
        CorpusProfile profile,
        ChannelWriter<SyntheticClaim> writer,
        int seed,
        CancellationToken ct)
    {
        var random = new Random(seed);
        var dist = profile.Professional;
        int seq = 0;

        var subTypes = new (string SubType, double Fraction)[]
        {
            ("officevisit", dist.OfficeVisitFraction),
            ("multiline", dist.MultiLineProcedureFraction),
            ("globalsurgery", dist.GlobalSurgeryFraction),
            ("bilateral", dist.BilateralFraction),
            ("assistantsurgeon", dist.AssistantSurgeonFraction),
            ("telemedicine", dist.TelemedicineFraction),
            ("labpathology", dist.LabPathologyFraction)
        };

        foreach (var (subType, count) in AllocateCounts(dist.Count, subTypes))
        {
            for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
            {
                seq++;
                var claim = generator.Generate(seq, subType, random);
                await writer.WriteAsync(claim, ct);
            }
        }
    }

    private async Task ProduceInstitutionalClaims(
        InstitutionalClaimGenerator generator,
        CorpusProfile profile,
        ChannelWriter<SyntheticClaim> writer,
        int seed,
        CancellationToken ct)
    {
        var random = new Random(seed);
        var dist = profile.Institutional;
        int seq = 0;

        var subTypes = new (string SubType, double Fraction)[]
        {
            ("inpatient", dist.InpatientDrgFraction),
            ("outpatient", dist.OutpatientPerDiemFraction),
            ("emergency", dist.EmergencyFraction),
            ("observation", dist.ObservationFraction),
            ("stoploss", dist.StopLossOutlierFraction),
            ("skillednursing", dist.SkilledNursingFraction)
        };

        foreach (var (subType, count) in AllocateCounts(dist.Count, subTypes))
        {
            for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
            {
                seq++;
                var claim = generator.Generate(seq, subType, random);
                await writer.WriteAsync(claim, ct);
            }
        }
    }

    private async Task ProduceDentalClaims(
        DentalClaimGenerator generator,
        CorpusProfile profile,
        ChannelWriter<SyntheticClaim> writer,
        int seed,
        CancellationToken ct)
    {
        var random = new Random(seed);
        var dist = profile.Dental;
        int seq = 0;

        var subTypes = new (string SubType, double Fraction)[]
        {
            ("preventive", dist.PreventiveFraction),
            ("restorative", dist.RestorativeFraction),
            ("endodontics", dist.EndodonticsFraction),
            ("periodontics", dist.PeriodonticsFraction),
            ("orthodontics", dist.OrthodonticsFraction),
            ("oralsurgery", dist.OralSurgeryFraction)
        };

        foreach (var (subType, count) in AllocateCounts(dist.Count, subTypes))
        {
            for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
            {
                seq++;
                var claim = generator.Generate(seq, subType, random);
                await writer.WriteAsync(claim, ct);
            }
        }
    }

    private async Task ProduceEdgeCaseClaims(
        EdgeCaseClaimGenerator generator,
        CorpusProfile profile,
        ChannelWriter<SyntheticClaim> writer,
        int seed,
        CancellationToken ct)
    {
        var random = new Random(seed);
        var dist = profile.EdgeCases;
        int seq = 0;

        var scenarioGroups = new (EdgeCaseScenario[] Scenarios, int TotalCount)[]
        {
            (new[] {
                EdgeCaseScenario.CobPrimaryPayer, EdgeCaseScenario.CobSecondaryPayer,
                EdgeCaseScenario.CobTertiaryPayer, EdgeCaseScenario.CobBirthdayRule,
                EdgeCaseScenario.CobGenderRule, EdgeCaseScenario.CobMedicareSecondary
            }, dist.CobCount),

            (new[] {
                EdgeCaseScenario.RetroEligibilityAdd,
                EdgeCaseScenario.RetroEligibilityTermination,
                EdgeCaseScenario.RetroEligibilityCoverageChange
            }, dist.RetroEligibilityCount),

            (new[] {
                EdgeCaseScenario.NewbornAutoAdjudication,
                EdgeCaseScenario.NewbornMotherClaimLink,
                EdgeCaseScenario.NewbornFirstThirtyDays
            }, dist.NewbornCount),

            (new[] {
                EdgeCaseScenario.PriorAuthRequired_AuthOnFile,
                EdgeCaseScenario.PriorAuthRequired_NoAuth,
                EdgeCaseScenario.PriorAuthRequired_ExpiredAuth,
                EdgeCaseScenario.PriorAuthRequired_WrongProvider,
                EdgeCaseScenario.PriorAuthRequired_WrongProcedure
            }, dist.PriorAuthCount),

            (new[] {
                EdgeCaseScenario.SubrogationAccidentRelated,
                EdgeCaseScenario.SubrogationWorkersComp,
                EdgeCaseScenario.SubrogationThirdPartyLiability
            }, dist.SubrogationCount),

            (new[] {
                EdgeCaseScenario.BehavioralHealthCarveOut,
                EdgeCaseScenario.BehavioralHealthCarveIn,
                EdgeCaseScenario.BehavioralHealthParityCheck
            }, dist.BehavioralHealthCount),

            (new[] {
                EdgeCaseScenario.MedicaidTANF, EdgeCaseScenario.MedicaidSSI,
                EdgeCaseScenario.MedicaidCHIP, EdgeCaseScenario.MedicaidDualEligible,
                EdgeCaseScenario.MedicaidSpendDown
            }, dist.MedicaidCount)
        };

        foreach (var (scenarios, totalCount) in scenarioGroups)
        {
            foreach (var (scenario, count) in AllocateEvenly(totalCount, scenarios))
            {
                for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
                {
                    seq++;
                    var claim = generator.Generate(seq, scenario.ToString(), random);
                    await writer.WriteAsync(claim, ct);
                }
            }
        }
    }

    private static IReadOnlyList<(T Item, int Count)> AllocateCounts<T>(
        int total,
        IReadOnlyList<(T Item, double Fraction)> weightedItems)
    {
        if (weightedItems.Count == 0)
        {
            return Array.Empty<(T, int)>();
        }

        if (total <= 0)
        {
            return weightedItems
                .Select(item => (item.Item, 0))
                .ToList();
        }

        var weights = weightedItems
            .Select(item => Math.Max(0, item.Fraction))
            .ToArray();
        var totalWeight = weights.Sum();

        if (totalWeight <= 0)
        {
            Array.Fill(weights, 1.0);
            totalWeight = weights.Length;
        }

        var allocations = weightedItems
            .Select((item, index) =>
            {
                var exact = total * (weights[index] / totalWeight);
                var floor = (int)Math.Floor(exact);
                return new
                {
                    item.Item,
                    Count = floor,
                    Remainder = exact - floor
                };
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

    private static IReadOnlyList<(T Item, int Count)> AllocateEvenly<T>(int total, IReadOnlyList<T> items)
    {
        if (total <= 0 || items.Count == 0)
        {
            return Array.Empty<(T, int)>();
        }

        var baseCount = total / items.Count;
        var remainder = total % items.Count;

        return items
            .Select((item, index) => (item, baseCount + (index < remainder ? 1 : 0)))
            .ToList();
    }
}

/// <summary>
/// Result statistics from corpus generation.
/// </summary>
public class CorpusResult
{
    /// <summary>Total number of claims generated.</summary>
    public int TotalClaims { get; set; }

    /// <summary>Number of professional claims generated.</summary>
    public int ProfessionalCount { get; set; }

    /// <summary>Number of institutional claims generated.</summary>
    public int InstitutionalCount { get; set; }

    /// <summary>Number of dental claims generated.</summary>
    public int DentalCount { get; set; }

    /// <summary>Number of edge case claims generated.</summary>
    public int EdgeCaseCount { get; set; }

    /// <summary>Total elapsed time for generation.</summary>
    public TimeSpan ElapsedTime { get; set; }

    /// <summary>Random seed used for this generation run.</summary>
    public int Seed { get; set; }
}

/// <summary>
/// Progress report during corpus generation.
/// </summary>
public class CorpusProgress
{
    /// <summary>Number of claims generated so far.</summary>
    public int ClaimsGenerated { get; set; }

    /// <summary>Total claims to generate.</summary>
    public int TotalClaims { get; set; }

    /// <summary>Elapsed time since generation started.</summary>
    public TimeSpan ElapsedTime { get; set; }

    /// <summary>Estimated percentage complete.</summary>
    public double PercentComplete => TotalClaims > 0 ? (double)ClaimsGenerated / TotalClaims * 100 : 0;
}
