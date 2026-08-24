using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Observability;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.ReferenceData.Payers;

/// <summary>
/// Canonical payer identity. Tenant overlays are loaded only for the
/// requesting tenant. Routing lookups are exact-match; search is a separate
/// administrative operation that never selects a transaction target.
/// </summary>
internal sealed class PayerReferenceService : IPayerReferenceService
{
    private readonly IPayerReferenceStore _store;
    private readonly ILogger<PayerReferenceService> _logger;

    public PayerReferenceService(IPayerReferenceStore store, ILogger<PayerReferenceService> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task<PayerReference?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return await _store.GetByIdAsync(id.Trim(), ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<PayerReference>> SearchAsync(
        PayerSearchQuery query, CancellationToken ct = default) =>
        _store.SearchAsync(query, ct);

    public async Task<PayerResolution> ResolveExternalIdentifierAsync(
        string system, string type, string value, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(system) ||
            string.IsNullOrWhiteSpace(type) ||
            string.IsNullOrWhiteSpace(value))
        {
            return PayerResolution.Fail(
                PayerResolutionStatus.PayerNotFound, "External identifier is incomplete.");
        }

        var matches = (await _store.FindExactAsync(PayerLookup.Normalize(value), ct).ConfigureAwait(false))
            .Where(p => p.ExternalIdentifiers.Any(id =>
                PayerLookup.EqualsNormalized(id.System, system) &&
                PayerLookup.EqualsNormalized(id.Type, type) &&
                PayerLookup.EqualsNormalized(id.Value, value)))
            .ToList();

        return Unique(matches, value);
    }

    public async Task<IReadOnlyList<PayerTransactionCapability>> GetSupportedTransactionsAsync(
        string payerId, CancellationToken ct = default)
    {
        var payer = await GetByIdAsync(payerId, ct).ConfigureAwait(false);
        return payer is null
            ? Array.Empty<PayerTransactionCapability>()
            : payer.SupportedTransactions;
    }

    public async Task<PayerResolution> ResolveForTransactionAsync(
        string tenantId,
        string? payerId,
        HealthcareTransactionType transaction,
        string? externalSystem = null,
        string? externalType = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(payerId))
        {
            RecordResolution("not_found");
            return PayerResolution.Fail(
                PayerResolutionStatus.PayerNotFound, "Payer identifier is missing.");
        }

        IReadOnlyList<PayerReference> matches;
        try
        {
            matches = await _store.FindExactAsync(PayerLookup.Normalize(payerId), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Payer reference store failed for tenant {TenantId}",
                Sanitize(tenantId));
            RecordResolution("unavailable");
            return PayerResolution.Fail(
                PayerResolutionStatus.ReferenceDataUnavailable,
                "Payer reference data is temporarily unavailable.");
        }

        var unique = Unique(matches, payerId);
        if (unique.Status != PayerResolutionStatus.Found || unique.Payer is null)
        {
            RecordResolution(unique.Status == PayerResolutionStatus.AmbiguousPayer ? "ambiguous" : "not_found");
            return unique;
        }

        var payer = unique.Payer;
        PayerTenantOverride? overlay = null;
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            overlay = await _store.GetTenantOverrideAsync(tenantId, payer.Id, ct).ConfigureAwait(false);
        }

        if (overlay is { Enabled: false })
        {
            RecordResolution("disabled");
            return PayerResolution.Fail(
                PayerResolutionStatus.PayerDisabled,
                $"Payer '{payer.Id}' is disabled for this tenant.");
        }

        if (!payer.Active && overlay is null)
        {
            RecordResolution("not_found");
            return PayerResolution.Fail(
                PayerResolutionStatus.PayerNotFound,
                $"Payer '{payer.Id}' is no longer active in the directory.");
        }

        var capability = payer.SupportedTransactions.FirstOrDefault(t => t.Transaction == transaction);
        var support = capability?.Support ?? PayerTransactionSupport.NotSupported;
        if (support == PayerTransactionSupport.NotSupported)
        {
            RecordResolution("unsupported");
            return PayerResolution.Fail(
                PayerResolutionStatus.TransactionUnsupported,
                $"Payer '{payer.Id}' does not support {transaction}.");
        }

