using Microsoft.Extensions.Options;

namespace FhirService.Services.PayerToPayer;

/// <summary>
/// Server-side authorization gate for a Payer-to-Payer exchange: does the member
/// have an active opt-in on record for data sharing? This is decided by Cloud
/// Health Office from its own consent state — never from a value supplied on the
/// inbound request — so a receiving payer cannot self-attest consent.
/// </summary>
public interface IPayerToPayerConsentGate
{
    Task<bool> HasActiveOptInAsync(string tenantId, string memberId, CancellationToken ct = default);
}

/// <summary>
/// Opt-in records the plan holds for Payer-to-Payer data sharing, keyed by tenant.
/// Bound from configuration (synthetic in tests, per-engagement in a real
/// deployment). This is the generic active opt-in signal — it is NOT a dedicated
/// Payer-to-Payer ConsentType, so P2P-03 stays PARTIAL and independent.
/// </summary>
public sealed class PayerToPayerConsentOptions
{
    public const string SectionName = "Cms0057:PayerToPayerConsent";

    /// <summary>Members (by id) with an active opt-in, keyed by tenant id.</summary>
    public Dictionary<string, List<string>> OptedInMembersByTenant { get; set; } = new();
}

/// <summary>
/// Configuration-driven, tenant-scoped, fail-closed consent gate. A member is
/// authorized only if the plan's own opt-in records list them under the request's
/// tenant. An empty catalog authorizes no one.
///
/// This is the server-side authority in the current slice; binding it to the live
/// consent-service registry (an Active consent lookup) is the production wiring
/// and remains engagement work.
/// </summary>
public sealed class ConfiguredPayerToPayerConsentGate : IPayerToPayerConsentGate
{
    private readonly IOptions<PayerToPayerConsentOptions> _options;

    public ConfiguredPayerToPayerConsentGate(IOptions<PayerToPayerConsentOptions> options) => _options = options;

    public Task<bool> HasActiveOptInAsync(string tenantId, string memberId, CancellationToken ct = default)
    {
        var authorized =
            _options.Value.OptedInMembersByTenant.TryGetValue(tenantId, out var members)
            && members.Contains(memberId, StringComparer.Ordinal);
        return Task.FromResult(authorized);
    }
}
