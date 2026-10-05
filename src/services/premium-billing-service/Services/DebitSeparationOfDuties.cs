using CloudHealthOffice.Infrastructure.Security;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;

namespace PremiumBillingService.Services;

/// <summary>
/// Thrown when the acting user may not release a debit (maker-checker).
/// Controllers answer 403 "Separation of duties".
/// </summary>
public sealed class SeparationOfDutiesException : Exception
{
    public SeparationOfDutiesException(string message) : base(message) { }
}

/// <summary>
/// Maker-checker for pulling money from a sponsor's account, after the
/// capitation-service precedent (PaymentSeparationOfDuties): the user who
/// created or executed the billing run that produced an invoice (the makers)
/// cannot initiate an EFT/ACH debit against it. Releasing a debit also needs a
/// user: a service token (which satisfies every tenant permission) is refused.
/// There is no per-tenant override (as with provider-service bank-account
/// changes). An invoice with no recorded maker (legacy runs whose creator came
/// from the request body and was usually empty) is allowed and logged.
/// </summary>
public sealed class DebitSeparationOfDuties
{
    public static readonly EventId NoMakerRecordedEvent = new(4611, "PremiumDebitNoMakerRecorded");

    /// <summary>The placeholder older invoices carry instead of a person.</summary>
    internal const string LegacyInvoiceCreator = "billing-run";

    private readonly IBillingRunRepository _billingRuns;
    private readonly ICurrentActor _actor;
    private readonly ILogger _logger;

    public DebitSeparationOfDuties(IBillingRunRepository billingRuns, ICurrentActor actor, ILogger logger)
    {
        _billingRuns = billingRuns;
        _actor = actor;
        _logger = logger;
    }

    /// <summary>Throws <see cref="SeparationOfDutiesException"/> when the actor may not debit these invoices.</summary>
    public async Task EnsureMayReleaseAsync(IEnumerable<PremiumInvoice> invoices)
    {
        if (_actor.IsService)
            throw new SeparationOfDutiesException(
                "Separation of duties: releasing a sponsor debit needs a user with payments:approve, not a service token");

        var user = _actor.UserId;
        var runCache = new Dictionary<string, BillingRun?>(StringComparer.Ordinal);

        foreach (var invoice in invoices)
        {
            var makers = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(invoice.CreatedBy) && invoice.CreatedBy != LegacyInvoiceCreator)
                makers.Add(invoice.CreatedBy);

            if (!string.IsNullOrEmpty(invoice.BillingRunId))
            {
                if (!runCache.TryGetValue(invoice.BillingRunId, out var run))
                {
                    run = await _billingRuns.GetByIdAsync(invoice.BillingRunId);
                    runCache[invoice.BillingRunId] = run;
                }
                if (!string.IsNullOrEmpty(run?.CreatedBy)) makers.Add(run.CreatedBy);
                if (!string.IsNullOrEmpty(run?.ExecutedBy)) makers.Add(run.ExecutedBy);
            }

            if (makers.Count == 0)
            {
                _logger.LogWarning(NoMakerRecordedEvent,
                    "No maker recorded for invoice {InvoiceNumber}; debit by {User} allowed without a maker-checker comparison",
                    invoice.InvoiceNumber, user);
                continue;
            }

            if (makers.Contains(user))
                throw new SeparationOfDutiesException(
                    $"Separation of duties: you prepared invoice {invoice.InvoiceNumber} (created or executed its billing run); " +
                    "a different user with payments:approve must initiate the debit");
        }
    }
}
