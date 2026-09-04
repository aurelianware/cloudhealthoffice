using System.Text.Json.Serialization;

namespace Cms0057Evidence;

/// <summary>
/// Sanitized, public-safe projection of an <see cref="EvidenceReport"/>.
///
/// This type is built by an ALLOW-LIST (see <see cref="PublicEvidenceProjector"/>):
/// only the fields declared here are ever copied out of the raw report. Supporting
/// test names, rationales, workflow run ids, environment, file paths, and any other
/// internal detail are never present, because they have no home on this object.
///
/// Status strings are DECLARED capability status (PASSABLE / PARTIAL / GAP / N/A),
/// never test-execution status: a passing GAP-assertion test confirms a gap and is
/// reported as GAP, not as a pass.
/// </summary>
public sealed class PublicEvidence
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }

    /// <summary>Always "validated": a public snapshot is only ever produced from a
    /// fully passing, reconciled run (see the projector's failure guard).</summary>
    [JsonPropertyName("evidenceStatus")] public string EvidenceStatus { get; init; } = "validated";

    [JsonPropertyName("generatedAtUtc")] public string GeneratedAtUtc { get; init; } = "";
    [JsonPropertyName("commitSha")] public string? CommitSha { get; init; }
    [JsonPropertyName("commitShaShort")] public string? CommitShaShort { get; init; }

    /// <summary>Durable public URL to the tested source revision, when derivable.</summary>
    [JsonPropertyName("sourceCommitUrl")] public string? SourceCommitUrl { get; init; }

    [JsonPropertyName("testDataClassification")] public string TestDataClassification { get; init; } = "synthetic";
    [JsonPropertyName("framework")] public string Framework { get; init; } = "";
    [JsonPropertyName("fhirVersion")] public string FhirVersion { get; init; } = "";

    [JsonPropertyName("scenarioCount")] public int ScenarioCount { get; init; }
    [JsonPropertyName("testSummary")] public PublicTestSummary TestSummary { get; init; } = new();

    /// <summary>Cloud Health Office Replace — product capability — declared-status counts.</summary>
    [JsonPropertyName("replaceSummary")] public StatusCounts ReplaceSummary { get; init; } = new();

    /// <summary>External-core (Augment) integration capability, keyed by backend
    /// (e.g. "qnxt"). Reported separately from product capability. Only backends
    /// present in the manifest appear here.</summary>
    [JsonPropertyName("integrations")]
    public SortedDictionary<string, StatusCounts> Integrations { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("scenarios")] public List<PublicScenario> Scenarios { get; init; } = new();

    [JsonPropertyName("disclaimers")] public List<string> Disclaimers { get; init; } = new();
}

public sealed class PublicTestSummary
{
    [JsonPropertyName("passed")] public int Passed { get; init; }
    [JsonPropertyName("failed")] public int Failed { get; init; }
    [JsonPropertyName("skipped")] public int Skipped { get; init; }
}

/// <summary>Declared-status counts for one capability dimension.</summary>
public sealed class StatusCounts
{
    [JsonPropertyName("passable")] public int Passable { get; init; }
    [JsonPropertyName("partial")] public int Partial { get; init; }
    [JsonPropertyName("gap")] public int Gap { get; init; }
    [JsonPropertyName("na")] public int Na { get; init; }
}

public sealed class PublicScenario
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("capability")] public string Capability { get; init; } = "";

    /// <summary>Declared CHO Replace (product) capability status.</summary>
    [JsonPropertyName("replace")] public string Replace { get; init; } = "";

    /// <summary>Declared integration status per external-core backend key.</summary>
    [JsonPropertyName("integrations")]
    public SortedDictionary<string, string> Integrations { get; init; } = new(StringComparer.Ordinal);
}
