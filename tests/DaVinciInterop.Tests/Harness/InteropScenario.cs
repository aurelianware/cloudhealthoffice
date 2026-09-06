using System.Text.Json;
using System.Text.Json.Serialization;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// One entry in the scenario inventory (interop/scenarios.json).
///
/// The inventory is data, not code: adding a scenario means adding a row here and
/// a test that executes it, never editing the orchestration. A row with no result
/// in a run is reported <see cref="InteropStatus.NotRun"/> — the harness does not
/// invent a green row for a scenario nobody executed.
/// </summary>
public sealed record InteropScenarioDefinition
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("protocol")] public string Protocol { get; init; } = "";
    [JsonPropertyName("choRole")] public string ChoRole { get; init; } = "";
    [JsonPropertyName("externalTarget")] public string ExternalTarget { get; init; } = "";
    [JsonPropertyName("requiredServices")] public List<string> RequiredServices { get; init; } = new();

    /// <summary>False for a placeholder awaiting a future PR. Never a claim about a result.</summary>
    [JsonPropertyName("implemented")] public bool Implemented { get; init; }

    [JsonPropertyName("description")] public string Description { get; init; } = "";

    public ChoRole ParsedChoRole => Enum.Parse<ChoRole>(ChoRole);
}

/// <summary>The scenario inventory as loaded from interop/scenarios.json.</summary>
public sealed record InteropScenarioInventory
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("scenarios")] public List<InteropScenarioDefinition> Scenarios { get; init; } = new();

    public static InteropScenarioInventory Load() => LoadFrom(InteropPaths.ScenarioInventory);

    public static InteropScenarioInventory LoadFrom(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Scenario inventory not found at '{path}'.", path);
        }

        return JsonSerializer.Deserialize<InteropScenarioInventory>(
                   File.ReadAllText(path),
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException($"'{path}' did not deserialize to a scenario inventory.");
    }

    public InteropScenarioDefinition Scenario(string id) =>
        Scenarios.SingleOrDefault(s => s.Id == id)
        ?? throw new KeyNotFoundException(
            $"No scenario '{id}' in interop/scenarios.json. Known ids: " +
            string.Join(", ", Scenarios.Select(s => s.Id)));
}

/// <summary>
/// Accumulates one scenario's observations and turns them into an
/// <see cref="InteropResult"/>.
///
/// A scenario builds its result as it goes — interactions, findings, IG
/// compatibility — so that a scenario which fails midway still produces evidence
/// describing how far it got and against which pinned upstream version.
/// </summary>
public sealed class InteropScenarioRun
{
    private readonly InteropScenarioDefinition _definition;
    private readonly ExternalServiceDefinition _target;
    private readonly DateTimeOffset _startedAt;
    private readonly List<InteropFinding> _findings = new();
    private readonly Dictionary<string, ProtocolCompatibility> _compatibility = new(StringComparer.Ordinal);

    public InteropScenarioRun(InteropScenarioDefinition definition, ExternalServiceDefinition target)
    {
        _definition = definition;
        _target = target;
        _startedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Records a standards observation. Errors fail the scenario; warnings do not.</summary>
    public InteropScenarioRun Record(InteropFinding finding)
    {
        _findings.Add(finding);
        return this;
    }

    /// <summary>Records which IG version each side was operating under for a protocol.</summary>
    public InteropScenarioRun RecordCompatibility(string protocol, string? cho, string? external, string? note = null)
    {
        _compatibility[protocol] = new ProtocolCompatibility
        {
            Cho = cho,
            External = external,
            Mismatch = cho is not null && external is not null
                       && !string.Equals(cho, external, StringComparison.OrdinalIgnoreCase),
            Note = note,
        };
        return this;
    }

    /// <summary>True when any recorded finding is an Error.</summary>
    public bool HasBlockingFindings =>
        _findings.Any(f => f.Severity == nameof(FindingSeverity.Error));

    /// <summary>Seals the run into an evidence result.</summary>
    public InteropResult Complete(
        InteropStatus status,
        IReadOnlyList<InteropInteraction> interactions,
        string? statusReason = null)
    {
        var endedAt = DateTimeOffset.UtcNow;
        return new InteropResult
        {
            ScenarioId = _definition.Id,
            Title = _definition.Title,
            Protocol = _definition.Protocol,
            ChoRole = _definition.ChoRole,
            Target = _target.Name,
            TargetVersion = _target.EvidenceVersion,
            TargetImageReference = _target.Pin.Reference,
            TargetSourceCommit = _target.Pin.SourceCommit ?? _target.Pin.Commit,
            ChoCommit = InteropSettings.ChoCommit,
            StartedAtUtc = _startedAt.ToString("O"),
            EndedAtUtc = endedAt.ToString("O"),
            DurationMs = (long)(endedAt - _startedAt).TotalMilliseconds,
            Status = status.ToString(),
            StatusReason = statusReason,
            Interactions = interactions.ToList(),
            Findings = _findings.ToList(),
            Compatibility = new Dictionary<string, ProtocolCompatibility>(_compatibility),
            Environment = EnvironmentMetadata(),
        };
    }

    private static InteropEnvironmentMetadata EnvironmentMetadata() => new()
    {
        Environment = InteropSettings.EnvironmentLabel,
        Os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
        Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        FhirLibrary = typeof(Hl7.Fhir.Model.Resource).Assembly.GetName().Version?.ToString() ?? "unknown",
        DataClassification = "synthetic",
    };
}
