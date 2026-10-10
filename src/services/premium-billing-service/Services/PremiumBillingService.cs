using CloudHealthOffice.Infrastructure.Security;
using PremiumBillingService.Clients;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;

namespace PremiumBillingService.Services;

public interface IPremiumBillingService
{
    Task<BillingRun> CreateBillingRunAsync(CreateBillingRunRequest request, string? createdBy);
    Task<BillingRun> ExecuteBillingRunAsync(string billingRunId);
    Task<BillingRun> GetBillingRunAsync(string billingRunId);
    Task<IEnumerable<BillingRun>> GetBillingRunsAsync(DateTime? from, DateTime? to);
    Task CancelBillingRunAsync(string billingRunId);
    Task<PremiumInvoice> RecordPaymentAsync(string invoiceId, RecordPaymentRequest request);
    Task<PremiumInvoice> VoidInvoiceAsync(string invoiceId, string reason);
    Task<PremiumInvoice> MarkInvoiceSentAsync(string invoiceId);
    Task<IEnumerable<PremiumInvoice>> GetOverdueInvoicesAsync();
    Task<AgingReport> GetAgingReportAsync();
    Task<DelinquencyRunResult> ProcessDelinquenciesAsync();
}

public class PremiumBillingService : IPremiumBillingService
{
    private readonly IBillingRunRepository _billingRunRepository;
    private readonly IPremiumInvoiceRepository _invoiceRepository;
    private readonly ISponsorServiceClient _sponsorClient;
    private readonly ICoverageServiceClient _coverageClient;
    private readonly ICurrentActor _actor;
    private readonly ILogger<PremiumBillingService> _logger;
    private readonly ISponsorAccountRepository? _sponsorAccounts;
    private readonly IRatedInvoiceGenerator? _ratedInvoices;
    private readonly RatedBillingOptions _ratedOptions;

    public PremiumBillingService(
        IBillingRunRepository billingRunRepository,
        IPremiumInvoiceRepository invoiceRepository,
        ISponsorServiceClient sponsorClient,
        ICoverageServiceClient coverageClient,
        ICurrentActor actor,
        ILogger<PremiumBillingService> logger,
        ISponsorAccountRepository? sponsorAccounts = null,
        IRatedInvoiceGenerator? ratedInvoices = null,
        Microsoft.Extensions.Options.IOptions<RatedBillingOptions>? ratedOptions = null)
    {
        _sponsorAccounts = sponsorAccounts;
        _ratedInvoices = ratedInvoices;
        _ratedOptions = ratedOptions?.Value ?? new RatedBillingOptions();
        _billingRunRepository = billingRunRepository;
        _invoiceRepository = invoiceRepository;
        _sponsorClient = sponsorClient;
        _coverageClient = coverageClient;
        _actor = actor;
        _logger = logger;
    }

    /// <summary>The acting user (or service) from the token; never from a body.</summary>
    private string ActorId => _actor.UserId;

