using CloudHealthOffice.Consent.Contracts;
using CloudHealthOffice.Infrastructure.Logging;

namespace FhirService.Services.Consent;

/// <summary>
/// Reads a member's consent records for an authorization decision. Purpose-agnostic
/// on purpose: a source returns EVERYTHING on record for the member, and
/// <see cref="ConsentAuthorizationPolicy"/> decides which purpose is satisfied.
/// A source that filtered by purpose could widen authorization by returning the
/// wrong subset, so it is not allowed to try.
///
/// The registry in consent-service is authoritative; this is the seam
/// fhir-service reads it through, shared by every purpose.
/// </summary>
public interface IConsentSource
{
    /// <summary>
    /// Every consent on record for the member. A registry that cannot be read
    /// throws <see cref="ConsentRegistryUnavailableException"/>; it never
    /// answers with an empty list, which for an opt-out purpose would mean "the
    /// member never opted out".
    /// </summary>
    Task<IReadOnlyList<ConsentAuthorizationSnapshot>> GetConsentsAsync(
        string tenantId, string memberId, CancellationToken ct = default);
}

/// <summary>
/// Answers "has this member authorized THIS purpose, right now?" for any purpose.
///
/// One implementation serves every purpose so that Payer-to-Payer and Provider
/// Access cannot drift apart: the same fail-closed read, the same
/// <see cref="ConsentAuthorizationPolicy"/>, the same lifecycle rules. Adding a
/// purpose adds no logic here.
/// </summary>
public interface IConsentEvaluator
{
    /// <summary>
    /// Evaluates <paramref name="purpose"/> for the member as of
    /// <paramref name="asOfUtc"/> (defaulting to now). Point-in-time by design —
    /// the instant is chosen by the plan, never taken from a request.
    /// </summary>
    Task<ConsentDecision> EvaluateAsync(
        string tenantId,
        string memberId,
        ConsentPurposeOfUse purpose,
        DateTime? asOfUtc = null,
        CancellationToken ct = default);

    /// <summary>
    /// Evaluates a purpose that is allowed unless the member has opted out
    /// (CMS-0057-F Provider Access), by
    /// <see cref="ConsentAuthorizationPolicy.EvaluateOptOut"/>. An unreadable
    /// registry refuses: it cannot show the member has not opted out.
    /// </summary>
    Task<ConsentDecision> EvaluateOptOutAsync(
        string tenantId,
        string memberId,
        ConsentPurposeOfUse purpose,
        DateTime? asOfUtc = null,
        CancellationToken ct = default);
}

/// <summary>Which consent source fhir-service uses when no registry URL is configured.</summary>
public static class ConsentSourceSelection
{
    /// <summary>
    /// The configured catalog stands in for the registry in Development and
    /// Testing, and where a deployment deliberately configured one (a demo).
    /// Anywhere else, with neither, <see cref="UnavailableConsentSource"/> refuses
    /// every decision.
    /// </summary>
    public static bool UseConfiguredCatalog(IHostEnvironment environment, IConfiguration configuration) =>
        environment.IsDevelopment()
        || environment.IsEnvironment("Testing")
        || configuration.GetSection(
                FhirService.Services.PayerToPayer.PayerToPayerConsentOptions.SectionName + ":ConsentsByTenant")
            .GetChildren().Any();
}

/// <summary>
/// The source when no consent registry is configured outside Development and no
/// catalog was configured either: every read fails, so every consent decision
/// refuses. Never an empty answer, which Provider Access would read as "nobody
/// opted out" (ADR 021).
/// </summary>
public sealed class UnavailableConsentSource : FhirService.Services.PayerToPayer.IPayerToPayerConsentSource
{
    private readonly ILogger<UnavailableConsentSource> _logger;

    public UnavailableConsentSource(ILogger<UnavailableConsentSource> logger) => _logger = logger;

    public Task<IReadOnlyList<ConsentAuthorizationSnapshot>> GetConsentsAsync(
        string tenantId, string memberId, CancellationToken ct = default)
    {
        _logger.LogError("No consent registry is configured (Services:ConsentServiceUrl); consent decisions are refused.");
        throw new ConsentRegistryUnavailableException("No consent registry is configured.");
    }
}

/// <summary>The consent registry could not be read: unreachable, refused, or unreadable.</summary>
public sealed class ConsentRegistryUnavailableException : Exception
{
    public ConsentRegistryUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// The production evaluator: read the member's consents from the registry, then
/// apply the shared policy. It holds no rules of its own — every lifecycle and
/// purpose decision belongs to <see cref="ConsentAuthorizationPolicy"/>, so the
/// answer is identical whichever caller asks.
///
/// Fail-closed at every edge: a blank tenant or member, a source that throws, and
/// a registry that returns nothing all deny. An unreadable registry is not
/// permission.
/// </summary>
public sealed class RegistryConsentEvaluator : IConsentEvaluator
{
    private readonly IConsentSource _source;
    private readonly ILogger<RegistryConsentEvaluator> _logger;

    public RegistryConsentEvaluator(IConsentSource source, ILogger<RegistryConsentEvaluator> logger)
    {
        _source = source;
        _logger = logger;
    }

    public Task<ConsentDecision> EvaluateAsync(
        string tenantId,
        string memberId,
        ConsentPurposeOfUse purpose,
        DateTime? asOfUtc = null,
        CancellationToken ct = default) =>
        DecideAsync(tenantId, memberId, purpose, asOfUtc, ConsentAuthorizationPolicy.Evaluate, ct);

    public Task<ConsentDecision> EvaluateOptOutAsync(
        string tenantId,
        string memberId,
        ConsentPurposeOfUse purpose,
        DateTime? asOfUtc = null,
        CancellationToken ct = default) =>
        DecideAsync(tenantId, memberId, purpose, asOfUtc, ConsentAuthorizationPolicy.EvaluateOptOut, ct);

    private async Task<ConsentDecision> DecideAsync(
        string tenantId,
        string memberId,
        ConsentPurposeOfUse purpose,
        DateTime? asOfUtc,
        Func<string, string, ConsentPurposeOfUse, IReadOnlyList<ConsentAuthorizationSnapshot>, DateTime, ConsentDecision> policy,
        CancellationToken ct)
    {
        var asOf = asOfUtc ?? DateTime.UtcNow;

        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(memberId))
            return ConsentDecision.Deny(purpose, ConsentAuthorizationReason.NoConsentOnRecord, asOf);

        IReadOnlyList<ConsentAuthorizationSnapshot> consents;
        try
        {
            consents = await _source.GetConsentsAsync(tenantId, memberId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Category only — the exception can carry registry detail.
            _logger.LogWarning(
                "Consent lookup failed for tenant={Tenant} purpose={Purpose}; denying ({Fault}).",
                LogSanitizer.Sanitize(tenantId), purpose, ex.GetType().Name);
            return ConsentDecision.Deny(purpose, ConsentAuthorizationReason.RegistryUnavailable, asOf);
        }

        return policy(tenantId, memberId, purpose, consents, asOf);
    }
}
