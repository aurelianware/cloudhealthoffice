using System.Text.Json.Serialization;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// Outcome of one interoperability scenario.
///
/// Deliberately NOT the CMS-0057-F acceptance vocabulary (PASSABLE / PARTIAL /
/// GAP). Those words describe how completely CHO implements a regulated
/// capability; these describe whether one exchange with an independent
/// implementation worked. Interop success is not CMS certification, and the two
/// must never be read as the same scale.
/// </summary>
public enum InteropStatus
{
    /// <summary>The exchange happened and every assertion held.</summary>
    Passed,

    /// <summary>The exchange happened and an assertion failed, or the exchange could not complete.</summary>
    Failed,

    /// <summary>Deliberately not executed this run (opt-in not enabled, prerequisite absent).</summary>
    Skipped,

    /// <summary>Defined in the inventory but not implemented or not selected for this run.</summary>
    NotRun,
}

/// <summary>How serious an observed standards discrepancy is.</summary>
public enum FindingSeverity
{
    /// <summary>Worth recording; does not affect the scenario outcome.</summary>
    Info,

    /// <summary>A real discrepancy between the two implementations; does not fail the scenario on its own.</summary>
    Warning,

    /// <summary>An assertion the scenario requires to hold. Fails the scenario.</summary>
    Error,
}

/// <summary>
/// A single sanitized HTTP interaction. Bodies are stored as separate artifact
/// files; this record summarizes the exchange and points at them.
/// </summary>
public sealed record InteropInteraction
{
    [JsonPropertyName("sequence")] public int Sequence { get; init; }
    [JsonPropertyName("target")] public string Target { get; init; } = "";
    [JsonPropertyName("method")] public string Method { get; init; } = "";
    [JsonPropertyName("url")] public string Url { get; init; } = "";
    [JsonPropertyName("requestContentType")] public string? RequestContentType { get; init; }
    [JsonPropertyName("statusCode")] public int StatusCode { get; init; }
    [JsonPropertyName("responseContentType")] public string? ResponseContentType { get; init; }
    [JsonPropertyName("durationMs")] public long DurationMs { get; init; }

    /// <summary>resourceType of the parsed response body, when it is FHIR.</summary>
    [JsonPropertyName("responseResourceType")] public string? ResponseResourceType { get; init; }

    /// <summary>Issue summaries when the response was an OperationOutcome.</summary>
    [JsonPropertyName("operationOutcomeIssues")] public List<string> OperationOutcomeIssues { get; init; } = new();

    /// <summary>Sanitized request headers actually sent (values redacted where sensitive).</summary>
    [JsonPropertyName("requestHeaders")] public Dictionary<string, string> RequestHeaders { get; init; } = new();

    /// <summary>Artifact-relative path of the captured request body, if any.</summary>
    [JsonPropertyName("requestArtifact")] public string? RequestArtifact { get; init; }

    /// <summary>Artifact-relative path of the captured response body, if any.</summary>
    [JsonPropertyName("responseArtifact")] public string? ResponseArtifact { get; init; }

    /// <summary>Set when the request never produced a response (connect failure, timeout).</summary>
    [JsonPropertyName("transportError")] public string? TransportError { get; init; }
}

/// <summary>
/// A standards observation from an exchange. A discrepancy may mean a CHO bug, an
/// upstream RI bug, an IG ambiguity or a version mismatch — the finding records
/// what was seen and does not assign blame.
/// </summary>
public sealed record InteropFinding
{
    [JsonPropertyName("severity")] public string Severity { get; init; } = nameof(FindingSeverity.Info);
    [JsonPropertyName("code")] public string Code { get; init; } = "";
    [JsonPropertyName("summary")] public string Summary { get; init; } = "";
    [JsonPropertyName("choObserved")] public string? ChoObserved { get; init; }
    [JsonPropertyName("externalObserved")] public string? ExternalObserved { get; init; }
    [JsonPropertyName("specificationReference")] public string? SpecificationReference { get; init; }

