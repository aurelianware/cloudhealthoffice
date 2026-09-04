using System.Text.Json.Serialization;

namespace Cms0057Evidence;

/// <summary>Execution-time identity of the evidence (who/what/when was tested).</summary>
public sealed class EvidenceIdentity
{
    [JsonPropertyName("generatedAtUtc")] public string GeneratedAtUtc { get; init; } = "";
    [JsonPropertyName("repository")] public string? Repository { get; init; }
    [JsonPropertyName("commitSha")] public string? CommitSha { get; init; }
    [JsonPropertyName("ref")] public string? Ref { get; init; }
    [JsonPropertyName("workflowRunId")] public string? WorkflowRunId { get; init; }
    [JsonPropertyName("workflowRunNumber")] public string? WorkflowRunNumber { get; init; }
    [JsonPropertyName("environment")] public string Environment { get; init; } = "local";
    [JsonPropertyName("testDataClassification")] public string TestDataClassification { get; init; } = "synthetic";
    [JsonPropertyName("framework")] public string Framework { get; init; } = ".NET 8 / xUnit";
    [JsonPropertyName("fhirVersion")] public string FhirVersion { get; init; } = "R4";
}

public sealed class TestExecutionSummary
{
    [JsonPropertyName("passed")] public int Passed { get; init; }
    [JsonPropertyName("failed")] public int Failed { get; init; }
    [JsonPropertyName("skipped")] public int Skipped { get; init; }
    [JsonPropertyName("total")] public int Total { get; init; }
}

public sealed class BackendEvidence
{
    /// <summary>e.g. "replace.cloudHealthOffice", "augment.qnxt".</summary>
    [JsonPropertyName("backend")] public string Backend { get; init; } = "";

    /// <summary>Declared capability status from the manifest (PASSABLE/PARTIAL/GAP/N/A).</summary>
    [JsonPropertyName("declaredStatus")] public string DeclaredStatus { get; init; } = "";

    /// <summary>Aggregate outcome of the associated tests: Passed / Failed / NotRun.</summary>
    [JsonPropertyName("testExecutionStatus")] public string TestExecutionStatus { get; init; } = "";

    [JsonPropertyName("supportingTests")] public List<string> SupportingTests { get; init; } = new();
    [JsonPropertyName("rationale")] public string? Rationale { get; init; }
}

public sealed class ScenarioEvidence
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("capability")] public string Capability { get; init; } = "";
    [JsonPropertyName("backends")] public List<BackendEvidence> Backends { get; init; } = new();
}

public sealed class GapEntry
{
    [JsonPropertyName("scenarioId")] public string ScenarioId { get; init; } = "";
    [JsonPropertyName("backend")] public string Backend { get; init; } = "";
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("rationale")] public string? Rationale { get; init; }
}

public sealed class EvidenceReport
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }
    [JsonPropertyName("identity")] public EvidenceIdentity Identity { get; init; } = new();
    [JsonPropertyName("testSummary")] public TestExecutionSummary TestSummary { get; init; } = new();
    [JsonPropertyName("scenarios")] public List<ScenarioEvidence> Scenarios { get; init; } = new();
    [JsonPropertyName("knownGaps")] public List<GapEntry> KnownGaps { get; init; } = new();

    /// <summary>True when any associated acceptance test failed.</summary>
    [JsonIgnore] public bool HasTestFailures => TestSummary.Failed > 0;
}

public static class ExecutionStatus
{
    public const string Passed = "Passed";
    public const string Failed = "Failed";
    public const string NotRun = "NotRun";
}

public static class BackendIds
{
    public const string Replace = "replace.cloudHealthOffice";
    public static string Augment(string key) => $"augment.{key}";
}
