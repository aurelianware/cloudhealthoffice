using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PremiumBillingService.Clients;
using PremiumBillingService.Models;
using PremiumBillingService.Rating;
using PremiumBillingService.Repositories;

namespace PremiumBillingService.Services;

public enum RatedInvoiceOutcomeKind
{
    /// <summary>A new invoice was created (issued, or Draft).</summary>
    Created,

    /// <summary>The period's Draft was computed again in place.</summary>
    Regenerated,

    /// <summary>The period already has an issued rated invoice; nothing changed.</summary>
    AlreadyIssued,

    /// <summary>The period already has an invoice priced the original way; nothing changed.</summary>
    UnratedInvoiceExists
}

public sealed record RatedInvoiceOutcome(RatedInvoiceOutcomeKind Kind, PremiumInvoice Invoice);

public interface IRatedInvoiceGenerator
{
    /// <summary>
    /// Creates or regenerates the group's rated invoice for the period.
    /// Idempotent: an issued invoice is never touched and never duplicated; a
    /// Draft is recomputed in place.
    /// </summary>
    Task<RatedInvoiceOutcome> GenerateAsync(SponsorDto sponsor, string tenantId, DateTime billingPeriod,
        string? billingRunId, string? actor, CancellationToken cancellationToken = default);

    /// <summary>Recomputes a Draft rated invoice (e.g. after a missing rate table was added).</summary>
    Task<RatedInvoiceOutcome> RegenerateDraftAsync(string invoiceId, string tenantId, string? actor, CancellationToken cancellationToken = default);

    /// <summary>Issues a Draft rated invoice that has no rating exceptions.</summary>
    Task<PremiumInvoice> IssueDraftAsync(string invoiceId, string? actor);
}

/// <summary>
/// Rated billing: prices a group's invoice with the premium rating engine
/// from the tenant's stored rate tables, coverage-service's coverage records
/// and member-service's rating census. See docs/architecture/premium-rated-billing.md.
/// </summary>
public sealed class RatedInvoiceGenerator : IRatedInvoiceGenerator
{
    private const int MaxAttempts = 5;

    /// <summary>Rating exception: nothing to bill (no coverage in the period, nothing to reconcile).</summary>
    public const string NoBillableCoverage = "NO_BILLABLE_COVERAGE";

    private readonly IPremiumInvoiceRepository _invoices;
    private readonly IRateTableRepository _rateTables;
    private readonly ICoverageServiceClient _coverage;
    private readonly IMemberServiceClient _members;
    private readonly RatedBillingOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<RatedInvoiceGenerator> _logger;

