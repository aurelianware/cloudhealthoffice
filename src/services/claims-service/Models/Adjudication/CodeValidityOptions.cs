namespace ClaimsService.Models.Adjudication;

/// <summary>
/// What <see cref="Services.Adjudication.Stages.CodeValidityStage"/> does with a
/// diagnosis or procedure code that was not valid on the claim's date of service.
/// </summary>
public enum CodeValidityEnforcementMode
{
    /// <summary>Do not check.</summary>
    Off,

    /// <summary>
    /// Default. Record the findings (stage notes, shadow findings) and let the
    /// claim through. The first step for a plan, and what a shadow run reports.
    /// </summary>
    Report,

    /// <summary>Pend the claim (pend code <c>CODEVALID</c>) for an examiner.</summary>
    PendForReview,

    /// <summary>
    /// Deny the claim: CARC 146 (diagnosis invalid for the dates of service) when
    /// a diagnosis is invalid, otherwise CARC 181 (procedure code invalid on the
    /// date of service).
    /// </summary>
    Deny,
}

/// <summary>Per-plan posture for the code-validity check.</summary>
public class CodeValidityPolicy
{
    public CodeValidityEnforcementMode Mode { get; set; } = CodeValidityEnforcementMode.Report;
}

/// <summary>
/// Service-wide posture (section <c>Adjudication:CodeValidity</c>) with
/// per-tenant overrides under <c>Tenants</c>.
/// </summary>
public class CodeValidityOptions : CodeValidityPolicy
{
    public const string SectionName = "Adjudication:CodeValidity";

    /// <summary>How long to wait for terminology-service before passing the claim unchecked.</summary>
    public int TimeoutMilliseconds { get; set; } = 2_000;

    public Dictionary<string, CodeValidityPolicy> Tenants { get; set; } = new(StringComparer.Ordinal);

    public CodeValidityPolicy For(string tenantId) =>
        !string.IsNullOrWhiteSpace(tenantId) && Tenants.TryGetValue(tenantId, out var policy) && policy is not null
            ? policy
            : this;
}