    public static InteropFinding Info(string code, string summary) =>
        new() { Severity = nameof(FindingSeverity.Info), Code = code, Summary = summary };

    public static InteropFinding Warning(string code, string summary, string? cho = null, string? external = null, string? spec = null) =>
        new()
        {
            Severity = nameof(FindingSeverity.Warning),
            Code = code,
            Summary = summary,
            ChoObserved = cho,
            ExternalObserved = external,
            SpecificationReference = spec,
        };

    public static InteropFinding Error(string code, string summary, string? cho = null, string? external = null, string? spec = null) =>
        new()
        {
            Severity = nameof(FindingSeverity.Error),
            Code = code,
            Summary = summary,
            ChoObserved = cho,
            ExternalObserved = external,
            SpecificationReference = spec,
        };
}

/// <summary>Which IG versions each side of an exchange was operating under.</summary>
public sealed record ProtocolCompatibility
{
    /// <summary>The IG version CHO targets for this protocol.</summary>
    [JsonPropertyName("cho")] public string? Cho { get; init; }

    /// <summary>The IG version the external implementation reported or declared.</summary>
    [JsonPropertyName("external")] public string? External { get; init; }

    /// <summary>True when the two sides are not on the same IG version.</summary>
    [JsonPropertyName("mismatch")] public bool Mismatch { get; init; }

    [JsonPropertyName("note")] public string? Note { get; init; }
}

/// <summary>Environment the run happened in. Never carries credentials.</summary>
public sealed record InteropEnvironmentMetadata
{
    [JsonPropertyName("environment")] public string Environment { get; init; } = "local";
    [JsonPropertyName("os")] public string Os { get; init; } = "";
    [JsonPropertyName("architecture")] public string Architecture { get; init; } = "";
    [JsonPropertyName("framework")] public string Framework { get; init; } = "";
    [JsonPropertyName("fhirLibrary")] public string FhirLibrary { get; init; } = "";
    [JsonPropertyName("dataClassification")] public string DataClassification { get; init; } = "synthetic";
}

/// <summary>
/// The result of one external interaction scenario: what ran, against which pinned
/// upstream version, from which CHO commit, and what was observed.
/// </summary>
public sealed record InteropResult
{
    [JsonPropertyName("scenarioId")] public string ScenarioId { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("protocol")] public string Protocol { get; init; } = "";
    [JsonPropertyName("choRole")] public string ChoRole { get; init; } = "";
    [JsonPropertyName("target")] public string Target { get; init; } = "";
    [JsonPropertyName("targetVersion")] public string TargetVersion { get; init; } = "";
    [JsonPropertyName("targetImageReference")] public string? TargetImageReference { get; init; }
    [JsonPropertyName("targetSourceCommit")] public string? TargetSourceCommit { get; init; }
    [JsonPropertyName("choCommit")] public string? ChoCommit { get; init; }
    [JsonPropertyName("startedAtUtc")] public string StartedAtUtc { get; init; } = "";
    [JsonPropertyName("endedAtUtc")] public string EndedAtUtc { get; init; } = "";
    [JsonPropertyName("durationMs")] public long DurationMs { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = nameof(InteropStatus.NotRun);
    [JsonPropertyName("statusReason")] public string? StatusReason { get; init; }
    [JsonPropertyName("interactions")] public List<InteropInteraction> Interactions { get; init; } = new();
    [JsonPropertyName("findings")] public List<InteropFinding> Findings { get; init; } = new();
    [JsonPropertyName("compatibility")] public Dictionary<string, ProtocolCompatibility> Compatibility { get; init; } = new();
    [JsonPropertyName("environment")] public InteropEnvironmentMetadata Environment { get; init; } = new();

    [JsonIgnore] public InteropStatus ParsedStatus => Enum.Parse<InteropStatus>(Status);
}
