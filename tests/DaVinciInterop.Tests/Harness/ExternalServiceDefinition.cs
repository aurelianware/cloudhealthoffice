using System.Text.Json.Serialization;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// The role an implementation plays in an exchange. CHO is not always the payer
/// and not always the client, so the role is carried per-participant rather than
/// assumed by the harness.
/// </summary>
public enum InteropRole
{
    /// <summary>The external system serves FHIR/CDS Hooks; CHO drives it.</summary>
    ExternalServer,

    /// <summary>The external system drives CHO; CHO serves.</summary>
    ExternalClient,

    /// <summary>The external system is a conformance runner (e.g. Inferno) executing a suite.</summary>
    ConformanceRunner,
}

/// <summary>Which side of an exchange Cloud Health Office is on for a scenario.</summary>
public enum ChoRole
{
    /// <summary>CHO issues the requests.</summary>
    Client,

    /// <summary>CHO answers the requests.</summary>
    Server,
}

/// <summary>How the harness reproduces an external implementation.</summary>
public enum PinKind
{
    /// <summary>A container image referenced by immutable digest.</summary>
    ImageDigest,

    /// <summary>An upstream release tag, recorded together with its commit SHA.</summary>
    ReleaseTag,

    /// <summary>A bare upstream commit SHA.</summary>
    Commit,
}

/// <summary>The exact, reproducible upstream version an external target is pinned to.</summary>
public sealed record ExternalPin
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("image")] public string? Image { get; init; }
    [JsonPropertyName("digest")] public string? Digest { get; init; }

    /// <summary>The value actually handed to Docker or git: image@digest, or repo@tag.</summary>
    [JsonPropertyName("reference")] public string Reference { get; init; } = "";

    [JsonPropertyName("tag")] public string? Tag { get; init; }
    [JsonPropertyName("commit")] public string? Commit { get; init; }
    [JsonPropertyName("sourceCommit")] public string? SourceCommit { get; init; }
    [JsonPropertyName("sourceCommitProvenance")] public string? SourceCommitProvenance { get; init; }
    [JsonPropertyName("imageCreatedUtc")] public string? ImageCreatedUtc { get; init; }
    [JsonPropertyName("originTag")] public string? OriginTag { get; init; }

    public PinKind ParsedKind => Kind switch
    {
        "imageDigest" => PinKind.ImageDigest,
        "releaseTag" => PinKind.ReleaseTag,
        "commit" => PinKind.Commit,
        _ => throw new InvalidOperationException($"Unknown pin kind '{Kind}'."),
    };

    /// <summary>
    /// A pin is reproducible only when it names an immutable upstream artifact.
    /// A floating tag (`:latest`, `:main`) or a bare branch name is not.
    /// </summary>
    public bool IsReproducible => ParsedKind switch
    {
        PinKind.ImageDigest => !string.IsNullOrWhiteSpace(Digest)
                               && Digest.StartsWith("sha256:", StringComparison.Ordinal)
                               && Reference.Contains('@'),
        PinKind.ReleaseTag => !string.IsNullOrWhiteSpace(Tag) && IsSha(Commit),
        PinKind.Commit => IsSha(Commit),
        _ => false,
    };

    private static bool IsSha(string? value) =>
        value is { Length: 40 } && value.All(Uri.IsHexDigit);
}

/// <summary>Compose placement for an external target.</summary>
public sealed record ComposePlacement
{
    [JsonPropertyName("service")] public string Service { get; init; } = "";
    [JsonPropertyName("profiles")] public List<string> Profiles { get; init; } = new();
}

/// <summary>
/// The endpoints an external target exposes. Centralized here so scenarios never
/// hard-code a base URL or a port.
/// </summary>
public sealed record ExternalEndpoints
{
    [JsonPropertyName("fhirBaseUrl")] public string? FhirBaseUrl { get; init; }
    [JsonPropertyName("cdsHooksBaseUrl")] public string? CdsHooksBaseUrl { get; init; }
    [JsonPropertyName("readinessUrl")] public string? ReadinessUrl { get; init; }
}

