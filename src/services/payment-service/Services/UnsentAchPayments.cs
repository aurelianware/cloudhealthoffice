using CloudHealthOffice.NachaTransmission;
using Microsoft.Extensions.Options;
using PaymentService.Models;
using PaymentService.Repositories;

namespace PaymentService.Services;

/// <summary>
/// Whether an issued payment's money may not have left yet: an ACH payment of a completed run
/// whose NACHA file was never generated, or whose current file carries it and has not been
/// recorded as Transmitted. A reversal of such a payment must not be recovered from the
/// provider (its receivable is held as PendingVoid) until the payment is voided before
/// payment or found to have been paid.
///
/// <para>
/// Only where this service sends the files: bank transmission on for the service
/// (<c>BankTransmission:Enabled</c>) and for the tenant
/// (<c>paymentControls.nachaTransmission.enabled</c>). A tenant that sends its files by hand
/// has no Transmitted record to release a hold, so its receivables open as before (Open,
/// recovered by later runs).
/// </para>
/// </summary>
public interface IUnsentAchPayments
{
    /// <summary>Whether receivables of the tenant's unsent payments are held (bank transmission on for the service and the tenant).</summary>
    Task<bool> HoldsReceivablesAsync(string tenantId, CancellationToken cancellationToken = default);

    Task<bool> IsUnsentAsync(Payment payment, CancellationToken cancellationToken = default);
}

public sealed class UnsentAchPayments : IUnsentAchPayments
{
    private readonly IPaymentRunRepository _runs;
    private readonly IPaymentFileTransmissionRepository? _transmissions;
    private readonly BankTransmissionOptions _options;
    private readonly INachaTransmissionSettingsSource? _tenantSettings;
    private readonly Dictionary<string, bool> _holds = new(StringComparer.Ordinal);

    public UnsentAchPayments(IPaymentRunRepository runs, IPaymentFileTransmissionRepository? transmissions = null,
        IOptions<BankTransmissionOptions>? options = null, INachaTransmissionSettingsSource? tenantSettings = null)
    {
        _runs = runs;
        _transmissions = transmissions;
        _options = options?.Value ?? new BankTransmissionOptions();
        _tenantSettings = tenantSettings;
    }

    public async Task<bool> HoldsReceivablesAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return false;
        if (_tenantSettings == null)
            return true; // a transmitter without tenant settings (local folder, dev/test): this service sends every file
        if (_holds.TryGetValue(tenantId, out var known))
            return known;
        bool holds;
        try
        {
            holds = (await _tenantSettings.GetAsync(tenantId, cancellationToken))?.Enabled == true;
        }
        catch (NachaTransmissionException)
        {
            // Transmission is on for the service but the tenant's switch cannot be read now:
            // hold (a held receivable can be released; a provider charged for money it never
            // got cannot be undone as easily).
            holds = true;
        }
        _holds[tenantId] = holds;
        return holds;
    }

    public async Task<bool> IsUnsentAsync(Payment payment, CancellationToken cancellationToken = default)
    {
        if (payment.IsReversal || payment.VoidedBeforePaymentAt != null || string.IsNullOrEmpty(payment.RunId)
            || !string.Equals(payment.PaymentMethod, "ACH", StringComparison.OrdinalIgnoreCase))
            return false;
        var run = await _runs.GetByIdAsync(payment.RunId);
        if (run == null || !string.Equals(run.PaymentMethod, "ACH", StringComparison.OrdinalIgnoreCase))
            return false;
        var file = run.EftFile;
        if (file == null)
            return run.Status == PaymentRunStatus.Completed; // no file yet: nothing was sent
        if (!file.Entries.Any(e => e.PaymentIds.Contains(payment.Id, StringComparer.Ordinal)))
            return false; // paid by check (fallback) or zero: not in the file
        var record = _transmissions == null ? null : await _transmissions.GetAsync(run.TenantId, file.FileReference, cancellationToken);
        return record?.Status != PaymentFileTransmissionStatus.Transmitted;
    }
}
