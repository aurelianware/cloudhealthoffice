using CloudHealthOffice.Consent.Contracts;

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

    public async Task<ConsentDecision> EvaluateAsync(
        string tenantId,
        string memberId,
        ConsentPurposeOfUse purpose,
        DateTime? asOfUtc = null,
        CancellationToken ct = default)
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
                Clean(tenantId), purpose, ex.GetType().Name);
            return ConsentDecision.Deny(purpose, ConsentAuthorizationReason.NoConsentOnRecord, asOf);
        }

        return ConsentAuthorizationPolicy.Evaluate(tenantId, memberId, purpose, consents, asOf);
    }

    /// <summary>Strips CR/LF so an id cannot forge a log entry (CWE-117).</summary>
    private static string Clean(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("\r", string.Empty, StringComparison.Ordinal)
                   .Replace("\n", string.Empty, StringComparison.Ordinal);
}
