using ClaimsService.Models.Adjudication;
using ClaimsService.Services.Resolution;
using Microsoft.Extensions.Options;

namespace ClaimsService.Services.Adjudication.Stages;

/// <summary>
/// Capability 5.6 — replaces <see cref="NetworkCredentialingStubStage"/>.
/// Calls provider-service for both:
/// <list type="bullet">
///   <item><description>Network membership of the billing provider against
///     the resolved benefit plan's tier list (first matching tier wins).</description></item>
///   <item><description>Credentialing status of the billing provider as of
///     the claim's earliest service date.</description></item>
/// </list>
///
/// <para>
/// The stage produces structured <see cref="EnforcementOutcome"/> entries
/// on the context for each check. Resolution semantics for the stage's
/// final <see cref="ClaimAdjudicationStageResult"/>:
/// </para>
/// <list type="bullet">
///   <item><description>Both checks <c>Allow</c> → <c>Pass</c>; subsequent
///     stages run.</description></item>
///   <item><description>Either check <c>Deny</c> → <c>Deny</c>; pipeline
///     short-circuits to PersistenceStage.</description></item>
///   <item><description>Either check <c>Pend</c> with no <c>Deny</c> →
///     <c>Pend</c>; pipeline continues so subsequent stages can decorate.</description></item>
///   <item><description>All <c>Observe</c> outcomes (soft-validation) →
///     <c>Pass</c>; observation captured on the audit trail without
///     policy effect.</description></item>
/// </list>
///
/// <para>
/// <b>Time anchor (Decision 3):</b> the credentialing check uses the
/// claim's earliest service date (header <c>ServiceDateFrom</c> vs the
/// minimum line-level <c>ServiceDateFrom</c>) — the most-restrictive
/// interpretation. A provider credentialed AFTER the earliest service
/// date doesn't auto-pay claims for that earlier date.
/// </para>
///
/// <para>
/// <b>Network resolution (Decision 10):</b> walk the resolved plan's
/// tier list ordered by <see cref="ResolvedNetworkTier.TierLevel"/>
/// ascending; the first tier whose roster includes the billing NPI
/// wins. The matched tier is recorded on
/// <see cref="ClaimAdjudicationContext.MatchedNetworkTier"/> for
/// downstream consumption (BenefitCalculationStage cost-share tiering
/// in a follow-up; not changed by 5.6).
/// </para>
/// </summary>
public sealed class NetworkCredentialingStage : IClaimAdjudicationStage
{
    public const string StageName = "NetworkCredentialing";

    private readonly IProviderMembershipClient _membershipClient;
    private readonly ICredentialingStatusClient _credentialingClient;
    private readonly TenantEnforcementPolicyOptions _options;
    private readonly ILogger<NetworkCredentialingStage> _logger;

    public NetworkCredentialingStage(
        IProviderMembershipClient membershipClient,
        ICredentialingStatusClient credentialingClient,
        IOptions<TenantEnforcementPolicyOptions> options,
        ILogger<NetworkCredentialingStage> logger)
    {
        _membershipClient = membershipClient;
        _credentialingClient = credentialingClient;
        _options = options.Value;
        _logger = logger;
    }

    public string Name => StageName;
    public int Order => 200;
    public bool IsRequired => false;

    public async Task<ClaimAdjudicationStageResult> ExecuteAsync(
        ClaimAdjudicationContext context,
        CancellationToken ct)
    {
        var claim = context.Claim;
        var billingNpi = claim.BillingProviderNPI;

        if (string.IsNullOrWhiteSpace(billingNpi))
        {
            return ClaimAdjudicationStageResult.Reject(
                StageName,
                "Claim is missing BillingProviderNPI; network/credentialing checks cannot run.");
        }

        var serviceDate = ResolveEarliestServiceDate(claim);

        var outcomes = new List<EnforcementOutcome>();

        // Membership: walk plan tiers in priority order. First tier whose
        // upstream returns IsActiveMember=true wins.
        var membershipOutcome = await ResolveMembershipAsync(
            context,
            billingNpi,
            serviceDate,
            providerRole: "Billing provider",
            captureBillingContext: true,
            ct).ConfigureAwait(false);
        outcomes.Add(membershipOutcome);
        context.EnforcementOutcomes.Add(membershipOutcome);

        // Credentialing: only call when we have a providerId from the
        // matched tier's membership response. If no tier matched we skip
        // the credentialing check entirely — out-of-network providers'
        // credentialing status is irrelevant to the enforcement
        // decision (see stage doc).
        EnforcementOutcome? credentialingOutcome = null;
        var matchedProviderId = context.BillingProviderNetworkMembership?.ProviderId;
        if (!string.IsNullOrWhiteSpace(matchedProviderId)
            && membershipOutcome.Decision == EnforcementDecision.Allow)
        {
            credentialingOutcome = await ResolveCredentialingAsync(
                context,
                matchedProviderId!,
                serviceDate,
                providerRole: "Billing provider",
                captureBillingContext: true,
                ct).ConfigureAwait(false);
            outcomes.Add(credentialingOutcome);
            context.EnforcementOutcomes.Add(credentialingOutcome);
        }

        var renderingNpi = claim.RenderingProviderNPI;
        if (!string.IsNullOrWhiteSpace(renderingNpi)
            && !string.Equals(renderingNpi, billingNpi, StringComparison.OrdinalIgnoreCase))
        {
            var renderingMembershipOutcome = await ResolveMembershipAsync(
                context,
                renderingNpi,
                serviceDate,
                providerRole: "Rendering provider",
                captureBillingContext: false,
                ct).ConfigureAwait(false);
            outcomes.Add(renderingMembershipOutcome);
            context.EnforcementOutcomes.Add(renderingMembershipOutcome);

            if (!string.IsNullOrWhiteSpace(context.RenderingProviderNetworkMembership?.ProviderId)
                && renderingMembershipOutcome.Decision == EnforcementDecision.Allow)
            {
                var renderingCredentialingOutcome = await ResolveCredentialingAsync(
                    context,
                    context.RenderingProviderNetworkMembership.ProviderId,
                    serviceDate,
                    providerRole: "Rendering provider",
                    captureBillingContext: false,
                    ct).ConfigureAwait(false);
                outcomes.Add(renderingCredentialingOutcome);
                context.EnforcementOutcomes.Add(renderingCredentialingOutcome);
            }
        }

        return CombineOutcomes(outcomes);
    }

