using System.Text.Json.Serialization;
using CloudHealthOffice.ProviderEnrollmentService.Models;

namespace CloudHealthOffice.ProviderEnrollmentService.Cache;

/// <summary>
/// Cosmos DB document for TenantEnrollmentConfig.
///
/// Container: enrollment-tenant-config
/// Partition key: /tenantId
/// Document ID:   tenantId  (one document per tenant — point reads are O(1))
///
/// No TTL — tenant config documents are permanent until explicitly deleted.
/// </summary>
public sealed class TenantEnrollmentConfigDocument
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;               // = tenantId

    [JsonPropertyName("tenantId")]
    public string TenantId { get; init; } = string.Empty;         // partition key

    [JsonPropertyName("enabledStateCodes")]
    public IReadOnlyList<string> EnabledStateCodes { get; init; } = [];

    [JsonPropertyName("caqhOrganizationId")]
    public string? CaqhOrganizationId { get; init; }

    [JsonPropertyName("defaultGateMode")]
    public string DefaultGateMode { get; init; } = nameof(EnrollmentGateMode.Enforce);

    [JsonPropertyName("defaultRevalidationWarningDays")]
    public int DefaultRevalidationWarningDays { get; init; } = 90;

    [JsonPropertyName("defaultGoldCardBypassesGate")]
    public bool DefaultGoldCardBypassesGate { get; init; }

    [JsonPropertyName("mcoIds")]
    public IReadOnlyList<string> McoIds { get; init; } = [];

    [JsonPropertyName("lobOverrides")]
    public IReadOnlyList<LobOverrideDocument> LobOverrides { get; init; } = [];

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;

    // ── Mapping ───────────────────────────────────────────────────

    public static TenantEnrollmentConfigDocument FromModel(TenantEnrollmentConfig m) => new()
    {
        Id                              = m.TenantId,
        TenantId                        = m.TenantId,
        EnabledStateCodes               = m.EnabledStateCodes,
        CaqhOrganizationId              = m.CaqhOrganizationId,
        DefaultGateMode                 = m.DefaultGateMode.ToString(),
        DefaultRevalidationWarningDays  = m.DefaultRevalidationWarningDays,
        DefaultGoldCardBypassesGate     = m.DefaultGoldCardBypassesGate,
        McoIds                          = m.McoIds,
        LobOverrides                    = m.LobOverrides.Select(LobOverrideDocument.FromModel).ToList(),
        UpdatedAt                       = DateTime.UtcNow
    };

    public TenantEnrollmentConfig ToModel() => new()
    {
        TenantId                        = TenantId,
        EnabledStateCodes               = EnabledStateCodes,
        CaqhOrganizationId              = CaqhOrganizationId,
        DefaultGateMode                 = Enum.Parse<EnrollmentGateMode>(DefaultGateMode),
        DefaultRevalidationWarningDays  = DefaultRevalidationWarningDays,
        DefaultGoldCardBypassesGate     = DefaultGoldCardBypassesGate,
        McoIds                          = McoIds,
        LobOverrides                    = LobOverrides.Select(o => o.ToModel()).ToList()
    };
}

public sealed class LobOverrideDocument
{
    [JsonPropertyName("lob")]
    public string Lob { get; init; } = string.Empty;

    [JsonPropertyName("gateMode")]
    public string? GateMode { get; init; }

    [JsonPropertyName("enabledStateCodes")]
    public IReadOnlyList<string>? EnabledStateCodes { get; init; }

    [JsonPropertyName("revalidationWarningDays")]
    public int? RevalidationWarningDays { get; init; }

    [JsonPropertyName("goldCardBypassesGate")]
    public bool? GoldCardBypassesGate { get; init; }

    public static LobOverrideDocument FromModel(LobEnrollmentOverride m) => new()
    {
        Lob                     = m.Lob.ToString(),
        GateMode                = m.GateMode?.ToString(),
        EnabledStateCodes       = m.EnabledStateCodes,
        RevalidationWarningDays = m.RevalidationWarningDays,
        GoldCardBypassesGate    = m.GoldCardBypassesGate
    };

    public LobEnrollmentOverride ToModel() => new()
    {
        Lob                     = Enum.Parse<LineOfBusiness>(Lob),
        GateMode                = GateMode is null ? null : Enum.Parse<EnrollmentGateMode>(GateMode),
        EnabledStateCodes       = EnabledStateCodes,
        RevalidationWarningDays = RevalidationWarningDays,
        GoldCardBypassesGate    = GoldCardBypassesGate
    };
}
