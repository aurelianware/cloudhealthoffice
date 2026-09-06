using System.Text.Json.Serialization;

namespace DaVinciInterop.Tests.Harness;

/// <summary>An external target as it appears in an evidence run.</summary>
public sealed record InteropEvidenceTarget
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("key")] public string Key { get; init; } = "";
    [JsonPropertyName("role")] public string Role { get; init; } = "";
    [JsonPropertyName("upstreamRepository")] public string UpstreamRepository { get; init; } = "";
    [JsonPropertyName("license")] public string License { get; init; } = "";

    /// <summary>The exact pinned version: image digest, or tag plus commit.</summary>
    [JsonPropertyName("version")] public string Version { get; init; } = "";

    [JsonPropertyName("pinReference")] public string PinReference { get; init; } = "";
    [JsonPropertyName("sourceCommit")] public string? SourceCommit { get; init; }
    [JsonPropertyName("implementationGuides")] public Dictionary<string, string> ImplementationGuides { get; init; } = new();

    /// <summary>Results recorded against this target this run.</summary>
    [JsonPropertyName("results")] public List<InteropResult> Results { get; init; } = new();
}

/// <summary>Counts by outcome. Deliberately never combined with CMS acceptance totals.</summary>
public sealed record InteropRunSummary
{
    [JsonPropertyName("passed")] public int Passed { get; init; }
    [JsonPropertyName("failed")] public int Failed { get; init; }
    [JsonPropertyName("skipped")] public int Skipped { get; init; }
    [JsonPropertyName("notRun")] public int NotRun { get; init; }
    [JsonPropertyName("total")] public int Total { get; init; }
}

/// <summary>
/// The machine-readable interoperability evidence document
/// (artifacts/interop/run.json).
///
/// Separate from CMS-0057-F acceptance evidence by design, and separate in
/// vocabulary too: Passed / Failed / Skipped / NotRun, never PASSABLE / PARTIAL /
/// GAP. The two documents answer different questions and must never be added
/// together into one score. An interop pass says an independent implementation
/// accepted CHO's request and CHO accepted its response; it says nothing about
/// CMS certification.
/// </summary>
public sealed record InteropEvidenceRun
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;

    /// <summary>Always "davinci-interoperability" — so this document can never be mistaken for CMS acceptance evidence.</summary>
    [JsonPropertyName("evidenceKind")] public string EvidenceKind { get; init; } = "davinci-interoperability";

    [JsonPropertyName("generatedAtUtc")] public string GeneratedAtUtc { get; init; } = "";
    [JsonPropertyName("choCommit")] public string? ChoCommit { get; init; }
    [JsonPropertyName("repository")] public string? Repository { get; init; }
    [JsonPropertyName("environment")] public string Environment { get; init; } = "local";
    [JsonPropertyName("dataClassification")] public string DataClassification { get; init; } = "synthetic";
    [JsonPropertyName("summary")] public InteropRunSummary Summary { get; init; } = new();
    [JsonPropertyName("targets")] public List<InteropEvidenceTarget> Targets { get; init; } = new();

    /// <summary>Inventory scenarios with no result this run, reported as NotRun.</summary>
    [JsonPropertyName("notRunScenarios")] public List<InteropResult> NotRunScenarios { get; init; } = new();

    /// <summary>Every finding across the run, hoisted for review.</summary>
    [JsonPropertyName("findings")] public List<InteropFinding> Findings { get; init; } = new();

    /// <summary>
    /// States plainly, inside the artifact itself, that this evidence does not
    /// feed the CMS-0057-F acceptance status of any scenario.
    /// </summary>
    [JsonPropertyName("relationshipToCmsAcceptance")]
    public string RelationshipToCmsAcceptance { get; init; } =
        "Independent of CMS-0057-F acceptance evidence. External interoperability results never change a " +
        "CMS-0057-F scenario status, and CMS acceptance statuses never imply an interoperability result. " +
        "See docs/interop/davinci.md and docs/compliance/CMS0057-ACCEPTANCE-INVENTORY.md.";
}
