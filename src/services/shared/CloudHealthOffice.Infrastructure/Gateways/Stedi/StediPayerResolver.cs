using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Gateways.Stedi;

/// <summary>
/// Resolves a Cloud Health Office canonical payer identifier to the Stedi
/// <c>tradingPartnerServiceId</c> for a given tenant.
///
/// Resolution order:
/// <list type="number">
///   <item>the requesting tenant's entry in <c>TenantPayerMap</c>;</item>
///   <item>the global <c>PayerMap</c>;</item>
///   <item>pass-through of the canonical id when no mapping matches
///         (many deployments store the Stedi id as the payer's external id).</item>
/// </list>
///
/// Tenant safety: only the requesting tenant's sub-map is consulted, so one
/// tenant's payer mapping can never resolve another tenant's identifiers.
/// </summary>
internal interface IStediPayerResolver
{
    /// <summary>
    /// Resolve the Stedi payer id for <paramref name="canonicalPayerId"/> under
    /// <paramref name="tenantId"/>. Returns null when no id can be determined.
    /// </summary>
    string? Resolve(string tenantId, string? canonicalPayerId);
}

internal sealed class StediPayerResolver : IStediPayerResolver
{
    private readonly IOptions<StediGatewayOptions> _options;

    public StediPayerResolver(IOptions<StediGatewayOptions> options) => _options = options;

    public string? Resolve(string tenantId, string? canonicalPayerId)
    {
        if (string.IsNullOrWhiteSpace(canonicalPayerId))
        {
            return null;
        }

        var opts = _options.Value;

        // 1. Tenant-scoped map — only ever the requesting tenant's own entries.
        //    A case-insensitive scan avoids allocating a dictionary per call and
        //    tolerates config keys that differ only by case.
        if (!string.IsNullOrWhiteSpace(tenantId) &&
            opts.TenantPayerMap.TryGetValue(tenantId, out var tenantMap) &&
            tenantMap is not null)
        {
            foreach (var entry in tenantMap)
            {
                if (string.Equals(entry.Key, canonicalPayerId, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(entry.Value))
                {
                    return entry.Value;
                }
            }
        }

        // 2. Global map.
        if (opts.PayerMap.TryGetValue(canonicalPayerId, out var mapped) &&
            !string.IsNullOrWhiteSpace(mapped))
        {
            return mapped;
        }

        // 3. Pass-through: the canonical id is already a Stedi trading-partner id.
        return canonicalPayerId;
    }
}