    public RatedInvoiceGenerator(
        IPremiumInvoiceRepository invoices,
        IRateTableRepository rateTables,
        ICoverageServiceClient coverage,
        IMemberServiceClient members,
        IOptions<RatedBillingOptions> options,
        TimeProvider clock,
        ILogger<RatedInvoiceGenerator> logger)
    {
        _invoices = invoices;
        _rateTables = rateTables;
        _coverage = coverage;
        _members = members;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// The id of a group's rated invoice for a period: the same on every run,
    /// so a second (or concurrent) generation finds it instead of creating a
    /// duplicate. <paramref name="issue"/> counts reissues after a void.
    /// </summary>
    public static string InvoiceId(string tenantId, string groupNumber, DateTime period, int issue)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId}\n{groupNumber}")))[..32].ToLowerInvariant();
        return $"inv-r-{key}-{period:yyyyMM}-{issue}";
    }

    public async Task<RatedInvoiceOutcome> GenerateAsync(SponsorDto sponsor, string tenantId, DateTime billingPeriod,
        string? billingRunId, string? actor, CancellationToken cancellationToken = default)
    {
        var settings = _options.For(tenantId);
        if (!settings.Enabled)
            throw new InvalidOperationException($"Rated billing is not enabled for tenant {tenantId}");
        var period = BilledLedger.MonthStart(billingPeriod);

        for (var attempt = 1; ; attempt++)
        {
            var groupInvoices = (await _invoices.GetByGroupNumberAsync(sponsor.GroupNumber)).ToList();
            var samePeriod = groupInvoices
                .Where(i => BilledLedger.MonthStart(i.BillingPeriodStart) == period && i.Status != InvoiceStatus.Voided)
                .ToList();

            var unrated = samePeriod.FirstOrDefault(i => i.PricingSource != PricingSource.RatingEngine);
            if (unrated != null)
                return new(RatedInvoiceOutcomeKind.UnratedInvoiceExists, unrated);
            var issued = samePeriod.FirstOrDefault(i => i.Status != InvoiceStatus.Draft);
            if (issued != null)
                return new(RatedInvoiceOutcomeKind.AlreadyIssued, issued);
            var draft = samePeriod.FirstOrDefault();

            var history = groupInvoices.Where(i => draft == null || i.Id != draft.Id).ToList();
            var invoice = draft ?? new PremiumInvoice
            {
                Id = InvoiceId(tenantId, sponsor.GroupNumber, period,
                    1 + groupInvoices.Count(i => BilledLedger.MonthStart(i.BillingPeriodStart) == period
                                                 && i.Status == InvoiceStatus.Voided
                                                 && i.PricingSource == PricingSource.RatingEngine)),
                InvoiceNumber = $"INV-{sponsor.GroupNumber}-{period:yyyy-MM}",
                CreatedBy = actor
            };

            await ComputeAsync(invoice, sponsor, tenantId, period, billingRunId, actor, settings, history, cancellationToken);

            try
            {
                if (draft == null)
                {
                    var created = await _invoices.CreateAsync(invoice);
                    Log(created, "created");
                    return new(RatedInvoiceOutcomeKind.Created, created);
                }
                var saved = await _invoices.UpdateAsync(invoice);
                Log(saved, "regenerated");
                return new(RatedInvoiceOutcomeKind.Regenerated, saved);
            }
            catch (Exception ex) when (ex is InvoiceAlreadyExistsException or ConcurrencyConflictException && attempt < MaxAttempts)
            {
                // Another run created or changed this invoice meanwhile: look again.
                _logger.LogInformation("Rated invoice for group {GroupNumber} {Period:yyyy-MM} changed concurrently; re-reading",
                    sponsor.GroupNumber, period);
            }
        }
    }

    public async Task<RatedInvoiceOutcome> RegenerateDraftAsync(string invoiceId, string tenantId, string? actor,
        CancellationToken cancellationToken = default)
    {
        var invoice = await _invoices.GetByIdAsync(invoiceId)
                      ?? throw new KeyNotFoundException($"Invoice {invoiceId} not found");
        RequireRatedDraft(invoice, "regenerated");
        var sponsor = new SponsorDto
        {
            GroupNumber = invoice.GroupNumber,
            EmployerName = invoice.SponsorName,
            BillingDay = invoice.BillingDay ?? 1,
            GracePeriodDays = invoice.GracePeriodDays
        };
        return await GenerateAsync(sponsor, tenantId, invoice.BillingPeriodStart, invoice.BillingRunId, actor, cancellationToken);
    }

    public async Task<PremiumInvoice> IssueDraftAsync(string invoiceId, string? actor)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        return await InvoiceWrites.UpdateWithRetryAsync(_invoices, invoiceId, null, invoice =>
        {
            RequireRatedDraft(invoice, "issued");
            if (invoice.RatingExceptions.Count > 0)
                throw new InvalidOperationException(
                    $"Invoice {invoice.InvoiceNumber} has {invoice.RatingExceptions.Count} rating exception(s); resolve them and regenerate it first");
            if (invoice.LineItems.Count == 0 && invoice.Adjustments.Count == 0)
                throw new InvalidOperationException($"Invoice {invoice.InvoiceNumber} has nothing to bill");
            Issue(invoice, now);
            invoice.LastUpdatedBy = actor;
            return true;
        }) ?? throw new KeyNotFoundException($"Invoice {invoiceId} not found");
    }

    private static void RequireRatedDraft(PremiumInvoice invoice, string action)
    {
        if (invoice.PricingSource != PricingSource.RatingEngine)
            throw new InvalidOperationException($"Invoice {invoice.InvoiceNumber} was not rated; only rated drafts can be {action}");
        if (invoice.Status != InvoiceStatus.Draft)
            throw new InvalidOperationException(
                $"Invoice {invoice.InvoiceNumber} is {invoice.Status}; issued invoices are never changed (corrections go on the next invoice)");
    }

    private async Task ComputeAsync(PremiumInvoice invoice, SponsorDto sponsor, string tenantId, DateTime period,
        string? billingRunId, string? actor, RatedBillingTenantOptions settings, List<PremiumInvoice> history,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var periodEnd = period.AddMonths(1).AddDays(-1);

        // Any failure here fails this group's invoice (a run warning), never a $0 or partial issued invoice.
        var coverages = await _coverage.GetAllCoveragesByGroupAsync(tenantId, sponsor.GroupNumber, cancellationToken);
        var census = await _members.GetRatingCensusAsync(tenantId, sponsor.GroupNumber, cancellationToken);
        var catalog = RateTableCatalog.Tolerant(
            RateTableRecord.Current(await _rateTables.ListVersionsAsync()).Select(r => r.ToRatingTable()));

        var build = RatingEnrollmentBuilder.Build(coverages, census, period.AddMonths(-settings.MaxRetroMonths), periodEnd);
        var calculation = new PremiumInvoiceCalculator(catalog).Calculate(new InvoiceCalculationRequest
        {
            BillingPeriodStart = period,
            BillingDate = now,
            Enrollments = build.Enrollments,
            // Only rated invoices are reconciled: an unrated one keyed its lines per member coverage.
            PriorBilling = BilledLedger.FromInvoices(history.Where(i => i.PricingSource == PricingSource.RatingEngine)),
            MaxRetroMonths = settings.MaxRetroMonths,
            EmployerContributionPercent = settings.EmployerContributionPercentFor(sponsor.GroupNumber),
            Unreconcilable = build.ExcludedCoverageIds
        });

        var format = settings.BillFormatFor(sponsor.GroupNumber);
        var lines = format == BillFormat.Composite ? CompositeBill.Collapse(calculation.LineItems) : calculation.LineItems;

        var exceptions = build.Issues.Select(i => new InvoiceRatingException
            {
                Code = InvoiceCalculationIssue.EnrollmentData,
                CoverageId = i.CoverageId,
                MemberId = i.MemberId,
                Message = Truncate(i.Message)
            })
            .Concat(calculation.Issues.Select(i => new InvoiceRatingException
            {
                Code = i.Code,
                CoverageId = i.CoverageId,
                ServiceMonth = i.Month,
                Message = Truncate(i.Message)
            }))
            .ToList();
        if (lines.Count == 0 && calculation.Adjustments.Count == 0 && exceptions.Count == 0)
            exceptions.Add(new InvoiceRatingException
            {
                Code = NoBillableCoverage,
                ServiceMonth = period,
                Message = $"No coverage of group {sponsor.GroupNumber} is billable for {period:yyyy-MM}"
            });

        invoice.TenantId = tenantId;
        invoice.BillingRunId = billingRunId ?? invoice.BillingRunId;
        invoice.GroupNumber = sponsor.GroupNumber;
        invoice.SponsorName = sponsor.EmployerName;
        invoice.BillingPeriodStart = period;
        invoice.BillingPeriodEnd = periodEnd;
        invoice.GracePeriodDays = sponsor.GracePeriodDays;
        invoice.BillingDay = sponsor.BillingDay;
        invoice.PricingSource = PricingSource.RatingEngine;
        invoice.BillFormat = format;
        invoice.LineItems = lines;
        invoice.Adjustments = calculation.Adjustments;
        invoice.RatingExceptions = exceptions;
        invoice.RatedAt = now;
        invoice.RatingRevision++;
        invoice.LastUpdatedBy = actor;
        invoice.RecalculateTotals();
        CheckMoney(invoice, calculation);

        if (exceptions.Count == 0 && !settings.HoldForReview)
        {
            Issue(invoice, now);
        }
        else
        {
            invoice.Status = InvoiceStatus.Draft;
            invoice.IssuedAt = null;
            SetDueDate(invoice, now);
        }
    }

    private static void Issue(PremiumInvoice invoice, DateTime now)
    {
        invoice.Status = InvoiceStatus.Generated;
        invoice.IssuedAt = now;
        SetDueDate(invoice, now);
    }

    /// <summary>Same rule as unrated invoices: the sponsor's billing day of the period, or 30 days out if it has passed.</summary>
    private static void SetDueDate(PremiumInvoice invoice, DateTime now)
    {
        var period = invoice.BillingPeriodStart;
        var billingDay = Math.Min(Math.Max(invoice.BillingDay ?? 1, 1), DateTime.DaysInMonth(period.Year, period.Month));
        invoice.DueDate = new DateTime(period.Year, period.Month, billingDay, 0, 0, 0, DateTimeKind.Utc);
        if (invoice.DueDate < now)
            invoice.DueDate = now.AddDays(30);
        invoice.GracePeriodExpires = invoice.DueDate.AddDays(invoice.GracePeriodDays);
    }

    /// <summary>Every amount is whole cents and the invoice total is exactly the sum of its lines.</summary>
    private static void CheckMoney(PremiumInvoice invoice, InvoiceCalculation calculation)
    {
        static bool Cents(decimal amount) => decimal.Round(amount, 2) == amount;
        var amounts = invoice.LineItems.SelectMany(l => new[] { l.TotalPremium, l.EmployerContribution, l.SubscriberPremium })
            .Concat(invoice.Adjustments.Select(a => a.Amount));
        if (!amounts.All(Cents))
            throw new InvalidOperationException($"Invoice {invoice.InvoiceNumber} has an amount that is not whole cents");
        if (invoice.SubtotalPremium != calculation.Subtotal
            || invoice.TotalAmount != invoice.LineItems.Sum(l => l.TotalPremium) + invoice.Adjustments.Sum(a => a.Amount)
            || invoice.LineItems.Any(l => l.TotalPremium != l.EmployerContribution + l.SubscriberPremium)
            || invoice.LineItems.Any(l => l.Components is { Count: > 0 } c && c.Sum(x => x.Amount) != l.TotalPremium))
            throw new InvalidOperationException($"Invoice {invoice.InvoiceNumber} does not add up");
    }

    private void Log(PremiumInvoice invoice, string what)
    {
        if (invoice.Status == InvoiceStatus.Draft)
            _logger.LogWarning("Rated invoice {InvoiceNumber} {What} as Draft with {Count} rating exception(s)",
                invoice.InvoiceNumber, what, invoice.RatingExceptions.Count);
        else
            _logger.LogInformation("Rated invoice {InvoiceNumber} {What}: {Lines} lines, {Adjustments} adjustments, total {Total:N2}",
                invoice.InvoiceNumber, what, invoice.LineItems.Count, invoice.Adjustments.Count, invoice.TotalAmount);
    }

    private static string Truncate(string message) => message.Length > 1000 ? message[..1000] : message;
}
