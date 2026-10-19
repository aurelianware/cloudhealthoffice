using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Money;
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
    UnratedInvoiceExists,

    /// <summary>
    /// An earlier period inside the retro window is still a Draft; the later
    /// period is not billed until it is issued or voided (the outcome's invoice
    /// is that Draft). Otherwise both could charge the same retro change.
    /// </summary>
    BlockedByEarlierDraft
}

public sealed record RatedInvoiceOutcome(RatedInvoiceOutcomeKind Kind, PremiumInvoice Invoice);

/// <summary>Another generation, regeneration or issue of the same group holds its billing lease.</summary>
public sealed class GroupBillingBusyException : InvalidOperationException
{
    public GroupBillingBusyException(string groupNumber)
        : base($"Rated billing of group {groupNumber} is busy (another run holds its lease); try again shortly")
    {
    }
}

/// <summary>A Draft no longer matches what billing computes now; it was regenerated and must be reviewed again.</summary>
public sealed class DraftOutOfDateException : InvalidOperationException
{
    public DraftOutOfDateException(string invoiceNumber)
        : base($"Invoice {invoiceNumber} was out of date (coverage, rates or the group's issued invoices changed since it was computed); " +
               "it has been regenerated: review it and issue it again")
    {
    }
}

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

    /// <summary>
    /// Issues a Draft rated invoice: recomputes it, and issues it only when it
    /// has no rating exceptions and is exactly what was computed (and reviewed)
    /// before, against the same issued invoices. Otherwise the regenerated Draft
    /// is saved and <see cref="DraftOutOfDateException"/> is thrown.
    /// </summary>
    Task<PremiumInvoice> IssueDraftAsync(string invoiceId, string tenantId, string? actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs another writer of a rated invoice (a void) under the group's billing
    /// lease, so it cannot land in the middle of a generation or issue.
    /// </summary>
    Task<T> RunUnderGroupLeaseAsync<T>(string groupNumber, Func<Task<T>> action, CancellationToken cancellationToken = default);
}

/// <summary>
/// Rated billing: prices a group's invoice with the premium rating engine
/// from the tenant's stored rate tables, coverage-service's coverage records
/// and member-service's rating census. Every read-compute-write of a group runs
/// under the group's billing lease. See docs/architecture/premium-rated-billing.md.
/// </summary>
public sealed class RatedInvoiceGenerator : IRatedInvoiceGenerator
{
    private const int MaxAttempts = 5;

    /// <summary>Rating exception: nothing to bill (no coverage in the period, nothing to reconcile).</summary>
    public const string NoBillableCoverage = "NO_BILLABLE_COVERAGE";

    /// <summary>Rating exception: a coverage billed in the retro window is no longer listed by coverage-service.</summary>
    public const string CoverageMissing = "COVERAGE_MISSING";

    private readonly IPremiumInvoiceRepository _invoices;
    private readonly IRateTableRepository _rateTables;
    private readonly ICoverageServiceClient _coverage;
    private readonly IMemberServiceClient _members;
    private readonly IBillingLeaseRepository _leases;
    private readonly ISponsorAccountRepository? _accounts;
    private readonly RatedBillingOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<RatedInvoiceGenerator> _logger;

