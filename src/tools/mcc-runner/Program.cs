using CloudHealthOffice.BenchmarkClaimGenerator;
using CloudHealthOffice.BenchmarkClaimGenerator.Configuration;
using CloudHealthOffice.BenchmarkClaimGenerator.Output;
using CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;

var claimCount = 1_000;
var seed = 42;
var outputPath = Path.Combine(Directory.GetCurrentDirectory(), "mcc-output");
var format = "json";

// Parse arguments
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--claims" or "-n" when i + 1 < args.Length:
            claimCount = int.Parse(args[++i]);
            break;
        case "--seed" or "-s" when i + 1 < args.Length:
            seed = int.Parse(args[++i]);
            break;
        case "--output" or "-o" when i + 1 < args.Length:
            outputPath = args[++i];
            break;
        case "--format" or "-f" when i + 1 < args.Length:
            format = args[++i].ToLowerInvariant();
            break;
        case "--help" or "-h":
            PrintUsage();
            return;
    }
}

// Build a scaled profile. Counts are allocated with remainder handling so
// smaller smoke-test runs generate exactly the requested claim count.
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

var profile = new CorpusProfile
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
        CobCount = edgeCounts["cob"],
        RetroEligibilityCount = edgeCounts["retroEligibility"],
        NewbornCount = edgeCounts["newborn"],
        PriorAuthCount = edgeCounts["priorAuth"],
        SubrogationCount = edgeCounts["subrogation"],
        BehavioralHealthCount = edgeCounts["behavioralHealth"],
        MedicaidCount = edgeCounts["medicaid"]
    }
};

Console.WriteLine($"Million Claim Challenge — Corpus Generator");
Console.WriteLine($"  Claims:  {profile.TotalClaims:N0}");
Console.WriteLine($"  Seed:    {seed}");
Console.WriteLine($"  Output:  {outputPath}");
Console.WriteLine($"  Format:  {format}");
Console.WriteLine();

var refData = new InMemoryReferenceDataProvider();
ICorpusWriter writer = format switch
{
    "fhir" => new FhirBundleWriter(outputPath),
    _ => new JsonCorpusWriter(outputPath)
};

var generator = new ClaimCorpusGenerator(refData);
var progress = new Progress<CorpusProgress>(p =>
{
    Console.Write($"\r  Progress: {p.ClaimsGenerated:N0} / {p.TotalClaims:N0}  ({p.PercentComplete:F1}%)  [{p.ElapsedTime:mm\\:ss}]");
});

await using (writer)
{
    var result = await generator.GenerateCorpusAsync(profile, writer, progress);

    Console.WriteLine();
    Console.WriteLine();
    Console.WriteLine($"  Done in {result.ElapsedTime:mm\\:ss\\.fff}");
    Console.WriteLine($"  Professional:  {result.ProfessionalCount:N0}");
    Console.WriteLine($"  Institutional: {result.InstitutionalCount:N0}");
    Console.WriteLine($"  Dental:        {result.DentalCount:N0}");
    Console.WriteLine($"  Edge Cases:    {result.EdgeCaseCount:N0}");
    Console.WriteLine($"  Total:         {result.TotalClaims:N0}");
    Console.WriteLine();
    Console.WriteLine($"  Output written to: {outputPath}");
}

static void PrintUsage()
{
    Console.WriteLine("""
    Million Claim Challenge — Corpus Generator

    Usage: mcc-runner [options]

    Options:
      -n, --claims <count>   Number of claims to generate (default: 1000)
      -s, --seed <seed>      Random seed for reproducibility (default: 42)
      -o, --output <path>    Output directory (default: ./mcc-output)
      -f, --format <fmt>     Output format: json or fhir (default: json)
      -h, --help             Show this help

    Examples:
      mcc-runner                              # 1K claims, JSON output
      mcc-runner -n 10000                     # 10K claims
      mcc-runner -n 1000000                   # Full million
      mcc-runner -n 5000 -f fhir -o ./fhir   # 5K claims as FHIR bundles
      mcc-runner -n 1000 -s 123              # Custom seed
    """);
}

static IReadOnlyList<(T Item, int Count)> AllocateCounts<T>(int total, IReadOnlyList<(T Item, double Fraction)> weightedItems)
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
