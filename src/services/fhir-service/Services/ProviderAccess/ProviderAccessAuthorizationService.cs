using CloudHealthOffice.Consent.Contracts;
using FhirService.Services.Consent;
using CloudHealthOffice.Infrastructure.Logging;

namespace FhirService.Services.ProviderAccess;

/// <summary>
/// Why a Provider Access request was refused. Kept internal to audit: the
/// external response is deliberately uniform, because telling a caller
/// "not attributed" rather than "no such member" lets them enumerate members.
/// </summary>
public enum ProviderAccessDenialReason
{
    None = 0,
    /// <summary>No member could be resolved from the request.</summary>
    NoMemberContext = 1,
    /// <summary>Tenant context missing — never inferred from the caller.</summary>
    NoTenantContext = 2,
    /// <summary>Caller identity missing from the token.</summary>
    NoCallerIdentity = 3,
    /// <summary>The member is not on this provider's panel.</summary>
    NotAttributed = 4,
    /// <summary>
    /// The member has opted out of Provider Access (or withdrew an earlier
    /// opt-in), or the consent registry could not be read to show they have not.
    /// </summary>
    ConsentDenied = 5,
}

/// <summary>
/// The composed Provider Access decision. Carries PHI-free identifiers only:
/// ids, categories, and an instant — no demographics, no clinical content, no
/// consent narrative.
/// </summary>
public sealed record ProviderAccessDecision
{
    public required bool Allowed { get; init; }
    public required ProviderAccessDenialReason Reason { get; init; }
    public required string TenantId { get; init; }
    public required string MemberId { get; init; }
    public string? ProviderId { get; init; }

    /// <summary>True once the panel check passed. False on any earlier refusal.</summary>
    public bool Attributed { get; init; }

    /// <summary>
    /// The consent that decided this: the opt-out that refused it, or an explicit
    /// permit on record. Null when the member simply never opted out.
    /// </summary>
    public string? AuthorizingConsentId { get; init; }

    /// <summary>The consent policy's own reason code, when consent was reached.</summary>
    public string? ConsentDecisionReason { get; init; }

    public required DateTime EvaluatedAtUtc { get; init; }

    public static ProviderAccessDecision Deny(
        ProviderAccessDenialReason reason,
        string tenantId,
        string memberId,
        string? providerId,
        DateTime evaluatedAtUtc,
        bool attributed = false,
        ConsentDecision? consent = null) => new()
        {
            Allowed = false,
            Reason = reason,
            TenantId = tenantId,
            MemberId = memberId,
            ProviderId = providerId,
            Attributed = attributed,
            AuthorizingConsentId = consent?.ConsentId,
            ConsentDecisionReason = consent?.Reason.ToString(),
            EvaluatedAtUtc = evaluatedAtUtc,
        };
}

/// <summary>
/// What the request layer knows, handed to the authorization service. The
/// service does not read <c>HttpContext</c>: it is given already-established
/// facts so the composition is testable without a web host, and so token
/// validation stays where it belongs (authentication middleware).
/// </summary>
public sealed record ProviderAccessRequest
{
    /// <summary>From the authenticated context — never a body or query value.</summary>
    public required string TenantId { get; init; }

    /// <summary>The member whose data is being read.</summary>
    public required string MemberId { get; init; }

    /// <summary>The calling provider's identity, from the token subject.</summary>
    public string? ProviderId { get; init; }
}