    public RatedInvoiceGenerator(
        IPremiumInvoiceRepository invoices,
        IRateTableRepository rateTables,
        ICoverageServiceClient coverage,
        IMemberServiceClient members,
        IBillingLeaseRepository leases,
        IOptions<RatedBillingOptions> options,
        TimeProvider clock,
        ILogger<RatedInvoiceGenerator> logger,
        ISponsorAccountRepository? accounts = null)
    {
        _invoices = invoices;
        _rateTables = rateTables;
        _coverage = coverage;
        _members = members;
        _leases = leases;
        _accounts = accounts;
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

    public Task<RatedInvoiceOutcome> GenerateAsync(SponsorDto sponsor, string tenantId, DateTime billingPeriod,
        string? billingRunId, string? actor, CancellationToken cancellationToken = default)
    {
        var settings = Settings(tenantId);
        return WithGroupLeaseAsync(sponsor.GroupNumber,
            lease => GenerateLockedAsync(sponsor, tenantId, BilledLedger.MonthStart(billingPeriod), billingRunId, actor, settings, lease, cancellationToken),
            cancellationToken);
    }

    public async Task<RatedInvoiceOutcome> RegenerateDraftAsync(string invoiceId, string tenantId, string? actor,
        CancellationToken cancellationToken = default)
    {
        var invoice = await _invoices.GetByIdAsync(invoiceId)
                      ?? throw new KeyNotFoundException($"Invoice {invoiceId} not found");
        RequireRatedDraft(invoice, "regenerated");
        return await GenerateAsync(SponsorOf(invoice), tenantId, invoice.BillingPeriodStart, invoice.BillingRunId, actor, cancellationToken);
    }

    public async Task<PremiumInvoice> IssueDraftAsync(string invoiceId, string tenantId, string? actor, CancellationToken cancellationToken = default)
    {
        var settings = Settings(tenantId);
        var first = await _invoices.GetByIdAsync(invoiceId) ?? throw new KeyNotFoundException($"Invoice {invoiceId} not found");
        RequireRatedDraft(first, "issued");

        return await WithGroupLeaseAsync(first.GroupNumber, async lease =>
        {
            // Re-read under the lease: what we hold now is what nobody else is changing.
            var invoice = await _invoices.GetByIdAsync(invoiceId) ?? throw new KeyNotFoundException($"Invoice {invoiceId} not found");
            RequireRatedDraft(invoice, "issued");
            var period = BilledLedger.MonthStart(invoice.BillingPeriodStart);
            var groupInvoices = (await _invoices.GetByGroupNumberAsync(invoice.GroupNumber)).ToList();
            var earlier = EarlierDraft(groupInvoices, period, settings, invoice.Id);
            if (earlier != null)
                throw new InvalidOperationException(
                    $"Invoice {earlier.InvoiceNumber} for {earlier.BillingPeriodStart:yyyy-MM} is still a Draft; issue or void it before {invoice.InvoiceNumber}");

            var reviewedFingerprint = invoice.RatingFingerprint;
            var reviewedBasis = invoice.ReconciledInvoiceIds.ToList();
            var state = await ComputeAsync(invoice, SponsorOf(invoice), tenantId, period, invoice.BillingRunId, actor, settings,
                groupInvoices.Where(i => i.Id != invoice.Id).ToList(), lease, cancellationToken);

            if (!state.Clean)
            {
                await UpdateFencedAsync(invoice, lease);
                throw new InvalidOperationException(
                    $"Invoice {invoice.InvoiceNumber} has {invoice.RatingExceptions.Count} rating exception(s); resolve them and regenerate it first");
            }
            if (reviewedFingerprint != invoice.RatingFingerprint || !reviewedBasis.SequenceEqual(invoice.ReconciledInvoiceIds))
            {
                await UpdateFencedAsync(invoice, lease);
                throw new DraftOutOfDateException(invoice.InvoiceNumber);
            }

            Issue(invoice);
            invoice.LastUpdatedBy = actor;
            var saved = await UpdateFencedAsync(invoice, lease);
            await EnsureCreditMovedAsync(saved);
            Log(saved, "issued");
            return saved;
        }, cancellationToken);
    }

    public Task<T> RunUnderGroupLeaseAsync<T>(string groupNumber, Func<Task<T>> action, CancellationToken cancellationToken = default) =>
        WithGroupLeaseAsync(groupNumber, _ => action(), cancellationToken);

    private TimeSpan LeaseDuration => TimeSpan.FromSeconds(_options.GroupLeaseSeconds);

    /// <summary>Extends the lease between steps; a holder whose lease was taken over stops before it writes anything.</summary>
    private async Task HeartbeatAsync(BillingLeaseHandle lease)
    {
        if (!await _leases.RenewAsync(lease, _clock.GetUtcNow().UtcDateTime, LeaseDuration))
            throw new BillingLeaseLostException(lease.GroupNumber);
    }

    private Task<PremiumInvoice> CreateFencedAsync(PremiumInvoice invoice, BillingLeaseHandle lease) =>
        _invoices.CreateFencedAsync(invoice, _leases, lease, _clock.GetUtcNow().UtcDateTime, LeaseDuration);

    private Task<PremiumInvoice> UpdateFencedAsync(PremiumInvoice invoice, BillingLeaseHandle lease) =>
        _invoices.UpdateFencedAsync(invoice, _leases, lease, _clock.GetUtcNow().UtcDateTime, LeaseDuration);

    private async Task<RatedInvoiceOutcome> GenerateLockedAsync(SponsorDto sponsor, string tenantId, DateTime period,
        string? billingRunId, string? actor, RatedBillingTenantOptions settings, BillingLeaseHandle lease, CancellationToken cancellationToken)
    {
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
            {
                // Self-heal: a net credit whose account entry was not posted (a crash between the save and the post).
                await EnsureCreditMovedAsync(issued);
                return new(RatedInvoiceOutcomeKind.AlreadyIssued, issued);
            }
            var draft = samePeriod.FirstOrDefault();
            var earlier = EarlierDraft(groupInvoices, period, settings, draft?.Id);
            if (earlier != null)
                return new(RatedInvoiceOutcomeKind.BlockedByEarlierDraft, earlier);

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

            var state = await ComputeAsync(invoice, sponsor, tenantId, period, billingRunId, actor, settings, history, lease, cancellationToken);
            if (state.Clean && !state.NeedsReview)
                Issue(invoice);

            try
            {
                // The invoice is saved first (fenced by the lease); a net credit is moved to the
                // account only once its invoice is saved, keyed by that invoice.
                if (draft == null)
                {
                    var created = await CreateFencedAsync(invoice, lease);
                    await EnsureCreditMovedAsync(created);
                    Log(created, "created");
                    return new(RatedInvoiceOutcomeKind.Created, created);
                }
                var saved = await UpdateFencedAsync(invoice, lease);
                await EnsureCreditMovedAsync(saved);
                Log(saved, "regenerated");
                return new(RatedInvoiceOutcomeKind.Regenerated, saved);
            }
            catch (Exception ex) when (ex is InvoiceAlreadyExistsException or ConcurrencyConflictException && attempt < MaxAttempts)
            {
                // Someone changed this invoice although we hold the lease (a writer that takes
                // none): look again. A lost lease is not retried (BillingLeaseLostException).
                _logger.LogInformation("Rated invoice for group {GroupNumber} {Period:yyyy-MM} changed concurrently; re-reading",
                    sponsor.GroupNumber, period);
            }
        }
    }