    public async Task<BillingRun> CreateBillingRunAsync(CreateBillingRunRequest request, string? createdBy)
    {
        // Normalize billing period to first of month
        var billingPeriod = new DateTime(request.BillingPeriod.Year, request.BillingPeriod.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var billingRun = new BillingRun
        {
            BillingRunNumber = $"BR-{billingPeriod:yyyy-MM}-{Guid.NewGuid().ToString()[..4].ToUpperInvariant()}",
            BillingPeriod = billingPeriod,
            Description = request.Description,
            Criteria = request.Criteria,
            CreatedBy = createdBy,
            // A scheduler creating the run carries a service token: record that
            // the creator is a service identity rather than a person.
            CreatedByIsService = _actor.IsAuthenticated && _actor.IsService,
            Status = BillingRunStatus.Pending
        };

        return await _billingRunRepository.CreateAsync(billingRun);
    }

    public async Task<BillingRun> ExecuteBillingRunAsync(string billingRunId)
    {
        var billingRun = await _billingRunRepository.GetByIdAsync(billingRunId)
            ?? throw new InvalidOperationException($"Billing run {billingRunId} not found");

        if (billingRun.Status != BillingRunStatus.Pending)
            throw new InvalidOperationException($"Billing run is in {billingRun.Status} state, expected Pending");

        billingRun.Status = BillingRunStatus.Running;
        billingRun.ExecutionStartedAt = DateTime.UtcNow;
        billingRun.ExecutedBy = ActorId;
        billingRun.ExecutedByIsService = _actor.IsService;
        await _billingRunRepository.UpdateAsync(billingRun);

        try
        {
            // Fetch active sponsors
            var sponsors = await FetchActiveSponsorsAsync(billingRun.TenantId, billingRun.Criteria);
            _logger.LogInformation("Found {Count} active sponsors for billing run {BillingRunNumber}",
                sponsors.Count, billingRun.BillingRunNumber);

            decimal totalPremium = 0;
            decimal totalAdjustments = 0;
            int totalMembers = 0;

            // Rated billing is per tenant and off unless configured (PremiumBilling:RatedBilling).
            var rated = _ratedOptions.For(billingRun.TenantId).Enabled;
            if (rated && _ratedInvoices == null)
                throw new InvalidOperationException("Rated billing is enabled for this tenant but the rated invoice generator is not registered");

            foreach (var sponsor in sponsors)
            {
                try
                {
                    PremiumInvoice invoice;
                    if (rated)
                    {
                        var outcome = await _ratedInvoices!.GenerateAsync(
                            sponsor, billingRun.TenantId, billingRun.BillingPeriod, billingRun.Id, billingRun.ExecutedBy);
                        invoice = outcome.Invoice;
                        if (outcome.Kind is RatedInvoiceOutcomeKind.AlreadyIssued or RatedInvoiceOutcomeKind.UnratedInvoiceExists)
                        {
                            // Idempotent: an issued invoice is never regenerated or duplicated.
                            billingRun.UnchangedInvoiceIds.Add(invoice.Id);
                            billingRun.Warnings.Add(
                                $"Group {sponsor.GroupNumber} already has invoice {invoice.InvoiceNumber} ({invoice.Status}) for {billingRun.BillingPeriod:yyyy-MM}; left unchanged");
                            continue;
                        }
                        if (invoice.Status == InvoiceStatus.Draft)
                        {
                            billingRun.DraftInvoiceIds.Add(invoice.Id);
                            billingRun.Warnings.Add(invoice.RatingExceptions.Count > 0
                                ? $"Invoice {invoice.InvoiceNumber} for group {sponsor.GroupNumber} is Draft: {invoice.RatingExceptions.Count} rating exception(s), first: {invoice.RatingExceptions[0].Message}"
                                : $"Invoice {invoice.InvoiceNumber} for group {sponsor.GroupNumber} is Draft, held for review");
                            // A draft is not billed: it is not in the run's invoices or totals.
                            continue;
                        }
                    }
                    else
                    {
                        invoice = await GenerateInvoiceForSponsorAsync(
                            sponsor, billingRun.TenantId, billingRun.BillingPeriod, billingRun.Id, billingRun.ExecutedBy);
                    }

                    billingRun.InvoiceIds.Add(invoice.Id);
                    totalPremium += invoice.SubtotalPremium;
                    totalAdjustments += invoice.TotalAdjustments;
                    totalMembers += invoice.MemberCount;
                }
                catch (Exception ex)
                {
                    var msg = $"Failed to generate invoice for group {sponsor.GroupNumber}: {ex.Message}";
                    billingRun.Warnings.Add(msg);
                    _logger.LogWarning(ex, "Failed to generate invoice for group {GroupNumber}", sponsor.GroupNumber);
                }
            }

            billingRun.TotalInvoices = billingRun.InvoiceIds.Count;
            billingRun.TotalPremiumAmount = totalPremium;
            billingRun.TotalAdjustmentAmount = totalAdjustments;
            billingRun.TotalMembers = totalMembers;
            billingRun.Status = BillingRunStatus.Completed;
            billingRun.ExecutionCompletedAt = DateTime.UtcNow;
            billingRun.ExecutionDurationSeconds = (billingRun.ExecutionCompletedAt.Value - billingRun.ExecutionStartedAt!.Value).TotalSeconds;

            _logger.LogInformation(
                "Billing run {BillingRunNumber} completed: {InvoiceCount} invoices, ${TotalPremium:N2} total premium, {MemberCount} members",
                billingRun.BillingRunNumber, billingRun.TotalInvoices, billingRun.TotalPremiumAmount, billingRun.TotalMembers);
        }
        catch (Exception ex)
        {
            billingRun.Status = BillingRunStatus.Failed;
            billingRun.Errors.Add(ex.Message);
            billingRun.ExecutionCompletedAt = DateTime.UtcNow;
            _logger.LogError(ex, "Billing run {BillingRunNumber} failed", billingRun.BillingRunNumber);
        }

        return await _billingRunRepository.UpdateAsync(billingRun);
    }

    public async Task<BillingRun> GetBillingRunAsync(string billingRunId)
    {
        return await _billingRunRepository.GetByIdAsync(billingRunId)
            ?? throw new InvalidOperationException($"Billing run {billingRunId} not found");
    }

    public async Task<IEnumerable<BillingRun>> GetBillingRunsAsync(DateTime? from, DateTime? to)
    {
        return await _billingRunRepository.SearchAsync(from, to);
    }

    public async Task CancelBillingRunAsync(string billingRunId)
    {
        var billingRun = await _billingRunRepository.GetByIdAsync(billingRunId)
            ?? throw new InvalidOperationException($"Billing run {billingRunId} not found");

        if (billingRun.Status != BillingRunStatus.Pending)
            throw new InvalidOperationException($"Can only cancel billing runs in Pending state, current: {billingRun.Status}");

        billingRun.Status = BillingRunStatus.Cancelled;
        billingRun.CancelledBy = ActorId;
        await _billingRunRepository.UpdateAsync(billingRun);
    }

    public async Task<PremiumInvoice> RecordPaymentAsync(string invoiceId, RecordPaymentRequest request)
    {
        var payment = new InvoicePayment
        {
            Amount = request.Amount,
            PaymentDate = request.PaymentDate,
            PaymentMethod = request.PaymentMethod,
            ReferenceNumber = request.ReferenceNumber,
            ReceivedDate = DateTime.UtcNow,
            RecordedBy = ActorId
        };

        // Re-read and re-applied on a concurrent change; a conflict that persists
        // reaches the API as 409 (ConcurrencyConflictException).
        var updated = await InvoiceWrites.UpdateWithRetryAsync(_invoiceRepository, invoiceId, null, invoice =>
        {
            if (invoice.Status is InvoiceStatus.Voided or InvoiceStatus.WriteOff or InvoiceStatus.Draft)
                throw new InvalidOperationException($"Cannot record payment on {invoice.Status} invoice");
            if (invoice.Payments.Any(p => p.PaymentId == payment.PaymentId))
                return false;
            invoice.Payments.Add(payment);
            invoice.RecalculateTotals();
            invoice.LastUpdatedBy = ActorId;
            PremiumInvoiceStatus.ApplyPaymentStatus(invoice);
            return true;
        }) ?? throw new InvalidOperationException($"Invoice {invoiceId} not found");

        _logger.LogInformation("Recorded payment of ${Amount:N2} on invoice {InvoiceNumber}, balance due: ${BalanceDue:N2}",
            request.Amount, updated.InvoiceNumber, updated.BalanceDue);

        // The sponsor's account balance follows the invoice it was paid on.
        if (_sponsorAccounts != null && !string.IsNullOrEmpty(updated.GroupNumber))
            await SponsorAccountBalances.RefreshAsync(_invoiceRepository, _sponsorAccounts, updated.GroupNumber, request.PaymentDate);

        return updated;
    }

    public async Task<PremiumInvoice> VoidInvoiceAsync(string invoiceId, string reason)
    {
        var updated = await InvoiceWrites.UpdateWithRetryAsync(_invoiceRepository, invoiceId, null, invoice =>
        {
            if (invoice.Status == InvoiceStatus.Paid)
                throw new InvalidOperationException("Cannot void a fully paid invoice");
            if (invoice.Status == InvoiceStatus.Voided)
                return false;

            invoice.Status = InvoiceStatus.Voided;
            invoice.LastUpdatedBy = ActorId;
            invoice.Adjustments.Add(new InvoiceAdjustment
            {
                Type = AdjustmentType.Other,
                Description = $"Invoice voided: {reason}",
                Amount = 0,
                AdjustmentDate = DateTime.UtcNow
            });
            return true;
        }) ?? throw new InvalidOperationException($"Invoice {invoiceId} not found");

        _logger.LogInformation("Voided invoice {InvoiceNumber}: {Reason}", updated.InvoiceNumber, SanitizeForLog(reason));

        // A voided invoice no longer counts toward the sponsor's open balance.
        if (_sponsorAccounts != null && !string.IsNullOrEmpty(updated.GroupNumber))
            await SponsorAccountBalances.RefreshAsync(_invoiceRepository, _sponsorAccounts, updated.GroupNumber, null);

        return updated;
    }

    public async Task<PremiumInvoice> MarkInvoiceSentAsync(string invoiceId)
    {
        return await InvoiceWrites.UpdateWithRetryAsync(_invoiceRepository, invoiceId, null, invoice =>
        {
            if (invoice.Status != InvoiceStatus.Generated)
                throw new InvalidOperationException($"Can only mark Generated invoices as Sent, current: {invoice.Status}");
            invoice.Status = InvoiceStatus.Sent;
            invoice.LastUpdatedBy = ActorId;
            return true;
        }) ?? throw new InvalidOperationException($"Invoice {invoiceId} not found");
    }

    public async Task<IEnumerable<PremiumInvoice>> GetOverdueInvoicesAsync()
    {
        return await _invoiceRepository.GetOverdueAsync();
    }

    public async Task<AgingReport> GetAgingReportAsync()
    {
        var overdueInvoices = (await _invoiceRepository.GetOverdueAsync()).ToList();
        var now = DateTime.UtcNow;

        var report = new AgingReport();

        foreach (var invoice in overdueInvoices)
        {
            var daysOverdue = (now - invoice.DueDate).Days;

            if (daysOverdue <= 30)
            {
                report.CurrentAmount += invoice.BalanceDue;
                report.CurrentCount++;
            }
            else if (daysOverdue <= 60)
            {
                report.ThirtyDayAmount += invoice.BalanceDue;
                report.ThirtyDayCount++;
            }
            else if (daysOverdue <= 90)
            {
                report.SixtyDayAmount += invoice.BalanceDue;
                report.SixtyDayCount++;
            }
            else
            {
                report.NinetyPlusDayAmount += invoice.BalanceDue;
                report.NinetyPlusDayCount++;
            }
        }

        report.TotalOutstanding = report.CurrentAmount + report.ThirtyDayAmount + report.SixtyDayAmount + report.NinetyPlusDayAmount;
        report.TotalCount = overdueInvoices.Count;

        return report;
    }

    public async Task<DelinquencyRunResult> ProcessDelinquenciesAsync()
    {
        var overdueInvoices = (await _invoiceRepository.GetOverdueAsync()).ToList();
        var now = DateTime.UtcNow;
        var result = new DelinquencyRunResult();
        var actor = ActorId;
        // One suspension call per sponsor per run, however many of its invoices are delinquent.
        var outcomes = new Dictionary<string, SponsorSuspensionOutcome>(StringComparer.Ordinal);

        foreach (var invoice in overdueInvoices)
        {
            // One invoice failing (a conflict that persists, a database error) must not
            // stop the run for the others: it is reported and picked up by the next run.
            try
            {
                await ProcessDelinquencyAsync(invoice, now, actor, outcomes, result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.InvoiceFailures.Add(new DelinquencyInvoiceFailure
                {
                    InvoiceId = invoice.Id,
                    InvoiceNumber = invoice.InvoiceNumber,
                    GroupNumber = invoice.GroupNumber,
                    Error = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message
                });
                _logger.LogError(ex, "Delinquency processing failed for invoice {InvoiceNumber}; the run continues",
                    SanitizeForLog(invoice.InvoiceNumber));
            }
        }

        if (result.SuspensionFailures.Count > 0)
            _logger.LogError(
                "Delinquency processing: {Failed} sponsor suspension(s) failed and are recorded on their invoices for retry",
                result.SuspensionFailures.Count);

        _logger.LogInformation("Delinquency processing complete: {Count} invoices marked delinquent, {Failed} invoice(s) failed",
            result.DelinquentCount, result.InvoiceFailures.Count);
        return result;
    }

    private enum DelinquencyStep { NewlyDelinquent, SuspensionRetry, Overdue }

    /// <summary>
    /// Moves one overdue invoice along (Overdue, or Delinquent with sponsor
    /// suspension). On a save conflict the invoice is re-read and re-evaluated:
    /// if a payment made it no longer overdue, it is left alone. Counters and
    /// failures are recorded only for the save that succeeded.
    /// </summary>
    private async Task ProcessDelinquencyAsync(PremiumInvoice listed, DateTime now, string actor,
        Dictionary<string, SponsorSuspensionOutcome> outcomes, DelinquencyRunResult result)
    {
        var invoice = (PremiumInvoice?)listed;
        for (var attempt = 1; ; attempt++)
        {
            if (invoice == null || !IsStillOverdue(invoice, now))
            {
                if (attempt > 1)
                    result.SkippedAfterConflict++;
                return;
            }

            DelinquencyStep step;
            SponsorSuspensionFailure? failure = null;
            if (invoice.GracePeriodExpires.HasValue && now > invoice.GracePeriodExpires.Value
                && invoice.Status != InvoiceStatus.Delinquent)
            {
                step = DelinquencyStep.NewlyDelinquent;
                invoice.Status = InvoiceStatus.Delinquent;
                invoice.LastUpdatedBy = actor;
                failure = RecordSuspension(invoice, actor, await SuspensionOutcomeAsync(invoice, outcomes, result));
            }
            else if (invoice.Status == InvoiceStatus.Delinquent
                     && invoice.SponsorSuspension?.State == SponsorSuspensionState.Failed)
            {
                // A suspension that failed on an earlier run is retried until it succeeds.
                step = DelinquencyStep.SuspensionRetry;
                failure = RecordSuspension(invoice, actor, await SuspensionOutcomeAsync(invoice, outcomes, result));
            }
            else if (invoice.Status == InvoiceStatus.Sent || invoice.Status == InvoiceStatus.PartiallyPaid)
            {
                // Mark as overdue if past due date but within grace period
                step = DelinquencyStep.Overdue;
                invoice.Status = InvoiceStatus.Overdue;
                invoice.LastUpdatedBy = actor;
            }
            else
            {
                return;
            }

            try
            {
                await _invoiceRepository.UpdateAsync(invoice);
            }
            catch (ConcurrencyConflictException) when (attempt < InvoiceWrites.MaxAttempts)
            {
                invoice = await _invoiceRepository.GetByIdAsync(listed.Id);
                continue;
            }

            switch (step)
            {
                case DelinquencyStep.NewlyDelinquent:
                    result.DelinquentCount++;
                    _logger.LogWarning(
                        "Invoice {InvoiceNumber} for group {GroupNumber} marked delinquent. Balance: ${BalanceDue:N2}",
                        invoice.InvoiceNumber, invoice.GroupNumber, invoice.BalanceDue);
                    break;
                case DelinquencyStep.SuspensionRetry:
                    result.SuspensionRetries++;
                    break;
            }
            if (failure != null)
                result.SuspensionFailures.Add(failure);
            return;
        }
    }

    /// <summary>The same test as the overdue query: still owes money, past due, not closed.</summary>
    private static bool IsStillOverdue(PremiumInvoice invoice, DateTime now) =>
        invoice.BalanceDue > 0 && invoice.DueDate < now
        && invoice.Status is not (InvoiceStatus.Voided or InvoiceStatus.WriteOff or InvoiceStatus.Paid or InvoiceStatus.Draft);

    // --- Private helper methods ---

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }

    private async Task<PremiumInvoice> GenerateInvoiceForSponsorAsync(
        SponsorDto sponsor, string tenantId, DateTime billingPeriod, string billingRunId, string? executedBy)
    {
        var periodStart = billingPeriod;
        var periodEnd = periodStart.AddMonths(1).AddDays(-1);
        var daysInMonth = DateTime.DaysInMonth(periodStart.Year, periodStart.Month);

        // Fetch active coverage records for this sponsor
        // A coverage-service failure fails this sponsor's invoice (recorded as a
        // run warning); it never produces a $0 invoice.
        var coverages = await _coverageClient.GetActiveCoveragesByGroupAsync(tenantId, sponsor.GroupNumber);

        var invoice = new PremiumInvoice
        {
            InvoiceNumber = $"INV-{sponsor.GroupNumber}-{billingPeriod:yyyy-MM}",
            BillingRunId = billingRunId,
            GroupNumber = sponsor.GroupNumber,
            SponsorName = sponsor.EmployerName,
            BillingPeriodStart = periodStart,
            BillingPeriodEnd = periodEnd,
            GracePeriodDays = sponsor.GracePeriodDays,
            CreatedBy = executedBy
        };

        foreach (var coverage in coverages)
        {
            // Skip coverages not active during billing period
            if (coverage.EffectiveDate > periodEnd)
                continue;
            if (coverage.TerminationDate.HasValue && coverage.TerminationDate.Value < periodStart)
                continue;

            // Calculate proration for mid-month adds/terms
            var coverageStart = coverage.EffectiveDate > periodStart ? coverage.EffectiveDate : periodStart;
            var coverageEnd = coverage.TerminationDate.HasValue && coverage.TerminationDate.Value < periodEnd
                ? coverage.TerminationDate.Value
                : periodEnd;

            var coveredDays = (coverageEnd - coverageStart).Days + 1;
            var prorationFactor = coveredDays >= daysInMonth ? 1.0m : (decimal)coveredDays / daysInMonth;

            var subscriberPremium = (coverage.MonthlyPremium ?? 0) * prorationFactor;
            var employerContribution = (coverage.EmployerContribution ?? 0) * prorationFactor;

            var lineItem = new InvoiceLineItem
            {
                MemberId = coverage.MemberId,
                MemberName = coverage.MemberName ?? coverage.MemberId,
                CoverageId = coverage.CoverageId,
                PlanId = coverage.PlanId,
                CoverageLevel = coverage.CoverageLevel,
                InsuranceLineCode = coverage.InsuranceLineCode,
                SubscriberPremium = Math.Round(subscriberPremium, 2),
                EmployerContribution = Math.Round(employerContribution, 2),
                TotalPremium = Math.Round(subscriberPremium + employerContribution, 2),
                EffectiveDate = coverage.EffectiveDate,
                TerminationDate = coverage.TerminationDate,
                ProrationFactor = Math.Round(prorationFactor, 4),
                IsRetroactive = coverage.EffectiveDate < periodStart && coverage.EffectiveDate.Month != periodStart.Month,
                AdjustmentReason = prorationFactor < 1.0m
                    ? $"Prorated: {coveredDays}/{daysInMonth} days"
                    : null
            };

            invoice.LineItems.Add(lineItem);
        }

        // Set due date based on sponsor billing config
        var billingDay = Math.Min(sponsor.BillingDay, DateTime.DaysInMonth(periodStart.Year, periodStart.Month));
        invoice.DueDate = new DateTime(periodStart.Year, periodStart.Month, billingDay, 0, 0, 0, DateTimeKind.Utc);
        if (invoice.DueDate < DateTime.UtcNow)
            invoice.DueDate = DateTime.UtcNow.AddDays(30); // If billing day already passed, give 30 days

        invoice.GracePeriodExpires = invoice.DueDate.AddDays(invoice.GracePeriodDays);

        invoice.RecalculateTotals();

        return await _invoiceRepository.CreateAsync(invoice);
    }

    private async Task<List<SponsorDto>> FetchActiveSponsorsAsync(string tenantId, BillingRunCriteria criteria)
    {
        List<SponsorDto> sponsors;
        try
        {
            sponsors = await _sponsorClient.GetActiveSponsorsAsync(tenantId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch sponsors from sponsor-service");
            throw new InvalidOperationException($"Failed to fetch active sponsors: {ex.Message}", ex);
        }

        // Apply criteria filters
        if (criteria.GroupNumbers.Count > 0)
            sponsors = sponsors.Where(s => criteria.GroupNumbers.Contains(s.GroupNumber)).ToList();

        if (criteria.LineOfBusiness.HasValue)
            sponsors = sponsors.Where(s => s.LineOfBusiness == criteria.LineOfBusiness.Value).ToList();

        return sponsors;
    }

    /// <summary>
    /// Asks sponsor-service to suspend the invoice's sponsor, once per group
    /// per run (a re-evaluated invoice reuses the answer).
    /// </summary>
    private async Task<SponsorSuspensionOutcome> SuspensionOutcomeAsync(PremiumInvoice invoice,
        Dictionary<string, SponsorSuspensionOutcome> outcomes, DelinquencyRunResult result)
    {
        if (!outcomes.TryGetValue(invoice.GroupNumber, out var outcome))
        {
            outcome = await _sponsorClient.SuspendSponsorAsync(invoice.TenantId, invoice.GroupNumber,
                $"Premium delinquency: invoice {invoice.InvoiceNumber} unpaid past its grace period");
            outcomes[invoice.GroupNumber] = outcome;
            if (outcome.Success)
                result.SponsorsSuspended++;
        }
        return outcome;
    }

    /// <summary>
    /// Records the suspension outcome on the invoice. A failure (including
    /// 401/403) is logged as an error and returned for the run result; it is
    /// retried on the next run.
    /// </summary>
    private SponsorSuspensionFailure? RecordSuspension(PremiumInvoice invoice, string actor, SponsorSuspensionOutcome outcome)
    {
        var record = invoice.SponsorSuspension ?? new SponsorSuspensionRecord();
        record.Attempts++;
        record.LastAttemptAt = DateTime.UtcNow;
        record.LastAttemptBy = actor;
        record.LastStatusCode = outcome.StatusCode;
        invoice.SponsorSuspension = record;

        if (outcome.Success)
        {
            record.State = SponsorSuspensionState.Suspended;
            record.LastError = null;
            record.SuspendedAt = DateTime.UtcNow;
            _logger.LogWarning("Suspended sponsor {GroupNumber} due to premium delinquency (invoice {InvoiceNumber})",
                SanitizeForLog(invoice.GroupNumber), SanitizeForLog(invoice.InvoiceNumber));
            return null;
        }

        record.State = SponsorSuspensionState.Failed;
        record.LastError = outcome.Error is { Length: > 1000 } e ? e[..1000] : outcome.Error;
        _logger.LogError(
            "Failed to suspend sponsor {GroupNumber} for delinquent invoice {InvoiceNumber} (attempt {Attempt}, status {StatusCode}): {Error}",
            SanitizeForLog(invoice.GroupNumber), SanitizeForLog(invoice.InvoiceNumber), record.Attempts,
            outcome.StatusCode, SanitizeForLog(outcome.Error));
        return new SponsorSuspensionFailure
        {
            InvoiceId = invoice.Id,
            InvoiceNumber = invoice.InvoiceNumber,
            GroupNumber = invoice.GroupNumber,
            StatusCode = outcome.StatusCode,
            Error = record.LastError
        };
    }
}

/// <summary>What a delinquency run did.</summary>
public class DelinquencyRunResult
{
    /// <summary>Invoices newly marked delinquent by this run.</summary>
    public int DelinquentCount { get; set; }

    /// <summary>Sponsors sponsor-service confirmed as suspended in this run.</summary>
    public int SponsorsSuspended { get; set; }

    /// <summary>Earlier failed suspensions retried in this run.</summary>
    public int SuspensionRetries { get; set; }

    /// <summary>Suspensions that failed in this run; each is recorded on its invoice and retried next run.</summary>
    public List<SponsorSuspensionFailure> SuspensionFailures { get; set; } = new();

    /// <summary>Invoices that could not be processed in this run (the others were); picked up by the next run.</summary>
    public List<DelinquencyInvoiceFailure> InvoiceFailures { get; set; } = new();

    /// <summary>Invoices that a concurrent change (usually a payment) took out of overdue while this run was saving them.</summary>
    public int SkippedAfterConflict { get; set; }
}

public class DelinquencyInvoiceFailure
{
    public string InvoiceId { get; set; } = string.Empty;
    public string InvoiceNumber { get; set; } = string.Empty;
    public string GroupNumber { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}

public class SponsorSuspensionFailure
{
    public string InvoiceId { get; set; } = string.Empty;
    public string InvoiceNumber { get; set; } = string.Empty;
    public string GroupNumber { get; set; } = string.Empty;
    public int? StatusCode { get; set; }
    public string? Error { get; set; }
}
