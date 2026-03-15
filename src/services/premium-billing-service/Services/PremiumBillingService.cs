using System.Text.Json;
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
    Task<int> ProcessDelinquenciesAsync();
}

public class PremiumBillingService : IPremiumBillingService
{
    private readonly IBillingRunRepository _billingRunRepository;
    private readonly IPremiumInvoiceRepository _invoiceRepository;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PremiumBillingService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public PremiumBillingService(
        IBillingRunRepository billingRunRepository,
        IPremiumInvoiceRepository invoiceRepository,
        IHttpClientFactory httpClientFactory,
        ILogger<PremiumBillingService> logger)
    {
        _billingRunRepository = billingRunRepository;
        _invoiceRepository = invoiceRepository;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

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
        await _billingRunRepository.UpdateAsync(billingRun);

        try
        {
            // Fetch active sponsors
            var sponsors = await FetchActiveSponsorsAsync(billingRun.Criteria);
            _logger.LogInformation("Found {Count} active sponsors for billing run {BillingRunNumber}",
                sponsors.Count, billingRun.BillingRunNumber);

            decimal totalPremium = 0;
            decimal totalAdjustments = 0;
            int totalMembers = 0;

            foreach (var sponsor in sponsors)
            {
                try
                {
                    var invoice = await GenerateInvoiceForSponsorAsync(
                        sponsor, billingRun.BillingPeriod, billingRun.Id);

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
        await _billingRunRepository.UpdateAsync(billingRun);
    }

    public async Task<PremiumInvoice> RecordPaymentAsync(string invoiceId, RecordPaymentRequest request)
    {
        var invoice = await _invoiceRepository.GetByIdAsync(invoiceId)
            ?? throw new InvalidOperationException($"Invoice {invoiceId} not found");

        if (invoice.Status == InvoiceStatus.Voided || invoice.Status == InvoiceStatus.WriteOff)
            throw new InvalidOperationException($"Cannot record payment on {invoice.Status} invoice");

        var payment = new InvoicePayment
        {
            Amount = request.Amount,
            PaymentDate = request.PaymentDate,
            PaymentMethod = request.PaymentMethod,
            ReferenceNumber = request.ReferenceNumber,
            ReceivedDate = DateTime.UtcNow
        };

        invoice.Payments.Add(payment);
        invoice.RecalculateTotals();

        // Update status based on balance
        if (invoice.BalanceDue <= 0)
            invoice.Status = InvoiceStatus.Paid;
        else if (invoice.TotalPaid > 0)
            invoice.Status = InvoiceStatus.PartiallyPaid;

        _logger.LogInformation("Recorded payment of ${Amount:N2} on invoice {InvoiceNumber}, balance due: ${BalanceDue:N2}",
            request.Amount, invoice.InvoiceNumber, invoice.BalanceDue);

        return await _invoiceRepository.UpdateAsync(invoice);
    }

    public async Task<PremiumInvoice> VoidInvoiceAsync(string invoiceId, string reason)
    {
        var invoice = await _invoiceRepository.GetByIdAsync(invoiceId)
            ?? throw new InvalidOperationException($"Invoice {invoiceId} not found");

        if (invoice.Status == InvoiceStatus.Paid)
            throw new InvalidOperationException("Cannot void a fully paid invoice");

        invoice.Status = InvoiceStatus.Voided;
        invoice.Adjustments.Add(new InvoiceAdjustment
        {
            Type = AdjustmentType.Other,
            Description = $"Invoice voided: {reason}",
            Amount = 0,
            AdjustmentDate = DateTime.UtcNow
        });

        _logger.LogInformation("Voided invoice {InvoiceNumber}: {Reason}", invoice.InvoiceNumber, SanitizeForLog(reason));

        return await _invoiceRepository.UpdateAsync(invoice);
    }

    public async Task<PremiumInvoice> MarkInvoiceSentAsync(string invoiceId)
    {
        var invoice = await _invoiceRepository.GetByIdAsync(invoiceId)
            ?? throw new InvalidOperationException($"Invoice {invoiceId} not found");

        if (invoice.Status != InvoiceStatus.Generated)
            throw new InvalidOperationException($"Can only mark Generated invoices as Sent, current: {invoice.Status}");

        invoice.Status = InvoiceStatus.Sent;
        return await _invoiceRepository.UpdateAsync(invoice);
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

    public async Task<int> ProcessDelinquenciesAsync()
    {
        var overdueInvoices = (await _invoiceRepository.GetOverdueAsync()).ToList();
        var now = DateTime.UtcNow;
        int delinquentCount = 0;

        foreach (var invoice in overdueInvoices)
        {
            // Check if grace period has expired
            if (invoice.GracePeriodExpires.HasValue && now > invoice.GracePeriodExpires.Value
                && invoice.Status != InvoiceStatus.Delinquent)
            {
                invoice.Status = InvoiceStatus.Delinquent;
                await _invoiceRepository.UpdateAsync(invoice);
                delinquentCount++;

                _logger.LogWarning(
                    "Invoice {InvoiceNumber} for group {GroupNumber} marked delinquent. Balance: ${BalanceDue:N2}",
                    invoice.InvoiceNumber, invoice.GroupNumber, invoice.BalanceDue);

                // Attempt to suspend sponsor via sponsor-service
                await TrySuspendSponsorAsync(invoice.GroupNumber);
            }
            else if (invoice.Status == InvoiceStatus.Sent || invoice.Status == InvoiceStatus.PartiallyPaid)
            {
                // Mark as overdue if past due date but within grace period
                invoice.Status = InvoiceStatus.Overdue;
                await _invoiceRepository.UpdateAsync(invoice);
            }
        }

        _logger.LogInformation("Delinquency processing complete: {Count} invoices marked delinquent", delinquentCount);
        return delinquentCount;
    }

    // --- Private helper methods ---

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }

    private async Task<PremiumInvoice> GenerateInvoiceForSponsorAsync(
        SponsorDto sponsor, DateTime billingPeriod, string billingRunId)
    {
        var periodStart = billingPeriod;
        var periodEnd = periodStart.AddMonths(1).AddDays(-1);
        var daysInMonth = DateTime.DaysInMonth(periodStart.Year, periodStart.Month);

        // Fetch active coverage records for this sponsor
        var coverages = await FetchCoveragesByGroupAsync(sponsor.GroupNumber);

        var invoice = new PremiumInvoice
        {
            InvoiceNumber = $"INV-{sponsor.GroupNumber}-{billingPeriod:yyyy-MM}",
            BillingRunId = billingRunId,
            GroupNumber = sponsor.GroupNumber,
            SponsorName = sponsor.EmployerName,
            BillingPeriodStart = periodStart,
            BillingPeriodEnd = periodEnd,
            GracePeriodDays = sponsor.GracePeriodDays,
            CreatedBy = "billing-run"
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

    private async Task<List<SponsorDto>> FetchActiveSponsorsAsync(BillingRunCriteria criteria)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("SponsorService");
            var response = await client.GetAsync("/api/v1/sponsors?status=Active");
            response.EnsureSuccessStatusCode();

            var sponsors = await response.Content.ReadFromJsonAsync<List<SponsorDto>>(JsonOptions) ?? new();

            // Apply criteria filters
            if (criteria.GroupNumbers.Count > 0)
                sponsors = sponsors.Where(s => criteria.GroupNumbers.Contains(s.GroupNumber)).ToList();

            if (criteria.LineOfBusiness.HasValue)
                sponsors = sponsors.Where(s => s.LineOfBusiness == (int)criteria.LineOfBusiness.Value).ToList();

            return sponsors;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch sponsors from sponsor-service");
            throw new InvalidOperationException("Failed to fetch active sponsors", ex);
        }
    }

    private async Task<List<CoverageDto>> FetchCoveragesByGroupAsync(string groupNumber)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("CoverageService");
            var response = await client.GetAsync($"/api/v1/coverages?groupNumber={groupNumber}&status=Active");
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync<List<CoverageDto>>(JsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch coverages for group {GroupNumber}", groupNumber);
            return new List<CoverageDto>();
        }
    }

    private async Task TrySuspendSponsorAsync(string groupNumber)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("SponsorService");
            var response = await client.PutAsJsonAsync(
                $"/api/v1/sponsors/{groupNumber}",
                new { Status = "Suspended" });

            if (response.IsSuccessStatusCode)
                _logger.LogWarning("Suspended sponsor {GroupNumber} due to premium delinquency", groupNumber);
            else
                _logger.LogWarning("Failed to suspend sponsor {GroupNumber}: {StatusCode}", groupNumber, response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error suspending sponsor {GroupNumber}", groupNumber);
        }
    }
}

/// <summary>
/// DTO for sponsor data fetched from sponsor-service
/// </summary>
public class SponsorDto
{
    public string GroupNumber { get; set; } = string.Empty;
    public string EmployerName { get; set; } = string.Empty;
    public int LineOfBusiness { get; set; }
    public int BillingDay { get; set; } = 1;
    public int GracePeriodDays { get; set; } = 30;
    public string? PaymentMethod { get; set; }
    public SponsorBankAccountDto? BankAccount { get; set; }
}

/// <summary>
/// Bank account info from sponsor-service for EFT/ACH drafts
/// </summary>
public class SponsorBankAccountDto
{
    public bool EftEnabled { get; set; }
    public string? PreferredEftMethod { get; set; }
    public string? RoutingNumber { get; set; }
    public string? AccountNumber { get; set; }
    public string? AccountType { get; set; }
    public string? AccountHolderName { get; set; }
    public string? StripeCustomerId { get; set; }
    public string? StripePaymentMethodId { get; set; }
    public string? RoutingNumberLast4 { get; set; }
    public string? AccountNumberLast4 { get; set; }
}

/// <summary>
/// DTO for coverage data fetched from coverage-service
/// </summary>
public class CoverageDto
{
    public string CoverageId { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string? MemberName { get; set; }
    public string GroupNumber { get; set; } = string.Empty;
    public string? PlanId { get; set; }
    public string? CoverageLevel { get; set; }
    public string? InsuranceLineCode { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public decimal? MonthlyPremium { get; set; }
    public decimal? EmployerContribution { get; set; }
}