    /// <summary>The oldest rated Draft of an earlier period inside the retro window, other than <paramref name="except"/>.</summary>
    private static PremiumInvoice? EarlierDraft(IEnumerable<PremiumInvoice> groupInvoices, DateTime period,
        RatedBillingTenantOptions settings, string? except) =>
        groupInvoices
            .Where(i => i.Id != except && i.Status == InvoiceStatus.Draft && i.PricingSource == PricingSource.RatingEngine)
            .Where(i => BilledLedger.MonthStart(i.BillingPeriodStart) < period
                        && BilledLedger.MonthStart(i.BillingPeriodStart) >= period.AddMonths(-settings.MaxRetroMonths))
            .OrderBy(i => i.BillingPeriodStart)
            .FirstOrDefault();

    private RatedBillingTenantOptions Settings(string tenantId)
    {
        var settings = _options.For(tenantId);
        if (!settings.Enabled)
            throw new InvalidOperationException($"Rated billing is not enabled for tenant {tenantId}");
        return settings;
    }

    private async Task<T> WithGroupLeaseAsync<T>(string groupNumber, Func<BillingLeaseHandle, Task<T>> action, CancellationToken cancellationToken)
    {
        var owner = Guid.NewGuid().ToString("N");
        var waited = Stopwatch.StartNew();
        BillingLeaseHandle? lease;
        while ((lease = await _leases.TryAcquireAsync(groupNumber, owner, _clock.GetUtcNow().UtcDateTime, LeaseDuration)) == null)
        {
            if (waited.Elapsed > TimeSpan.FromSeconds(_options.GroupLeaseWaitSeconds))
                throw new GroupBillingBusyException(groupNumber);
            await Task.Delay(100, cancellationToken);
        }
        try
        {
            return await action(lease);
        }
        finally
        {
            await _leases.ReleaseAsync(lease);
        }
    }

