using PaymentService.Models;
using PaymentService.Repositories;

namespace PaymentService.Services;

/// <summary>
/// Whether an issued payment's money may not have left yet: an ACH payment of a completed run
/// whose NACHA file was never generated, or whose current file carries it and has not been
/// recorded as Transmitted. A reversal of such a payment must not be recovered from the
/// provider (its receivable is held as PendingVoid) until the payment is voided before
/// payment or found to have been paid.
/// </summary>
public interface IUnsentAchPayments
{
    Task<bool> IsUnsentAsync(Payment payment, CancellationToken cancellationToken = default);
}

public sealed class UnsentAchPayments : IUnsentAchPayments
{
    private readonly IPaymentRunRepository _runs;
    private readonly IPaymentFileTransmissionRepository? _transmissions;

    public UnsentAchPayments(IPaymentRunRepository runs, IPaymentFileTransmissionRepository? transmissions = null)
    {
        _runs = runs;
        _transmissions = transmissions;
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
