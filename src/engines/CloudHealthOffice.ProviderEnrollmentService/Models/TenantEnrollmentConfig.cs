namespace CloudHealthOffice.ProviderEnrollmentService.Models;

// ─────────────────────────────────────────────────────────────────
// Tenant enrollment configuration
//
// Stored in Cosmos / Mongo as one document per tenant.
// The resolution hierarchy is:
//   LOB override (non-null fields) → tenant default
//
// Callers always consume a ResolvedEnrollmentConfig — no nulls,
// no conditional logic outside this file.
// ─────────────────────────────────────────────────────────────────

public enum EnrollmentGateMode
{
    /// <summary>
    /// Gate is never evaluated.
    /// Use for LOBs with no applicable state enrollment system:
    /// Marketplace/Exchange, Commercial, Medicare (until PECOS adapter ships).
    /// </summary>
    Disabled,

    /// <summary>
    /// Gate runs, failures are logged but never produce a denial.
    /// Use during initial rollout to measure impact before enforcing.
    /// </summary>
    Warn,

    /// <summary>
    /// Gate runs and denies on failure.
    /// Production mode for all Medicaid / CHIP / STAR / LTSS LOBs.
    /// </summary>
    Enforce
}

/// <summary>
/// Per-LOB override. Only non-null fields replace the tenant default.
/// A null field means "inherit the tenant-level value."
/// </summary>
public record LobEnrollmentOverride
{
    /// <summary>The line of business this override applies to.</summary>
    public required LineOfBusiness Lob { get; init; }

    /// <summary>null = inherit tenant default GateMode.</summary>
    public EnrollmentGateMode? GateMode { get; init; }

    /// <summary>
    /// null = inherit tenant default EnabledStateCodes.
    /// Useful when a LOB is only offered in a subset of the tenant's states
    /// — e.g. LTSS only in TX even though the plan also runs Medicaid in CA + FL.
    /// </summary>
    public IReadOnlyList<string>? EnabledStateCodes { get; init; }

    /// <summary>null = inherit tenant default RevalidationWarningDays.</summary>
    public int? RevalidationWarningDays { get; init; }

    /// <summary>null = inherit tenant default GoldCardBypassesGate.</summary>
    public bool? GoldCardBypassesGate { get; init; }
}

/// <summary>
/// Tenant-level enrollment configuration document.
/// One per tenant, stored in the enrollment-tenant-config container.
///
/// Partition key: /tenantId
/// Document ID:   tenantId  (simple — one document per tenant)
/// </summary>
public record TenantEnrollmentConfig
{
    public required string TenantId { get; init; }

    // ── Tenant-level defaults ─────────────────────────────────────
    // Applied to any LOB that does not have a specific override entry.

    /// <summary>
    /// State sources active for this tenant.
    /// Empty list = all registered platform sources are eligible.
    /// </summary>
    public IReadOnlyList<string> EnabledStateCodes { get; init; } = [];

    /// <summary>
    /// CAQH ProView organization ID assigned to this plan.
    /// Always tenant-level — there is no per-LOB CAQH org ID concept.
    /// </summary>
    public string? CaqhOrganizationId { get; init; }

    /// <summary>Default gate enforcement mode when no LOB override exists.</summary>
    public EnrollmentGateMode DefaultGateMode { get; init; } = EnrollmentGateMode.Enforce;

    /// <summary>
    /// Default revalidation alert window in days.
    /// Providers whose revalidation falls within this window trigger alerts.
    /// </summary>
    public int DefaultRevalidationWarningDays { get; init; } = 90;

    /// <summary>
    /// Whether a gold-card provider bypasses the enrollment gate by default.
    /// Gold-card logic is evaluated upstream in PasAutoAdjudicator before
    /// the gate is called — this flag controls whether the gate result is
    /// honoured or discarded when gold-card status is confirmed.
    /// </summary>
    public bool DefaultGoldCardBypassesGate { get; init; } = false;

    /// <summary>
    /// MCO participant IDs for this plan within each state enrollment system.
    /// Used by the panel reconciliation service to pull the plan's enrolled panel.
    /// Example: ["TXMCO01-MCO-TX-001"] for the tenant's PEMS MCO identifier.
    /// </summary>
    public IReadOnlyList<string> McoIds { get; init; } = [];

    // ── LOB overrides ─────────────────────────────────────────────

    /// <summary>
    /// Per-LOB overrides. Fields left null inherit the tenant default above.
    /// Typical use cases:
    ///   Marketplace / Commercial → GateMode: Disabled (no state enrollment system)
    ///   Medicare                 → GateMode: Disabled (PECOS adapter not yet built)
    ///   STAR / STARPlus          → tighter RevalidationWarningDays (TX compliance)
    ///   LTSS                     → EnabledStateCodes restricted to ["TX"]
    /// </summary>
    public IReadOnlyList<LobEnrollmentOverride> LobOverrides { get; init; } = [];

    // ── Resolution ────────────────────────────────────────────────

    /// <summary>
    /// Resolve the effective configuration for a specific LOB.
    ///
    /// Resolution order:
    ///   1. Find a LobEnrollmentOverride whose Lob matches (exact flag match).
    ///   2. For each field: use the override value if non-null, else the tenant default.
    ///   3. Return a flat ResolvedEnrollmentConfig with no nulls.
    ///
    /// Callers never touch LobOverrides directly — always call ResolveFor().
    /// </summary>
    public ResolvedEnrollmentConfig ResolveFor(LineOfBusiness lob)
    {
        // Exact LOB match — no flag decomposition. A request carrying
        // LineOfBusiness.STAR resolves the STAR override, not Medicaid.
        var ovr = LobOverrides.FirstOrDefault(o => o.Lob == lob);

        return new ResolvedEnrollmentConfig
        {
            TenantId                = TenantId,
            Lob                     = lob,
            GateMode                = ovr?.GateMode               ?? DefaultGateMode,
            EnabledStateCodes       = ovr?.EnabledStateCodes       ?? EnabledStateCodes,
            RevalidationWarningDays = ovr?.RevalidationWarningDays ?? DefaultRevalidationWarningDays,
            GoldCardBypassesGate    = ovr?.GoldCardBypassesGate    ?? DefaultGoldCardBypassesGate,
            CaqhOrganizationId      = CaqhOrganizationId,
            McoIds                  = McoIds
        };
    }
}

/// <summary>
/// Fully resolved enrollment configuration for a specific (tenant, LOB) pair.
/// All fields are non-null. This is the only type consumed by the gate and aggregator.
/// </summary>
public record ResolvedEnrollmentConfig
{
    public required string TenantId                         { get; init; }
    public required LineOfBusiness Lob                      { get; init; }
    public required EnrollmentGateMode GateMode             { get; init; }
    public required IReadOnlyList<string> EnabledStateCodes { get; init; }
    public required int RevalidationWarningDays             { get; init; }
    public required bool GoldCardBypassesGate               { get; init; }
    public string? CaqhOrganizationId                       { get; init; }
    public IReadOnlyList<string> McoIds                     { get; init; } = [];
}
