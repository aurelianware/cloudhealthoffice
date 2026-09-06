using CloudHealthOffice.Consent.Contracts;
using FhirService.Services.Consent;
using Microsoft.Extensions.Options;

namespace FhirService.Services.PayerToPayer;

/// <summary>
/// Server-side authorization gate for a Payer-to-Payer exchange: has the member
/// authorized THIS purpose? Decided by Cloud Health Office from its own consent
/// registry — never from a value on the request — so neither a receiving payer
/// nor an internal caller can self-attest consent.
///
/// The gate returns a <see cref="ConsentDecision"/> rather than a bool so the
/// exchange can record WHICH authorization allowed a disclosure, and so a
/// refusal carries a reason an operator can act on ("revoked" and "expired" and
/// "they never granted this purpose" are different facts).
///
/// Both directions of the exchange use this one gate: the inbound respond and
/// the outbound initiation ask the same question of the same registry, so one
/// direction cannot drift more permissive than the other.
/// </summary>
public interface IPayerToPayerConsentGate
{
    /// <summary>
    /// Evaluates Payer-to-Payer authorization for the member as of
    /// <paramref name="asOfUtc"/> (defaulting to now). Point-in-time by design:
    /// a multi-step exchange re-asks rather than carrying an earlier answer
    /// forward across a disclosure boundary.
    /// </summary>
    Task<ConsentDecision> EvaluateAsync(
        string tenantId, string memberId, DateTime? asOfUtc = null, CancellationToken ct = default);
}

/// <summary>
/// The Payer-to-Payer name for the shared consent seam. It adds nothing to
/// <see cref="IConsentSource"/> — one contract, so the two cannot drift apart in
/// signature, documentation, or annotation — and exists only so the
/// Payer-to-Payer wiring keeps a name that says what it is for.
///
/// Like every consent source, an implementation returns EVERYTHING on record for
/// the member: purpose filtering and lifecycle evaluation belong to
/// <c>ConsentAuthorizationPolicy</c>, so a source cannot widen authorization by
/// returning the wrong subset.
/// </summary>
public interface IPayerToPayerConsentSource : IConsentSource
{
}

/// <summary>
/// The member's consent records as configuration, for Demo mode and tests —
/// the same shape the registry serves, not a shortcut around it. Each entry
/// carries a purpose, a lifecycle status, and an effective period, so a Demo
/// deployment exercises the real policy rather than a boolean allow-list.
/// </summary>
public sealed class PayerToPayerConsentOptions
{
    public const string SectionName = "Cms0057:PayerToPayerConsent";

    /// <summary>Consent records held for members, keyed by tenant id.</summary>
    public Dictionary<string, List<ConfiguredConsentRecord>> ConsentsByTenant { get; set; } = new();
}

/// <summary>One configured consent record. Mirrors the registry's authorization projection.</summary>
public sealed class ConfiguredConsentRecord
{
    public string MemberId { get; set; } = string.Empty;
    public string? ConsentId { get; set; }

    /// <summary>
    /// Purpose this record authorizes. Defaults to <c>Unspecified</c>, which
    /// authorizes nothing — a misconfigured entry fails closed rather than
    /// granting the exchange.
    /// </summary>
    public ConsentPurposeOfUse PurposeOfUse { get; set; } = ConsentPurposeOfUse.Unspecified;

    public ConsentLifecycleStatus Status { get; set; } = ConsentLifecycleStatus.Draft;

    public DateTime? EffectiveAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

/// <summary>
/// Configuration-backed consent source (Demo default, and the fallback when no
/// consent registry is configured). An empty catalog authorizes no one.
/// </summary>
public sealed class ConfiguredPayerToPayerConsentSource : IPayerToPayerConsentSource, IConsentSource
{
    private readonly IOptions<PayerToPayerConsentOptions> _options;

    public ConfiguredPayerToPayerConsentSource(IOptions<PayerToPayerConsentOptions> options)
        => _options = options;

    public Task<IReadOnlyList<ConsentAuthorizationSnapshot>> GetConsentsAsync(
        string tenantId, string memberId, CancellationToken ct = default)
    {
        if (!_options.Value.ConsentsByTenant.TryGetValue(tenantId, out var records) || records is null)
            return Task.FromResult<IReadOnlyList<ConsentAuthorizationSnapshot>>(
                Array.Empty<ConsentAuthorizationSnapshot>());

        var snapshots = records
            .Where(r => string.Equals(r.MemberId, memberId, StringComparison.Ordinal))
            .Select((r, index) => new ConsentAuthorizationSnapshot
            {
                // Tenant and member come from the LOOKUP, not from the record, so
                // a config entry cannot claim to be another member's consent.
                TenantId = tenantId,
                MemberId = memberId,
                ConsentId = string.IsNullOrWhiteSpace(r.ConsentId)
                    ? $"configured-consent-{index}"
                    : r.ConsentId,
                PurposeOfUse = r.PurposeOfUse,
                Status = r.Status,
                EffectiveAt = r.EffectiveAt,
                ExpiresAt = r.ExpiresAt,
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<ConsentAuthorizationSnapshot>>(snapshots);
    }
}

/// <summary>
/// The production gate: reads the member's consents from the registry and
/// applies <see cref="ConsentAuthorizationPolicy"/> for the Payer-to-Payer
/// purpose. It holds no policy of its own — the shared policy decides, so the
/// answer is identical whichever service asks.
///
/// Fail-closed at every edge: a source that throws, a tenant or member that is
/// blank, or a registry that returns nothing all deny.
/// </summary>
public sealed class ConsentRegistryPayerToPayerConsentGate : IPayerToPayerConsentGate
{
    /// <summary>The purpose a Payer-to-Payer exchange requires. Not configurable.</summary>
    public const ConsentPurposeOfUse RequiredPurpose = ConsentPurposeOfUse.PayerToPayerExchange;

    private readonly IConsentEvaluator _evaluator;

    public ConsentRegistryPayerToPayerConsentGate(IConsentEvaluator evaluator)
        => _evaluator = evaluator;

    public Task<ConsentDecision> EvaluateAsync(
        string tenantId, string memberId, DateTime? asOfUtc = null, CancellationToken ct = default)
        => _evaluator.EvaluateAsync(tenantId, memberId, RequiredPurpose, asOfUtc, ct);
}
