using CloudHealthOffice.Infrastructure.ReferenceData.Payers;
using CloudHealthOffice.Infrastructure.ReferenceData.Payers.Stedi;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Gateways.Stedi;

/// <summary>
/// Resolves a Cloud Health Office payer identifier to a Stedi
/// <c>tradingPartnerServiceId</c> for a given tenant.
///
/// Primary path: <see cref="IPayerReferenceService"/>. Deprecated
/// <c>TenantPayerMap</c>/<c>PayerMap</c> are a fallback only. Arbitrary
/// payer ids are never passed through to Stedi.
/// </summary>
internal interface IStediPayerResolver
{
    Task<PayerResolution> ResolveAsync(string tenantId, string? payerId, CancellationToken ct);
}

internal sealed class StediPayerResolver : IStediPayerResolver
{
    private readonly IPayerReferenceService _payers;
    private readonly IOptions<StediGatewayOptions> _options;
    private readonly ILogger<StediPayerResolver> _logger;

    public StediPayerResolver(
        IPayerReferenceService payers,
        IOptions<StediGatewayOptions> options,
        ILogger<StediPayerResolver> logger)
    {
        _payers = payers;
        _options = options;
        _logger = logger;
    }

    public async Task<PayerResolution> ResolveAsync(
        string tenantId, string? payerId, CancellationToken ct)
    {
        var resolution = await _payers.ResolveForTransactionAsync(
            tenantId,
            payerId,
            HealthcareTransactionType.Eligibility270271,
            StediPayerIdentifiers.System,
            StediPayerIdentifiers.TradingPartnerServiceIdType,
            ct).ConfigureAwait(false);

        if (resolution.Status == PayerResolutionStatus.Found && resolution.Payer is not null)
        {
            var tenantMapped = DeprecatedTenantMap(tenantId, payerId);
            if (!string.IsNullOrWhiteSpace(tenantMapped))
            {
                _logger.LogWarning(
                    "Deprecated TenantPayerMap override used for tenant {TenantId}",
                    Sanitize(tenantId));
                return PayerResolution.Found(resolution.Payer, tenantMapped, usedDeprecatedFallback: true);
            }

            var stediId = resolution.ExternalIdentifierValue ?? SelectStediIdentifier(resolution.Payer);
            if (string.IsNullOrWhiteSpace(stediId))
            {
                return PayerResolution.Fail(
                    PayerResolutionStatus.ExternalIdentifierMissing,
                    $"Payer '{resolution.Payer.Id}' has no Stedi trading-partner identifier.");
            }

            return PayerResolution.Found(resolution.Payer, stediId);
        }

        if (resolution.Status != PayerResolutionStatus.PayerNotFound)
        {
            return resolution;
        }

        var fallback = DeprecatedTenantMap(tenantId, payerId) ?? DeprecatedGlobalMap(payerId);
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            _logger.LogWarning(
                "Deprecated PayerMap/TenantPayerMap fallback used for tenant {TenantId}",
                Sanitize(tenantId));
            return PayerResolution.Found(
                new PayerReference
                {
                    Id = payerId!.Trim(),
                    Name = payerId.Trim(),
                    Active = true
                },
                fallback,
                usedDeprecatedFallback: true);
        }

        return resolution;
    }

    private string? DeprecatedTenantMap(string tenantId, string? payerId)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(payerId))
        {
            return null;
        }

        var opts = _options.Value;
        if (!opts.TenantPayerMap.TryGetValue(tenantId, out var tenantMap) || tenantMap is null)
        {
            return null;
        }

        foreach (var entry in tenantMap)
        {
            if (string.Equals(entry.Key, payerId, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(entry.Value))
            {
                return entry.Value;
            }
        }

        return null;
    }

    private string? DeprecatedGlobalMap(string? payerId)
    {
        if (string.IsNullOrWhiteSpace(payerId))
        {
            return null;
        }

        return _options.Value.PayerMap.TryGetValue(payerId, out var mapped) &&
               !string.IsNullOrWhiteSpace(mapped)
            ? mapped
            : null;
    }

    private static string? SelectStediIdentifier(PayerReference payer)
    {
        string? Find(string type) =>
            payer.ExternalIdentifiers.FirstOrDefault(id =>
                PayerLookup.EqualsNormalized(id.System, StediPayerIdentifiers.System) &&
                PayerLookup.EqualsNormalized(id.Type, type))?.Value;

        return FirstNonEmpty(
            Find(StediPayerIdentifiers.TradingPartnerServiceIdType),
            Find(StediPayerIdentifiers.PrimaryPayerIdType),
            Find(StediPayerIdentifiers.IdType));
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