    private async Task<EnforcementOutcome> ResolveMembershipAsync(
        ClaimAdjudicationContext context,
        string billingNpi,
        DateTime serviceDate,
        string providerRole,
        bool captureBillingContext,
        CancellationToken ct)
    {
        var tiers = context.ResolvedPlan?.NetworkTiers
            ?? Array.Empty<ResolvedNetworkTier>();

        // Filter out tiers without a NetworkId — those are legacy-shape
        // rows still in the BP 5.5 → hard-validation rollout window.
        var resolvableTiers = tiers
            .Where(t => !string.IsNullOrWhiteSpace(t.NetworkId))
            .OrderBy(t => t.TierLevel)
            .ToList();

        if (resolvableTiers.Count == 0)
        {
            // No resolvable in-network tiers → out-of-network by default.
            // Apply network-mode policy.
            return ApplyNetworkMode(
                context,
                EnforcementDecision.Deny,
                reason: $"{providerRole}: no in-network tier configured for the resolved plan.",
                serviceDate,
                networkId: null,
                tier: null);
        }

        var degraded = false;
        foreach (var tier in resolvableTiers)
        {
            var membership = await _membershipClient.GetMembershipAsync(
                context.TenantId, tier.NetworkId!, billingNpi, serviceDate, forceRefresh: false, ct)
                .ConfigureAwait(false);

            if (membership is null || !membership.IsActiveMember)
            {
                var refreshed = await _membershipClient.GetMembershipAsync(
                    context.TenantId, tier.NetworkId!, billingNpi, serviceDate, forceRefresh: true, ct)
                    .ConfigureAwait(false);
                membership = refreshed ?? membership;
            }

            if (membership is null)
            {
                // Lookup degraded (transport / 5xx). Don't short-circuit
                // the tier walk on a single failure — try the next tier.
                // If every tier's lookup degraded we apply the FailMode
                // posture below.
                degraded = true;
                continue;
            }

            if (membership.IsActiveMember)
            {
                if (captureBillingContext)
                {
                    context.BillingProviderNetworkMembership = membership;
                    context.MatchedNetworkTier = tier;
                }
                else
                {
                    context.RenderingProviderNetworkMembership = membership;
                }

                return new EnforcementOutcome(
                    Check: EnforcementCheck.Membership,
                    Decision: EnforcementDecision.Allow,
                    Mode: _options.NetworkMode.ToString(),
                    Reason: null,
                    AsOfDate: serviceDate,
                    NetworkId: tier.NetworkId,
                    TierName: tier.TierName,
                    TierLevel: tier.TierLevel);
            }
        }

        if (degraded)
        {
            return ApplyNetworkMode(
                context,
                EnforcementDecision.Deny,
                reason: $"{providerRole}: membership-verification-unavailable",
                serviceDate,
                networkId: null,
                tier: null);
        }

        // Every tier returned a non-null, non-active result — provider
        // is out-of-network for this plan.
        return ApplyNetworkMode(
            context,
            EnforcementDecision.Deny,
            reason: $"{providerRole} is not an active member of any plan tier on the service date.",
            serviceDate,
            networkId: null,
            tier: null);
    }

