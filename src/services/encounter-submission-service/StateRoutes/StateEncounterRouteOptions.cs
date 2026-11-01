namespace EncounterSubmissionService.StateRoutes;

/// <summary>
/// How a state's encounter submission deadline is counted.
/// </summary>
public enum EncounterDeadlineRule
{
    /// <summary>N calendar days after the claim was adjudicated (Florida's 60-day window).</summary>
    DaysAfterAdjudication,

    /// <summary>N calendar days after the last day of the month the claim was adjudicated in.</summary>
    DaysAfterEndOfAdjudicationMonth
}

/// <summary>
/// Configuration for one state's encounter route, bound from
/// <c>EncounterRoutes:{STATE}</c> (for example <c>EncounterRoutes:TX</c>).
///
/// <para>The state-level values describe the receiver and are the same for every
/// plan. The identities a plan is issued by the state (submitter / trading partner
/// ID, the plan's own payer ID) live under <c>Tenants:{tenantId}</c>. A route is
/// admitted for a tenant only when the state is <see cref="Enabled"/> AND that
/// tenant has its identities configured, so turning a state on never starts
/// producing files for a plan the state has not enrolled.</para>
///
/// <para>Every receiver identifier here comes from the state's companion guide
/// and trading partner agreement. None is defaulted in code: a guessed receiver
/// ID would produce files that look valid and are routed nowhere.</para>
/// </summary>
public sealed class StateEncounterRouteOptions
{
    public bool Enabled { get; set; }

    /// <summary>ISA08 / GS03 / 1000B NM109 — the state receiver's EDI identifier.</summary>
    public string? ReceiverId { get; set; }

    /// <summary>ISA07 qualifier for <see cref="ReceiverId"/>. Defaults to ZZ (mutually defined).</summary>
    public string ReceiverIdQualifier { get; set; } = "ZZ";

    /// <summary>1000B NM103 — receiver name.</summary>
    public string? ReceiverName { get; set; }

    /// <summary>2010BB NM103 — the destination payer (the state Medicaid agency).</summary>
    public string? DestinationPayerName { get; set; }

    /// <summary>2010BB NM109 — the destination payer identifier.</summary>
    public string? DestinationPayerId { get; set; }

    /// <summary>2000B SBR01 for the state as destination payer. Companion guides differ; default S.</summary>
    public string DestinationPayerResponsibility { get; set; } = "S";

    /// <summary>2320 SBR01 for the plan's own adjudication. Default P.</summary>
    public string PlanPayerResponsibility { get; set; } = "P";

    /// <summary>2000B SBR09 claim filing indicator for the state program. Default MC (Medicaid).</summary>
    public string DestinationClaimFilingIndicator { get; set; } = "MC";

    /// <summary>2320 SBR09 claim filing indicator for the plan. Default HM (HMO).</summary>
    public string PlanClaimFilingIndicator { get; set; } = "HM";

    /// <summary>
    /// ISA15. Stays T (test) until the state certifies the plan; set P only after
    /// the state has accepted test files and issued production credentials.
    /// </summary>
    public string UsageIndicator { get; set; } = "T";

    public EncounterDeadlineRule? DeadlineRule { get; set; }
    public int? DeadlineDays { get; set; }

    /// <summary>Blob container (or bucket prefix) that staged files are written to.</summary>
    public string? StagingContainer { get; set; }

    /// <summary>Per-plan identities issued by the state, keyed by CHO tenant id.</summary>
    public Dictionary<string, StateEncounterTenantOptions> Tenants { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The identities a state issues to one plan for encounter submission.
/// </summary>
public sealed class StateEncounterTenantOptions
{
    /// <summary>ISA06 / GS02 / 1000A NM109 — the submitter (trading partner) ID the state issued.</summary>
    public string? SubmitterId { get; set; }

    public string SubmitterIdQualifier { get; set; } = "ZZ";

    /// <summary>1000A NM103 — submitter organization name.</summary>
    public string? SubmitterName { get; set; }

    public string? ContactName { get; set; }

    /// <summary>1000A PER04, digits only.</summary>
    public string? ContactPhone { get; set; }

    /// <summary>2330B NM103 — the plan's name as it adjudicated the claim.</summary>
    public string? PlanName { get; set; }

    /// <summary>2330B NM109 and 2430 SVD01 — the plan's payer identifier as the state knows it.</summary>
    public string? PlanPayerId { get; set; }

    /// <summary>
    /// Optional state-assigned plan code (Texas plan code, Medi-Cal plan code). Recorded on the
    /// file name and submission records; placed in the 837 only where a companion guide says so.
    /// </summary>
    public string? PlanCode { get; set; }

    /// <summary>Lists what is missing for the route to be usable. Empty when complete.</summary>
    public IReadOnlyList<string> MissingFields()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(SubmitterId)) missing.Add(nameof(SubmitterId));
        if (string.IsNullOrWhiteSpace(SubmitterName)) missing.Add(nameof(SubmitterName));
        if (string.IsNullOrWhiteSpace(PlanName)) missing.Add(nameof(PlanName));
        if (string.IsNullOrWhiteSpace(PlanPayerId)) missing.Add(nameof(PlanPayerId));
        return missing;
    }
}