        if (support == PayerTransactionSupport.EnrollmentRequired &&
            overlay?.EnrolledTransactions.Contains(transaction) != true)
        {
            RecordResolution("enrollment_required");
            return PayerResolution.Fail(
                PayerResolutionStatus.EnrollmentRequired,
                $"Payer '{payer.Id}' requires enrollment before {transaction} can be submitted.");
        }

        var externalValue = SelectExternalIdentifier(payer, overlay, externalSystem, externalType);
        if (!string.IsNullOrWhiteSpace(externalSystem) && string.IsNullOrWhiteSpace(externalValue))
        {
            RecordResolution("missing_identifier");
            return PayerResolution.Fail(
                PayerResolutionStatus.ExternalIdentifierMissing,
                $"Payer '{payer.Id}' has no {externalSystem}/{externalType ?? "id"} identifier.");
        }

        RecordResolution("success");
        return PayerResolution.Found(ApplyOverlay(payer, overlay), externalValue);
    }

    public Task<PayerTenantOverride?> GetTenantOverrideAsync(
        string tenantId, string payerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(payerId))
        {
            return Task.FromResult<PayerTenantOverride?>(null);
        }

        return _store.GetTenantOverrideAsync(tenantId, payerId, ct);
    }

    public Task SaveTenantOverrideAsync(PayerTenantOverride overlay, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(overlay.TenantId) || string.IsNullOrWhiteSpace(overlay.PayerId))
        {
            throw new ArgumentException("TenantId and PayerId are required on a tenant override.");
        }

        return _store.UpsertTenantOverrideAsync(overlay, ct);
    }

    private static PayerResolution Unique(IReadOnlyList<PayerReference> matches, string query)
    {
        var distinct = matches
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (distinct.Count == 0)
        {
            return PayerResolution.Fail(
                PayerResolutionStatus.PayerNotFound,
                $"No payer matched '{query}'.");
        }

        if (distinct.Count > 1)
        {
            return PayerResolution.Fail(
                PayerResolutionStatus.AmbiguousPayer,
                $"Multiple payers matched '{query}'; specify a canonical payer id.");
        }

        return PayerResolution.Found(distinct[0]);
    }

    private static string? SelectExternalIdentifier(
        PayerReference payer,
        PayerTenantOverride? overlay,
        string? system,
        string? type)
    {
        if (string.IsNullOrWhiteSpace(system))
        {
            return null;
        }

        var fromOverlay = FindIdentifier(overlay?.ExternalIdentifiers, system, type);
        if (!string.IsNullOrWhiteSpace(fromOverlay))
        {
            return fromOverlay;
        }

        return FindIdentifier(payer.ExternalIdentifiers, system, type);
    }

    private static string? FindIdentifier(
        IEnumerable<PayerExternalIdentifier>? identifiers, string system, string? type)
    {
        if (identifiers is null)
        {
            return null;
        }

        foreach (var id in identifiers)
        {
            if (!PayerLookup.EqualsNormalized(id.System, system))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(type) || PayerLookup.EqualsNormalized(id.Type, type))
            {
                return string.IsNullOrWhiteSpace(id.Value) ? null : id.Value;
            }
        }

        return null;
    }

    private static PayerReference ApplyOverlay(PayerReference payer, PayerTenantOverride? overlay)
    {
        if (overlay is null)
        {
            return payer;
        }

        if (!string.IsNullOrWhiteSpace(overlay.PreferredAlias) &&
            !payer.Aliases.Contains(overlay.PreferredAlias, StringComparer.OrdinalIgnoreCase))
        {
            payer.Aliases.Add(overlay.PreferredAlias);
        }

        foreach (var id in overlay.ExternalIdentifiers)
        {
            payer.ExternalIdentifiers.RemoveAll(existing =>
                PayerLookup.EqualsNormalized(existing.System, id.System) &&
                PayerLookup.EqualsNormalized(existing.Type, id.Type));
            payer.ExternalIdentifiers.Add(id);
        }

        return payer;
    }

    private static void RecordResolution(string result) =>
        ChoMetrics.PayerResolutionTotal.Add(1, new KeyValuePair<string, object?>("cho.result", result));

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
