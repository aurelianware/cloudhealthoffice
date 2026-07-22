using ClaimsService.Models;
using ClaimsService.Services.Resolution;

namespace ClaimsService.Services.Adjudication.Stages;

/// <summary>
/// Federal provider-exclusion (OIG/LEIE/SAM.gov) check, run against the
/// billing and rendering providers on the claim.
///
/// <para>
/// <b>Why this stage exists.</b> <c>HttpProviderIntegrityGate</c>
/// (benefit-plan-service, capability 5.10) has always existed and is well
/// tested, but was only ever wired into the standalone
/// <c>AdjudicationController.Adjudicate</c> HTTP endpoint -- not into this
/// orchestrator. Capability 5.5's six-stage pipeline (Scrubbing,
/// NetworkCredentialing, BenefitCalculation, NcciEdits,
/// CoordinationOfBenefits, AiExamination) never included a provider-integrity
/// stage in its original scope, so real claims processed through
/// <c>BenefitCalculationStage</c> -&gt; <c>calculate-benefits</c> were never
/// checked against federal exclusion lists at all. This stage closes that
/// gap using the exact same gate logic, reached via a new, side-effect-free
/// endpoint (<c>GET /api/v1/adjudication/provider-integrity/{npi}</c>) so
/// <c>calculate-benefits</c> itself stays exclusion-check-free -- it's also
/// called by portal/preview features that must not be blocked by a live
/// exclusion check on a hypothetical calculation.
/// </para>
///
/// <para>
/// <b>Resolution semantics.</b> A confirmed exclusion (<c>IsExcluded</c>)
/// is a <c>Deny</c> -- pipeline short-circuits to PersistenceStage.
/// Anything the gate could not confidently resolve either way
/// (<c>RequiresManualReview</c>, or the HTTP call to benefit-plan-service
/// itself failing) is a <c>Pend</c> with <c>PendCode="MEDREVIEW"</c> --
/// already a documented, recognized <see cref="PendDetails.PendCode"/>
/// value with an existing work-queue bucket (no new pend vocabulary).
/// The gate never fails open (see <c>HttpProviderIntegrityGate</c>'s own
/// contract), so this stage never silently passes a provider it could not
/// verify.
/// </para>
///
/// <para>
/// <b>Not tenant-configurable to fail open.</b> Unlike
/// <see cref="NetworkCredentialingStage"/>'s <c>FailOpen</c>/<c>SoftValidation</c>
/// enforcement modes, this stage has no equivalent -- a federal exclusion
/// check has no legitimate "advisory only" posture. It can be disabled
/// entirely via <c>EnabledStages</c> (matching every other stage's
/// contract), but while enabled it always enforces.
/// </para>
/// </summary>
public sealed class ProviderIntegrityStage : IClaimAdjudicationStage
{
    public const string StageName = "ProviderIntegrity";
    public const string MedicalReviewPendCode = "MEDREVIEW";

    private readonly IProviderIntegrityClient _client;
    private readonly ILogger<ProviderIntegrityStage> _logger;

    public ProviderIntegrityStage(
        IProviderIntegrityClient client,
        ILogger<ProviderIntegrityStage> logger)
    {
        _client = client;
        _logger = logger;
    }

    public string Name => StageName;
    public int Order => 150;
    public bool IsRequired => false;

    public async Task<ClaimAdjudicationStageResult> ExecuteAsync(
        ClaimAdjudicationContext context,
        CancellationToken ct)
    {
        var claim = context.Claim;

        var providersToCheck = new List<(string Role, string Npi)>();
        if (!string.IsNullOrWhiteSpace(claim.BillingProviderNPI))
        {
            providersToCheck.Add(("Billing provider", claim.BillingProviderNPI));
        }

        if (!string.IsNullOrWhiteSpace(claim.RenderingProviderNPI)
            && !string.Equals(claim.RenderingProviderNPI, claim.BillingProviderNPI, StringComparison.OrdinalIgnoreCase))
        {
            providersToCheck.Add(("Rendering provider", claim.RenderingProviderNPI));
        }

        if (providersToCheck.Count == 0)
        {
            // No provider NPI on the claim at all. ScrubbingStage
            // (Order=100, runs first) owns rejecting claims missing
            // required provider identifiers -- nothing to check here.
            return ClaimAdjudicationStageResult.Pass(StageName);
        }

        foreach (var (role, npi) in providersToCheck)
        {
            var result = await _client.CheckAsync(context.TenantId, npi, ct).ConfigureAwait(false);

            if (result is null)
            {
                // Transport failure reaching benefit-plan-service itself.
                // The gate's "never fail open" contract covers failures
                // reaching ITS upstreams; if benefit-plan-service is
                // unreachable at all, apply the same never-fail-open
                // policy here.
                _logger.LogWarning(
                    "Provider integrity check unreachable for claim {ClaimVersionId}, {Role} NPI {Npi}",
                    SanitizeForLog(context.ClaimVersionId), role, SanitizeForLog(npi));
                return PendForReview(context, role, "Provider integrity check could not be reached.");
            }

            if (result.IsExcluded)
            {
                context.AdjudicationResult.DenialReasonCode = result.DenialCode ?? "B7";
                context.AdjudicationResult.DenialReason = result.DenialReason
                    ?? $"{role} is excluded from federal healthcare programs.";
                return ClaimAdjudicationStageResult.Deny(StageName, context.AdjudicationResult.DenialReason);
            }

            if (!result.Passed || result.RequiresManualReview)
            {
                return PendForReview(
                    context,
                    role,
                    result.DenialReason ?? "Provider integrity could not be confidently verified.");
            }
        }

        return ClaimAdjudicationStageResult.Pass(StageName);
    }

    private static ClaimAdjudicationStageResult PendForReview(
        ClaimAdjudicationContext context,
        string role,
        string reason)
    {
        var formatted = $"{role}: {reason}";
        context.PendDetails = new PendDetails
        {
            PendCode = MedicalReviewPendCode,
            PendReason = TruncatePendReason(formatted),
            PendedAt = DateTime.UtcNow,
        };
        return ClaimAdjudicationStageResult.Pend(StageName, formatted);
    }

    private static string? TruncatePendReason(string? reason) =>
        reason is { Length: > 500 } ? reason[..500] : reason;

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}