/// <summary>
/// Authentication expected by an external target. Synthetic only: the harness has
/// no path that presents a real credential, and nothing here alters how CHO
/// itself authenticates callers.
/// </summary>
public sealed record ExternalAuth
{
    /// <summary>"None" or "SmartClientCredentials".</summary>
    [JsonPropertyName("mode")] public string Mode { get; init; } = "None";

    [JsonPropertyName("tokenUrl")] public string? TokenUrl { get; init; }
    [JsonPropertyName("clientId")] public string? ClientId { get; init; }
    [JsonPropertyName("scope")] public string? Scope { get; init; }
    [JsonPropertyName("note")] public string? Note { get; init; }
}

/// <summary>Inferno-specific runner configuration for a conformance kit.</summary>
public sealed record InfernoRunnerConfig
{
    [JsonPropertyName("apiBaseUrl")] public string ApiBaseUrl { get; init; } = "";
    [JsonPropertyName("suites")] public List<string> Suites { get; init; } = new();
    [JsonPropertyName("runner")] public string? Runner { get; init; }
}

/// <summary>
/// One independent implementation the harness can orchestrate: what it is, where
/// it came from, exactly which version, how to start it, how to know it is ready,
/// and where to talk to it.
/// </summary>
public sealed record ExternalServiceDefinition
{
    /// <summary>Upstream project name, e.g. "HL7-DaVinci/br-payer".</summary>
    [JsonPropertyName("name")] public string Name { get; init; } = "";

    /// <summary>Short key used by scenarios and compose, e.g. "br-payer".</summary>
    [JsonPropertyName("key")] public string Key { get; init; } = "";

    [JsonPropertyName("role")] public string Role { get; init; } = "";
    [JsonPropertyName("protocols")] public List<string> Protocols { get; init; } = new();
    [JsonPropertyName("upstreamRepository")] public string UpstreamRepository { get; init; } = "";
    [JsonPropertyName("license")] public string License { get; init; } = "";
    [JsonPropertyName("pin")] public ExternalPin Pin { get; init; } = new();
    [JsonPropertyName("compose")] public ComposePlacement Compose { get; init; } = new();
    [JsonPropertyName("endpoints")] public ExternalEndpoints Endpoints { get; init; } = new();
    [JsonPropertyName("auth")] public ExternalAuth Auth { get; init; } = new();
    [JsonPropertyName("inferno")] public InfernoRunnerConfig? Inferno { get; init; }

    /// <summary>IG versions this target implements, keyed by protocol (CRD/DTR/PAS/PDex/CDex).</summary>
    [JsonPropertyName("implementationGuides")] public Dictionary<string, string> ImplementationGuides { get; init; } = new();

    [JsonPropertyName("igVersionProvenance")] public string? IgVersionProvenance { get; init; }
    [JsonPropertyName("requiredEgress")] public List<string> RequiredEgress { get; init; } = new();
    [JsonPropertyName("startupNotes")] public string? StartupNotes { get; init; }

    public InteropRole ParsedRole => Enum.Parse<InteropRole>(Role);

    /// <summary>The version string recorded in evidence for this target.</summary>
    public string EvidenceVersion => Pin.ParsedKind switch
    {
        PinKind.ImageDigest => Pin.Digest ?? Pin.Reference,
        PinKind.ReleaseTag => $"{Pin.Tag} ({Pin.Commit})",
        _ => Pin.Commit ?? Pin.Reference,
    };
}

/// <summary>
/// A pinned upstream content source (CQL, PlanDefinitions, Questionnaires) that
/// scenarios may point at. Never vendored into this repository.
/// </summary>
public sealed record ExternalContentSource
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("key")] public string Key { get; init; } = "";
    [JsonPropertyName("purpose")] public string Purpose { get; init; } = "";
    [JsonPropertyName("upstreamRepository")] public string UpstreamRepository { get; init; } = "";
    [JsonPropertyName("license")] public string License { get; init; } = "";
    [JsonPropertyName("pin")] public ExternalPin Pin { get; init; } = new();
    [JsonPropertyName("vendored")] public bool Vendored { get; init; }
    [JsonPropertyName("note")] public string? Note { get; init; }
}
