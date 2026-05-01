using System.Text.Json.Serialization;

namespace ClaimsService.Services.Resolution;

/// <summary>
/// Cross-service Coordination of Benefits lookup against coverage-service's
/// <c>GET /api/v1/coverage/member/{memberId}/cob</c> (capability 5.8).
/// Consumed by <see cref="Adjudication.Stages.CoordinationOfBenefitsStage"/>;
/// wrapped by <see cref="CachingCoverageClient"/> in production for a 5-minute
/// per-pod TTL.
///
/// <para>
/// The 5-minute TTL mirrors <see cref="CachingProviderMembershipClient"/>'s
/// shorter membership window (vs. credentialing's 1-hour): coverage records
/// CAN terminate without an explicit signal (open-enrollment loss, mid-year
/// termination), so a longer cache risks stale "no other coverage" results
/// for claims submitted right after a coverage change.
/// </para>
///
/// <para>
/// <b>404 semantics (Decision 14a — ratified plan-phase).</b> coverage-service
/// returns <c>404 Not Found</c> when a member has zero COB entries (whether
/// the member is missing OR has no other insurance). Implementations of this
/// interface MUST translate that 404 into an empty list so callers can rely on
/// <em>"empty list = CHO is the only coverage"</em> semantics. Genuine
/// transport failures surface as <c>null</c> (degraded; mode-driven outcome).
/// </para>
/// </summary>
public interface ICoverageClient
{
    /// <summary>
    /// Returns the COB entries for <paramref name="memberId"/> as of
    /// <paramref name="asOfDate"/>. An empty list means CHO is the only
    /// coverage (NOT a degradation signal). <c>null</c> means transport
    /// failure or unparseable response — the stage's degradation posture
    /// (Decision 7) pends in <c>PendForSecondary</c> and <c>Deny</c>
    /// modes ("unable to determine coverage state" is not structurally a
    /// denial), and passes with telemetry in <c>SoftValidation</c> mode.
    /// </summary>
    Task<IReadOnlyList<CobEntry>?> GetCobEntriesAsync(
        string tenantId,
        string memberId,
        DateTime asOfDate,
        bool forceRefresh = false,
        CancellationToken ct = default);
}

/// <summary>
/// Pipeline-local representation of one COB entry. Mirrors the wire shape
/// of coverage-service's <c>CobEntryResponse</c>
/// (<see cref="HttpCoverageClient"/> deserialises directly into this type).
///
/// <para>
/// <b>Phase 1 caveat — <see cref="PayerId"/> field semantics (Decision 1E).</b>
/// coverage-service currently populates <c>PayerId</c> from the policy
/// number (<c>Coverage.OtherInsurance.PolicyNumber</c>), not a true payer
/// identifier. Phase 1 carries the value through unchanged for telemetry
/// continuity; Phase 2 priorEob work fixes the upstream contract.
/// </para>
/// </summary>
public sealed class CobEntry
{
    [JsonPropertyName("payerName")]
    public string PayerName { get; set; } = string.Empty;

    [JsonPropertyName("payerId")]
    public string PayerId { get; set; } = string.Empty;

    /// <summary>
    /// X12 SBR01 sequence: <c>"P"</c> = Primary, <c>"S"</c> = Secondary,
    /// <c>"T"</c> = Tertiary. Stays a string at the wire boundary so
    /// upstream enum drift cannot break this client.
    /// </summary>
    [JsonPropertyName("coverageSequence")]
    public string CoverageSequence { get; set; } = "S";

    [JsonPropertyName("groupNumber")]
    public string? GroupNumber { get; set; }

    [JsonPropertyName("policyNumber")]
    public string? PolicyNumber { get; set; }

    [JsonPropertyName("coverageBeginDate")]
    public DateTime CoverageBeginDate { get; set; }

    [JsonPropertyName("coverageEndDate")]
    public DateTime? CoverageEndDate { get; set; }

    [JsonPropertyName("isMedicare")]
    public bool IsMedicare { get; set; }

    /// <summary>True when <see cref="CoverageSequence"/> is <c>"P"</c>
    /// (case-insensitive). Convenience accessor for stage logic.</summary>
    public bool IsPrimary =>
        string.Equals(CoverageSequence, "P", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <see cref="CoverageSequence"/> is <c>"S"</c>
    /// (case-insensitive). Convenience accessor for stage logic.</summary>
    public bool IsSecondary =>
        string.Equals(CoverageSequence, "S", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <see cref="CoverageSequence"/> is <c>"T"</c>
    /// (case-insensitive). Convenience accessor for stage logic.</summary>
    public bool IsTertiary =>
        string.Equals(CoverageSequence, "T", StringComparison.OrdinalIgnoreCase);
}
