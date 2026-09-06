using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// Writes the sanitized evidence package for an interop run.
///
///   artifacts/interop/
///     run.json          machine-readable evidence (InteropEvidenceRun)
///     junit.xml         the same outcomes in a format CI understands
///     requests/         redacted request bodies the harness sent
///     responses/        redacted response bodies the external system returned
///     service-logs/     tail of each external container's log
///
/// Nothing written here carries a bearer token, a client secret, a private key or
/// PHI: every body and log passes through <see cref="Redaction"/>, header values
/// are redacted at capture time, and the only data the harness ever sends is
/// <see cref="SyntheticInteropData"/>. Raw container logs are never published as
/// they are — run.json is the artifact intended for later publication.
/// </summary>
public sealed class InteropEvidenceWriter
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Cap on a single captured body. A CapabilityStatement from a fully loaded
    /// HAPI server is a couple of megabytes; the cap keeps an artifact bundle
    /// reviewable without discarding the part that shows what was exchanged.
    /// </summary>
    private const int MaxCapturedBodyBytes = 2 * 1024 * 1024;

    /// <summary>UTF-8 without a BOM: the artifacts are consumed by jq, Python and CI parsers.</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _root;

    public InteropEvidenceWriter(string? artifactsRoot = null) =>
        _root = artifactsRoot ?? InteropPaths.ArtifactsRoot;

    public string Root => _root;

    /// <summary>
    /// Builds the run document from the executed results plus the inventory, so
    /// scenarios that did not run this time are reported NotRun rather than
    /// silently omitted.
    /// </summary>
    public static InteropEvidenceRun BuildRun(
        InteropVersions versions,
        InteropScenarioInventory inventory,
        IReadOnlyList<InteropResult> results)
    {
        var executedIds = results.Select(r => r.ScenarioId).ToHashSet(StringComparer.Ordinal);

        var targets = new List<InteropEvidenceTarget>();
        foreach (var definition in versions.Targets)
        {
            var targetResults = results.Where(r => r.Target == definition.Name).ToList();
            targets.Add(new InteropEvidenceTarget
            {
                Name = definition.Name,
                Key = definition.Key,
                Role = definition.Role,
                UpstreamRepository = definition.UpstreamRepository,
                License = definition.License,
                Version = definition.EvidenceVersion,
                PinReference = definition.Pin.Reference,
                SourceCommit = definition.Pin.SourceCommit ?? definition.Pin.Commit,
                ImplementationGuides = new Dictionary<string, string>(definition.ImplementationGuides),
                Results = targetResults,
            });
        }

        var notRun = inventory.Scenarios
            .Where(s => !executedIds.Contains(s.Id))
            .Select(s => NotRunResult(s, versions))
            .ToList();

        var all = results.Concat(notRun).ToList();

        return new InteropEvidenceRun
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            ChoCommit = InteropSettings.ChoCommit,
            Repository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY"),
            Environment = InteropSettings.EnvironmentLabel,
            Summary = new InteropRunSummary
            {
                Passed = all.Count(r => r.ParsedStatus == InteropStatus.Passed),
                Failed = all.Count(r => r.ParsedStatus == InteropStatus.Failed),
                Skipped = all.Count(r => r.ParsedStatus == InteropStatus.Skipped),
                NotRun = all.Count(r => r.ParsedStatus == InteropStatus.NotRun),
                Total = all.Count,
            },
            Targets = targets,
            NotRunScenarios = notRun,
            Findings = results.SelectMany(r => r.Findings).ToList(),
        };
    }

    private static InteropResult NotRunResult(InteropScenarioDefinition scenario, InteropVersions versions)
    {
        string version = "";
        string? pin = null;
        var targetName = scenario.ExternalTarget;
        try
        {
            var target = versions.Target(scenario.ExternalTarget);
            targetName = target.Name;
            version = target.EvidenceVersion;
            pin = target.Pin.Reference;
        }
        catch (KeyNotFoundException)
        {
            // A scenario may name a target that has not been pinned yet; report it
            // as NotRun against the raw target key rather than failing the writer.
        }

        return new InteropResult
        {
            ScenarioId = scenario.Id,
            Title = scenario.Title,
            Protocol = scenario.Protocol,
            ChoRole = scenario.ChoRole,
            Target = targetName,
            TargetVersion = version,
            TargetImageReference = pin,
            ChoCommit = InteropSettings.ChoCommit,
            Status = nameof(InteropStatus.NotRun),
            StatusReason = scenario.Implemented
                ? "Implemented but not selected for this run."
                : "Placeholder in the scenario inventory; no scenario implementation exists yet.",
        };
    }

    /// <summary>Writes the full evidence package and returns the run document path.</summary>
    public string Write(
        InteropEvidenceRun run,
        IReadOnlyDictionary<string, string>? capturedBodies = null,
        IReadOnlyDictionary<string, string>? serviceLogs = null)
    {
        Directory.CreateDirectory(_root);

        var runPath = Path.Combine(_root, "run.json");
        File.WriteAllText(runPath, JsonSerializer.Serialize(run, WriteOptions), Utf8NoBom);

        File.WriteAllText(Path.Combine(_root, "junit.xml"), BuildJUnit(run), Utf8NoBom);

        foreach (var (relativePath, body) in capturedBodies ?? new Dictionary<string, string>())
        {
            var destination = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            // Bodies are redacted at capture time; redact again on write so a body
            // that reached the writer by another path cannot skip the pass.
            File.WriteAllText(destination, Truncate(Redaction.Body(body)), Utf8NoBom);
        }

        if (serviceLogs is { Count: > 0 })
        {
            var logsDirectory = Path.Combine(_root, "service-logs");
            Directory.CreateDirectory(logsDirectory);
            foreach (var (service, log) in serviceLogs)
            {
                File.WriteAllText(
                    Path.Combine(logsDirectory, $"{SanitizeFileName(service)}.log"),
                    Redaction.Body(log),
                    Utf8NoBom);
            }
        }

        return runPath;
    }

    /// <summary>Renders the run as JUnit XML so CI can surface scenario outcomes.</summary>
    public static string BuildJUnit(InteropEvidenceRun run)
    {
        var results = run.Targets.SelectMany(t => t.Results).Concat(run.NotRunScenarios).ToList();

        var testCases = results.Select(result =>
        {
            var testCase = new XElement("testcase",
                new XAttribute("name", result.ScenarioId),
                new XAttribute("classname", $"DaVinciInterop.{result.Protocol}"),
                new XAttribute("time", (result.DurationMs / 1000.0).ToString("0.000")));

            switch (result.ParsedStatus)
            {
                case InteropStatus.Failed:
                    testCase.Add(new XElement("failure",
                        new XAttribute("message", result.StatusReason ?? "Interoperability scenario failed."),
                        new XText(DescribeFailure(result))));
                    break;
                case InteropStatus.Skipped:
                case InteropStatus.NotRun:
                    testCase.Add(new XElement("skipped",
                        new XAttribute("message", result.StatusReason ?? result.Status)));
                    break;
            }

            return testCase;
        });

        var suite = new XElement("testsuite",
            new XAttribute("name", "DaVinciInterop"),
            new XAttribute("tests", run.Summary.Total),
            new XAttribute("failures", run.Summary.Failed),
            new XAttribute("skipped", run.Summary.Skipped + run.Summary.NotRun),
            new XAttribute("timestamp", run.GeneratedAtUtc),
            testCases);

        return new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("testsuites", suite))
            .ToString();
    }

    private static string DescribeFailure(InteropResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"target: {result.Target} @ {result.TargetVersion}");
        if (result.TargetImageReference is not null)
        {
            builder.AppendLine($"pin: {result.TargetImageReference}");
        }

        foreach (var interaction in result.Interactions)
        {
            builder.AppendLine(
                $"  {interaction.Method} {interaction.Url} -> " +
                (interaction.TransportError is not null
                    ? $"transport error: {interaction.TransportError}"
                    : $"HTTP {interaction.StatusCode} {interaction.ResponseResourceType ?? "(non-FHIR body)"}"));
            foreach (var issue in interaction.OperationOutcomeIssues)
            {
                builder.AppendLine($"    OperationOutcome {issue}");
            }
        }

        foreach (var finding in result.Findings)
        {
            builder.AppendLine($"  [{finding.Severity}] {finding.Code}: {finding.Summary}");
        }

        return builder.ToString();
    }

    private static string Truncate(string body) =>
        body.Length <= MaxCapturedBodyBytes
            ? body
            : body[..MaxCapturedBodyBytes] +
              $"{System.Environment.NewLine}… truncated at {MaxCapturedBodyBytes} characters " +
              $"(original length {body.Length}).";

    private static string SanitizeFileName(string value) =>
        string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
