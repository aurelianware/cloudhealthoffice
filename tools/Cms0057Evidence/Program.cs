using Cms0057Evidence;

// Exit codes: 0 = success, 2 = input/reconciliation error, 3 = acceptance test failures present.
try
{
    var opts = CliOptions.Parse(args);

    var manifest = ManifestLoader.Load(opts.ManifestPath);
    var trx = TrxParser.Parse(opts.ResultsPath);
    var traits = opts.TestAssemblyPath is null
        ? Array.Empty<ScenarioTrait>()
        : TraitReader.Read(opts.TestAssemblyPath);

    var identity = BuildIdentity(opts.Environment);
    var report = EvidenceBuilder.Build(manifest, traits, trx, identity);

    Directory.CreateDirectory(opts.OutputDir);
    WriteFile(Path.Combine(opts.OutputDir, "cms0057-evidence.json"), EvidenceWriters.ToJson(report));
    WriteFile(Path.Combine(opts.OutputDir, "cms0057-evidence.md"), EvidenceWriters.ToMarkdown(report));
    WriteFile(Path.Combine(opts.OutputDir, "cms0057-evidence.html"), EvidenceWriters.ToHtml(report));

    Console.WriteLine($"CMS-0057-F evidence written to {opts.OutputDir}");
    Console.WriteLine($"  scenarios: {report.Scenarios.Count}  tests: {report.TestSummary.Passed} passed / "
                      + $"{report.TestSummary.Failed} failed / {report.TestSummary.Skipped} skipped");

    // Sanitized public snapshot — only ever from a fully passing run. When tests
    // failed we deliberately do NOT write it (fail-safe); the raw evidence above is
    // still written and the process exits non-zero below.
    if (opts.PublicOutputPath is not null && !report.HasTestFailures)
    {
        var publicEvidence = PublicEvidenceProjector.Project(report);
        var publicDir = Path.GetDirectoryName(Path.GetFullPath(opts.PublicOutputPath));
        if (!string.IsNullOrEmpty(publicDir)) Directory.CreateDirectory(publicDir);
        WriteFile(opts.PublicOutputPath, EvidenceWriters.ToPublicJson(publicEvidence));
        Console.WriteLine($"  public snapshot: {opts.PublicOutputPath}");
    }

    if (report.HasTestFailures)
    {
        Console.Error.WriteLine($"::error::{report.TestSummary.Failed} acceptance test(s) failed — evidence written but marking failure.");
        return 3;
    }
    return 0;
}
catch (CliException ex)
{
    Console.Error.WriteLine($"::error::{ex.Message}");
    Console.Error.WriteLine(CliOptions.Usage);
    return 2;
}
catch (Exception ex) when (ex is ManifestException or TrxException or EvidenceReconciliationException or FileNotFoundException)
{
    Console.Error.WriteLine($"::error::{ex.Message}");
    return 2;
}

static EvidenceIdentity BuildIdentity(string environment) => new()
{
    GeneratedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
    Repository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY"),
    CommitSha = Environment.GetEnvironmentVariable("GITHUB_SHA"),
    Ref = Environment.GetEnvironmentVariable("GITHUB_REF_NAME"),
    WorkflowRunId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
    WorkflowRunNumber = Environment.GetEnvironmentVariable("GITHUB_RUN_NUMBER"),
    Environment = environment,
    TestDataClassification = "synthetic",
    Framework = ".NET 8 / xUnit",
    FhirVersion = "R4",
};

// Deterministic output: LF line endings, UTF-8 without BOM.
static void WriteFile(string path, string content)
    => File.WriteAllText(path, content.Replace("\r\n", "\n"), new System.Text.UTF8Encoding(false));

internal sealed class CliException : Exception
{
    public CliException(string message) : base(message) { }
}

internal sealed class CliOptions
{
    public string ManifestPath { get; private init; } = "";
    public string ResultsPath { get; private init; } = "";
    public string OutputDir { get; private init; } = "";
    public string? TestAssemblyPath { get; private init; }
    public string? PublicOutputPath { get; private init; }
    public string Environment { get; private init; } = "local";

    public const string Usage =
        "Usage: Cms0057Evidence --manifest <scenarios.json> --results <results.trx> " +
        "--output <dir> [--test-assembly <Cms0057Acceptance.Tests.dll>] " +
        "[--public-output <public-evidence.json>] [--environment <name>]";

    public static CliOptions Parse(string[] args)
    {
        string? manifest = null, results = null, output = null, testAssembly = null, publicOutput = null, env = null;
        for (var i = 0; i < args.Length; i++)
        {
            string Next(string flag) => i + 1 < args.Length
                ? args[++i]
                : throw new CliException($"Missing value for {flag}");
            switch (args[i])
            {
                case "--manifest": manifest = Next("--manifest"); break;
                case "--results": results = Next("--results"); break;
                case "--output": output = Next("--output"); break;
                case "--test-assembly": testAssembly = Next("--test-assembly"); break;
                case "--public-output": publicOutput = Next("--public-output"); break;
                case "--environment": env = Next("--environment"); break;
                default: throw new CliException($"Unknown argument: {args[i]}");
            }
        }
        if (manifest is null) throw new CliException("--manifest is required");
        if (results is null) throw new CliException("--results is required");
        if (output is null) throw new CliException("--output is required");

        return new CliOptions
        {
            ManifestPath = manifest,
            ResultsPath = results,
            OutputDir = output,
            TestAssemblyPath = testAssembly,
            PublicOutputPath = publicOutput,
            Environment = env ?? "local",
        };
    }
}