    private static SponsorDto SponsorOf(PremiumInvoice invoice) => new()
    {
        GroupNumber = invoice.GroupNumber,
        EmployerName = invoice.SponsorName,
        BillingDay = invoice.BillingDay ?? 1,
        GracePeriodDays = invoice.GracePeriodDays
    };

    private static void RequireRatedDraft(PremiumInvoice invoice, string action)
    {
        if (invoice.PricingSource != PricingSource.RatingEngine)
            throw new InvalidOperationException($"Invoice {invoice.InvoiceNumber} was not rated; only rated drafts can be {action}");
        if (invoice.Status != InvoiceStatus.Draft)
            throw new InvalidOperationException(
                $"Invoice {invoice.InvoiceNumber} is {invoice.Status}; issued invoices are never changed (corrections go on the next invoice)");
    }

    private readonly record struct ComputeState(bool Clean, bool NeedsReview);

    /// <summary>Recomputes the invoice's content in place and leaves it a Draft; the caller decides whether to issue.</summary>
    private async Task<ComputeState> ComputeAsync(PremiumInvoice invoice, SponsorDto sponsor, string tenantId, DateTime period,
        string? billingRunId, string? actor, RatedBillingTenantOptions settings, List<PremiumInvoice> history,
        BillingLeaseHandle lease, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var periodEnd = period.AddMonths(1).AddDays(-1);

        // Any failure here fails this group's invoice (a run warning), never a $0 or partial issued invoice.
        var coverages = await _coverage.GetAllCoveragesByGroupAsync(tenantId, sponsor.GroupNumber, cancellationToken);
        await HeartbeatAsync(lease);
        var census = await _members.GetRatingCensusAsync(tenantId, sponsor.GroupNumber, cancellationToken);
        await HeartbeatAsync(lease);
        var catalog = RateTableCatalog.Tolerant(
            RateTableRecord.Current(await _rateTables.ListVersionsAsync()).Select(r => r.ToRatingTable()));

        var build = RatingEnrollmentBuilder.Build(coverages, census, period.AddMonths(-settings.MaxRetroMonths), periodEnd);
        // Only rated invoices are reconciled: an unrated one keyed its lines per member coverage.
        var ledger = BilledLedger.FromInvoices(history.Where(i => i.PricingSource == PricingSource.RatingEngine));

        // A coverage billed in the window that coverage-service no longer lists at all (not terminated:
        // gone) would otherwise be credited back in full. Hold it for a person instead.
        var listed = coverages.Select(c => c.CoverageId).ToHashSet(StringComparer.Ordinal);
        var vanished = new SortedDictionary<string, List<DateTime>>(StringComparer.Ordinal);
        for (var back = settings.MaxRetroMonths; back >= 1; back--)
        {
            var month = period.AddMonths(-back);
            if (!ledger.WasInvoiced(month))
                continue;
            foreach (var key in ledger.CoveragesBilledIn(month))
            {
                if (key.StartsWith("member:", StringComparison.Ordinal) || listed.Contains(key) || ledger.Billed(key, month) == 0m)
                    continue;
                if (!vanished.TryGetValue(key, out var months))
                    vanished[key] = months = new List<DateTime>();
                months.Add(month);
            }
        }

        var unreconcilable = new HashSet<string>(build.ExcludedCoverageIds, StringComparer.Ordinal);
        unreconcilable.UnionWith(vanished.Keys);
        var calculation = new PremiumInvoiceCalculator(catalog).Calculate(new InvoiceCalculationRequest
        {
            BillingPeriodStart = period,
            BillingDate = now,
            Enrollments = build.Enrollments,
            PriorBilling = ledger,
            MaxRetroMonths = settings.MaxRetroMonths,
            EmployerContributionPercent = settings.EmployerContributionPercentFor(sponsor.GroupNumber),
            Unreconcilable = unreconcilable
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
            .Concat(vanished.Select(v => new InvoiceRatingException
            {
                Code = CoverageMissing,
                CoverageId = v.Key,
                ServiceMonth = v.Value[0],
                Message = Truncate($"Coverage {v.Key} was billed for {string.Join(", ", v.Value.Select(m => m.ToString("yyyy-MM")))} " +
                                   "but coverage-service no longer lists it; terminate it with a date instead of deleting it, " +
                                   "or credit it by hand")
            }))
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
        invoice.Payments = new List<InvoicePayment>();
        invoice.RecalculateTotals();
        CheckMoney(invoice, calculation);

        var reasons = new List<string>();
        if (settings.HoldForReview)
            reasons.Add("HELD_FOR_REVIEW: the tenant issues rated invoices after review");
        var corrections = calculation.Adjustments.Where(a => a.IsRateCorrection && Math.Abs(a.Amount) > settings.RateCorrectionReviewThreshold).ToList();
        if (corrections.Count > 0)
            reasons.Add($"RATE_CORRECTION: {corrections.Count} adjustment(s) totalling {corrections.Sum(a => a.Amount):0.00} re-price invoiced months " +
                        "under a different rate table version or plan than they were billed with");
        if (invoice.TotalAmount < 0 && _accounts == null)
            reasons.Add("NET_CREDIT: the invoice is a net credit and there is no sponsor account store to move it to");
        invoice.ReviewReasons = reasons;
        invoice.ReconciledInvoiceIds = ledger.SourceInvoiceIds.OrderBy(i => i, StringComparer.Ordinal).ToList();
        invoice.RatingFingerprint = Fingerprint(invoice);

        invoice.Status = InvoiceStatus.Draft;
        invoice.IssuedAt = null;
        SetDueDate(invoice, now);
        return new ComputeState(exceptions.Count == 0, reasons.Count > 0);
    }

    /// <summary>
    /// Issues the computed invoice (not saved yet). A net credit (retro credits
    /// above the month's charges) is settled by a <c>SponsorAccountCredit</c>
    /// payment of the same amount; the account itself is credited by
    /// <see cref="EnsureCreditMovedAsync"/> only after this invoice is saved.
    /// </summary>
    private void Issue(PremiumInvoice invoice)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        invoice.Status = InvoiceStatus.Generated;
        invoice.IssuedAt = now;
        SetDueDate(invoice, now);
        if (invoice.TotalAmount >= 0)
            return;
        if (_accounts == null)
            throw new InvalidOperationException($"Invoice {invoice.InvoiceNumber} is a net credit and there is no sponsor account store");
        var entryId = CreditEntryId(invoice);
        invoice.Payments.Add(new InvoicePayment
        {
            PaymentId = entryId,
            Amount = invoice.TotalAmount,
            PaymentDate = now,
            ReceivedDate = now,
            PaymentMethod = CreditTransferMethod,
            ReferenceNumber = entryId,
            RecordedBy = "rated-billing"
        });
        invoice.RecalculateTotals();
        invoice.Status = InvoiceStatus.Paid;
    }

    private const string CreditTransferMethod = "SponsorAccountCredit";

    private static string CreditEntryId(PremiumInvoice invoice) => $"rated-credit-{invoice.Id}";

    /// <summary>
    /// Posts the account credit of a saved, issued net-credit invoice, once:
    /// keyed by the invoice, so a retry, a rerun or a second writer posts nothing
    /// more. Called after every save of an issued rated invoice and for an
    /// already issued one (self-healing a crash between the save and the post).
    /// </summary>
    private async Task EnsureCreditMovedAsync(PremiumInvoice invoice)
    {
        if (invoice.PricingSource != PricingSource.RatingEngine || invoice.Status is InvoiceStatus.Draft or InvoiceStatus.Voided)
            return;
        var transfer = invoice.Payments.FirstOrDefault(p => p.PaymentMethod == CreditTransferMethod && p.PaymentId == CreditEntryId(invoice));
        if (transfer == null || _accounts == null)
            return;
        var entryId = transfer.PaymentId;
        var credit = -transfer.Amount;
        var now = _clock.GetUtcNow().UtcDateTime;
        await _accounts.UpdateAsync(invoice.GroupNumber, account =>
        {
            var earlier = account.Entries.FirstOrDefault(e => e.EntryId == entryId);
            if (earlier != null)
            {
                if (earlier.Amount != credit)
                    throw new InvalidOperationException(
                        $"Sponsor account {invoice.GroupNumber} already holds a credit of {earlier.Amount:0.00} from invoice {invoice.InvoiceNumber}, not {credit:0.00}; needs attention");
                return;
            }
            account.UnappliedCredit += credit;
            account.NetBalance = account.OpenInvoiceBalance - account.UnappliedCredit;
            account.Entries.Add(new SponsorAccountEntry
            {
                EntryId = entryId,
                Type = SponsorAccountEntryType.RatedInvoiceCredit,
                Amount = credit,
                PostedAt = now,
                InvoiceId = invoice.Id,
                Reference = invoice.InvoiceNumber,
                Memo = $"Net credit of rated invoice {invoice.InvoiceNumber} ({invoice.BillingPeriodStart:yyyy-MM})",
                PostedBy = "rated-billing"
            });
        });
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

    private static readonly JsonSerializerOptions FingerprintOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>What a reviewer sees: lines, adjustments (without their computation date), exceptions, review reasons.</summary>
    private static string Fingerprint(PremiumInvoice invoice)
    {
        var payload = new
        {
            invoice.BillFormat,
            invoice.LineItems,
            Adjustments = invoice.Adjustments.Select(a => new
            {
                a.Type, a.Amount, a.Description, a.RelatedMemberId, a.CoverageId, a.ServicePeriodStart, a.ServicePeriodEnd,
                a.IsRatingRetro, a.RateTableVersions, a.IsRateCorrection
            }),
            Exceptions = invoice.RatingExceptions,
            invoice.ReviewReasons
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, FingerprintOptions))).ToLowerInvariant();
    }

    /// <summary>Every amount is whole cents and the invoice total is exactly the sum of its lines.</summary>
    private static void CheckMoney(PremiumInvoice invoice, InvoiceCalculation calculation)
    {
        static bool Cents(decimal amount) => MoneyRounding.ToCents(amount) == amount;
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
            _logger.LogWarning("Rated invoice {InvoiceNumber} {What} as Draft with {Count} rating exception(s) and {Reasons} review reason(s)",
                invoice.InvoiceNumber, what, invoice.RatingExceptions.Count, invoice.ReviewReasons.Count);
        else
            _logger.LogInformation("Rated invoice {InvoiceNumber} {What}: {Lines} lines, {Adjustments} adjustments, total {Total:N2}",
                invoice.InvoiceNumber, what, invoice.LineItems.Count, invoice.Adjustments.Count, invoice.TotalAmount);
    }

    private static string Truncate(string message) => message.Length > 1000 ? message[..1000] : message;
}