/// <summary>
/// Composes the Provider Access authorization decision.
///
/// Provider Access requires ALL of: an authenticated caller, an adequate SMART
/// scope, provider/member attribution, no Provider Access opt-out from the
/// member (CMS-0057-F shares with attributed providers unless the patient opts
/// out, 42 CFR 422.121(b)(4) and parallels), and tenant/member isolation. This service owns the last three; authentication
/// and SMART scope are established upstream (authentication middleware and
/// <c>SmartScopeEnforcementMiddleware</c>) and are NOT re-implemented here — the
/// abstraction coordinates the controls, it does not duplicate the security
/// layers beneath it.
///
/// Every control is independent and mandatory. Attribution does not imply
/// consent; consent does not imply attribution; a SMART scope implies neither.
/// The decision fails closed if any of them refuses.
/// </summary>
public interface IProviderAccessAuthorizationService
{
    Task<ProviderAccessDecision> AuthorizeAsync(
        ProviderAccessRequest request, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ProviderAccessAuthorizationService : IProviderAccessAuthorizationService
{
    /// <summary>
    /// The purpose Provider Access requires. Not configurable, and compared as
    /// data inside <see cref="ConsentAuthorizationPolicy"/> — never inferred
    /// from a route name or a controller.
    /// </summary>
    public const ConsentPurposeOfUse RequiredPurpose = ConsentPurposeOfUse.ProviderAccess;

    private readonly IProviderAttributionSource _attribution;
    private readonly IConsentEvaluator _consent;
    private readonly ILogger<ProviderAccessAuthorizationService> _logger;

    public ProviderAccessAuthorizationService(
        IProviderAttributionSource attribution,
        IConsentEvaluator consent,
        ILogger<ProviderAccessAuthorizationService> logger)
    {
        _attribution = attribution;
        _consent = consent;
        _logger = logger;
    }

    public async Task<ProviderAccessDecision> AuthorizeAsync(
        ProviderAccessRequest request, CancellationToken ct = default)
    {
        // The evaluation instant is the authorization attempt, chosen here.
        // Never a timestamp from the request: a caller that picks the instant
        // picks which consent state it is judged against.
        var now = DateTime.UtcNow;

        if (string.IsNullOrWhiteSpace(request.TenantId))
            return Denied(ProviderAccessDenialReason.NoTenantContext, request, now);

        if (string.IsNullOrWhiteSpace(request.MemberId))
            return Denied(ProviderAccessDenialReason.NoMemberContext, request, now);

        if (string.IsNullOrWhiteSpace(request.ProviderId))
            return Denied(ProviderAccessDenialReason.NoCallerIdentity, request, now);

        // ── Attribution ───────────────────────────────────────────────────────
        // Asked first because it is the cheaper control and because a provider
        // with no relationship to the member has no business having the member's
        // consent state considered at all.
        bool attributed;
        try
        {
            attributed = await _attribution.IsAttributedAsync(
                request.TenantId, request.ProviderId, request.MemberId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // An unreadable attribution source is not a relationship.
            _logger.LogWarning(
                "Provider Access attribution lookup failed for tenant={Tenant}; denying ({Fault}).",
                LogSanitizer.Sanitize(request.TenantId), ex.GetType().Name);
            return Denied(ProviderAccessDenialReason.NotAttributed, request, now);
        }

        if (!attributed)
            return Denied(ProviderAccessDenialReason.NotAttributed, request, now);

        // ── Opt-out ───────────────────────────────────────────────────────────
        // The same registry Payer-to-Payer uses, asked whether the member opted
        // out of this purpose. Sharing is the default; an in-force opt-out, a
        // withdrawn opt-in, or a registry that cannot be read refuses. A
        // Payer-to-Payer record says nothing about Provider Access either way.
        var consent = await _consent.EvaluateOptOutAsync(
            request.TenantId, request.MemberId, RequiredPurpose, asOfUtc: now, ct);

        if (!consent.Allowed)
            return ProviderAccessDecision.Deny(
                ProviderAccessDenialReason.ConsentDenied,
                request.TenantId, request.MemberId, request.ProviderId, now,
                attributed: true, consent: consent);

        return new ProviderAccessDecision
        {
            Allowed = true,
            Reason = ProviderAccessDenialReason.None,
            TenantId = request.TenantId,
            MemberId = request.MemberId,
            ProviderId = request.ProviderId,
            Attributed = true,
            AuthorizingConsentId = consent.ConsentId,
            ConsentDecisionReason = consent.Reason.ToString(),
            EvaluatedAtUtc = now,
        };
    }

    private static ProviderAccessDecision Denied(
        ProviderAccessDenialReason reason, ProviderAccessRequest request, DateTime now)
        => ProviderAccessDecision.Deny(
            reason, request.TenantId, request.MemberId, request.ProviderId, now);
}
