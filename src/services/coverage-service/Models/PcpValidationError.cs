using System.Text.Json.Serialization;

namespace CoverageService.Models;

/// <summary>
/// Structured validation failure emitted by <c>PcpAssignmentService</c>. The
/// <see cref="Code"/> values are an API contract — the portal localizes off them
/// and picks the remediation path (search again, escalate to ops, etc.). Do not
/// rename without coordinating a portal release.
/// </summary>
public sealed class PcpValidationError
{
    public PcpValidationError(string code, string field, string message, PcpValidationSeverity severity = PcpValidationSeverity.Error)
    {
        Code = code;
        Field = field;
        Message = message;
        Severity = severity;
    }

    /// <summary>Machine-readable error code (see <see cref="PcpValidationCodes"/>).</summary>
    public string Code { get; }

    /// <summary>Field the error is attached to (providerNpi, memberId, panel, etc.).</summary>
    public string Field { get; }

    /// <summary>Human-readable explanation — displayed as fallback if portal has no localization.</summary>
    public string Message { get; }

    /// <summary>Severity. Today all validation errors are <see cref="PcpValidationSeverity.Error"/>; Warning reserved for future soft-fails.</summary>
    public PcpValidationSeverity Severity { get; }
}

/// <summary>
/// Severity of a PCP validation failure. Serialized as a string on the wire —
/// downstream clients (member-service + portal) expect "Error"/"Warning".
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PcpValidationSeverity
{
    Warning = 0,
    Error = 1
}

/// <summary>
/// Stable string codes for PCP validation failures. Ordered here in the same
/// fail-fast order <c>PcpAssignmentService</c> evaluates them.
/// </summary>
public static class PcpValidationCodes
{
    public const string ProviderNotFound = "PROVIDER_NOT_FOUND";
    public const string ProviderInactive = "PROVIDER_INACTIVE";
    public const string ProviderNotCredentialed = "PROVIDER_NOT_CREDENTIALED";
    public const string NoNetworkParticipation = "NO_NETWORK_PARTICIPATION";
    public const string NotAcceptingPatients = "NOT_ACCEPTING_PATIENTS";
    public const string LobNotAccepted = "LOB_NOT_ACCEPTED";
    public const string AgeOutOfRange = "AGE_OUT_OF_RANGE";
    public const string PanelFull = "PANEL_FULL";

    // Preflight (not part of the ordered ladder):
    public const string NoActiveCoverage = "NO_ACTIVE_COVERAGE";
    public const string InvalidNpi = "INVALID_NPI";
}