    private async Task<EnforcementOutcome> ResolveCredentialingAsync(
        ClaimAdjudicationContext context,
        string providerId,
        DateTime serviceDate,
        string providerRole,
        bool captureBillingContext,
        CancellationToken ct)
    {
        var snapshot = await _credentialingClient.GetStatusAsOfAsync(
            context.TenantId, providerId, serviceDate, forceRefresh: false, ct)
            .ConfigureAwait(false);

        if (snapshot is null || !snapshot.IsApprovedAtAsOf)
        {
            var refreshed = await _credentialingClient.GetStatusAsOfAsync(
                context.TenantId, providerId, serviceDate, forceRefresh: true, ct)
                .ConfigureAwait(false);
            snapshot = refreshed ?? snapshot;
        }

        if (snapshot is null)
        {
            return ApplyCredentialingMode(
                EnforcementDecision.Deny,
                reason: $"{providerRole}: credentialing-status-unavailable",
                serviceDate);
        }

        if (captureBillingContext)
        {
            context.BillingProviderCredentialingStatus = snapshot;
        }
        else
        {
            context.RenderingProviderCredentialingStatus = snapshot;
        }

        if (snapshot.IsApprovedAtAsOf)
        {
            return new EnforcementOutcome(
                Check: EnforcementCheck.Credentialing,
                Decision: EnforcementDecision.Allow,
                Mode: _options.CredentialingMode.ToString(),
                Reason: null,
                AsOfDate: serviceDate);
        }

        // Pending / Denied / Expired / Suspended / Unknown → not approved
        // for the service date. Drive policy via mode.
        var reason = $"{providerRole} credentialing status is '{snapshot.Status}' on the service date.";
        return ApplyCredentialingMode(EnforcementDecision.Deny, reason, serviceDate);
    }

    private EnforcementOutcome ApplyNetworkMode(
        ClaimAdjudicationContext context,
        EnforcementDecision intended,
        string reason,
        DateTime serviceDate,
        string? networkId,
        ResolvedNetworkTier? tier)
    {
        var decision = _options.NetworkMode switch
        {
            NetworkEnforcementMode.FailClosed => intended,
            NetworkEnforcementMode.FailOpen => EnforcementDecision.Pend,
            NetworkEnforcementMode.SoftValidation => EnforcementDecision.Observe,
            _ => intended,
        };

        if (decision == EnforcementDecision.Observe)
        {
            _logger.LogInformation(
                "NetworkCredentialingStage soft-validation observed for claim {ClaimVersionId}: would-{Intended} reason={Reason}",
                SanitizeForLog(context.ClaimVersionId), intended, SanitizeForLog(reason));
        }

        return new EnforcementOutcome(
            Check: EnforcementCheck.Membership,
            Decision: decision,
            Mode: _options.NetworkMode.ToString(),
            Reason: reason,
            AsOfDate: serviceDate,
            NetworkId: networkId,
            TierName: tier?.TierName,
            TierLevel: tier?.TierLevel);
    }

    private EnforcementOutcome ApplyCredentialingMode(
        EnforcementDecision intended,
        string reason,
        DateTime serviceDate)
    {
        var decision = _options.CredentialingMode switch
        {
            CredentialingEnforcementMode.FailClosed => intended,
            CredentialingEnforcementMode.FailOpen => EnforcementDecision.Pend,
            CredentialingEnforcementMode.SoftValidation => EnforcementDecision.Observe,
            _ => intended,
        };

        return new EnforcementOutcome(
            Check: EnforcementCheck.Credentialing,
            Decision: decision,
            Mode: _options.CredentialingMode.ToString(),
            Reason: reason,
            AsOfDate: serviceDate);
    }

    /// <summary>
    /// Resolves the earliest service date across the claim header and
    /// every line. Earliest wins (Decision 3 — most-restrictive
    /// interpretation; protects against credentialing gaps mid-claim
    /// when lines span service dates).
    /// </summary>
    internal static DateTime ResolveEarliestServiceDate(ClaimsService.Models.AdapterClaim claim)
    {
        var earliest = claim.ServiceDateFrom;
        foreach (var line in claim.ClaimLines)
        {
            if (line.ServiceDateFrom != default && line.ServiceDateFrom < earliest)
            {
                earliest = line.ServiceDateFrom;
            }
        }
        return earliest == default ? DateTime.UtcNow : earliest;
    }

    private static ClaimAdjudicationStageResult CombineOutcomes(
        IReadOnlyList<EnforcementOutcome> outcomes)
    {
        // Deny dominates Pend; Pend dominates Allow/Observe.
        var deny = outcomes.FirstOrDefault(o => o.Decision == EnforcementDecision.Deny);
        if (deny is not null)
        {
            return ClaimAdjudicationStageResult.Deny(StageName, FormatReason(deny));
        }

        var pend = outcomes.FirstOrDefault(o => o.Decision == EnforcementDecision.Pend);
        if (pend is not null)
        {
            return ClaimAdjudicationStageResult.Pend(StageName, FormatReason(pend));
        }

        // All Allow / Observe.
        return ClaimAdjudicationStageResult.Pass(StageName);
    }

    private static string FormatReason(EnforcementOutcome outcome)
        => $"{outcome.Check}: {outcome.Reason ?? outcome.Decision.ToString()} (mode={outcome.Mode})";

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}
