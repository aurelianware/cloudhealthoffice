using System.Diagnostics;
using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Repositories;
using CloudHealthOffice.Infrastructure.Logging;
using Microsoft.Extensions.Options;

namespace ClaimsService.Services.Adjudication.Stages;

/// <summary>
/// Timely filing. Runs at <see cref="Order"/> = 110, after scrubbing (100)
/// and before duplicate detection (120) and every stage that calls another
/// service, so a late claim is settled without those round trips.
///
/// <para>
/// <b>The clock.</b> Each line is measured from its date of service to the
/// payer receipt date, <see cref="AdapterClaim.ReceivedDate"/>: the instant
/// claims-service accepted the claim, stamped at intake and never taken from
/// the submitter (not 837 BHT04, and not the day adjudication happens to
/// run, so a pend, an outage or a re-run never makes a timely claim late).
/// A professional or dental line is measured from its own from-date; an
/// institutional claim from its statement through-date, for every line.
/// A line is late when it is received more than the limit's days after that
/// date (comparing UTC calendar dates).
/// </para>
///
/// <para>
/// <b>Corrections.</b> An adjustment version (replacement, or a payer
/// reprocessing; <see cref="AdapterClaim.PredecessorVersionId"/> set) is
/// measured from the receipt date of the chain's first version: a corrected
/// claim is timely when the claim it corrects was. Correcting a claim that
/// was itself late does not make it timely.
/// </para>
///
/// <para>
/// <b>The limit</b> comes from <see cref="TimelyFilingOptions"/> for the
/// claim's tenant: a provider contract rule, then the line of business, then
/// the default (<see cref="TimelyFilingOptions.For"/>).
/// </para>
///
/// <para>
/// <b>Outcome.</b> In <see cref="TimelyFilingEnforcementMode.Deny"/> mode a
/// claim whose every line is late is denied with CARC 29 (group CO); one
/// with some late lines pends with pend code <see cref="PendCode"/>, naming
/// the lines, for an examiner to deny them or override the limit. An
/// examiner's approval of exactly that pend lets the claim through on the
/// re-run (proof of timely filing, a retroactive eligibility change, and so
/// on). A claim with no receipt date can't be measured and pends.
/// </para>
/// </summary>
public sealed class TimelyFilingStage : IClaimAdjudicationStage
{
    public const string StageName = "TimelyFiling";
    public const string PendCode = "TIMELY";
    public const string GroupCode = "CO";

    /// <summary>The chain's first version could not be read, so a correction can't be measured.</summary>
    public const string OriginalUnavailableReason =
        "Timely filing could not be checked: the original claim of this correction could not be read; pended for review.";

    public const string NoReceiptDateReason =
        "Timely filing could not be checked: the claim has no payer receipt date; pended for review.";

    /// <summary>A predecessor chain longer than this is treated as unreadable.</summary>
    private const int MaxChainDepth = 50;

    private static readonly ActivitySource ActivitySource = new("ClaimsService.Adjudication");

    private readonly IClaimRepository _repository;
    private readonly TimelyFilingOptions _options;
    private readonly ILogger<TimelyFilingStage> _logger;

    public TimelyFilingStage(
        IClaimRepository repository,
        IOptions<TimelyFilingOptions> options,
        ILogger<TimelyFilingStage> logger)
    {
        _repository = repository;
        _options = options.Value;
        _logger = logger;
    }

    public string Name => StageName;
    public int Order => 110;
    public bool IsRequired => false;

    public async Task<ClaimAdjudicationStageResult> ExecuteAsync(ClaimAdjudicationContext context, CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity("Adjudication.TimelyFiling", ActivityKind.Internal);
        activity?.SetTag("claim.versionId", context.ClaimVersionId);
        activity?.SetTag("tenant.id", context.TenantId);

        var claim = context.Claim;
        var policy = _options.For(context.TenantId);
        activity?.SetTag("timely_filing.mode", policy.Mode.ToString());

        var (limitDays, ruleName) = LimitFor(policy, claim);
        if (limitDays is null)
        {
            activity?.SetTag("timely_filing.outcome", "no_limit");
            return ClaimAdjudicationStageResult.Pass(StageName);
        }
        activity?.SetTag("timely_filing.limit_days", limitDays.Value);

        DateTime? receivedAt;
        try
        {
            receivedAt = await ReceiptDateAsync(context, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reading the original of correction {ClaimVersionId} for timely filing failed",
                LogSanitizer.Sanitize(context.ClaimVersionId));
            activity?.SetTag("timely_filing.outcome", "original_unavailable");
            return PendOrPass(context, policy, OriginalUnavailableReason).AsCheckUnavailable(context);
        }

        if (receivedAt is null)
        {
            activity?.SetTag("timely_filing.outcome", "no_receipt_date");
            return PendOrPass(context, policy, NoReceiptDateReason);
        }

        var lateLines = LateLines(claim, receivedAt.Value, limitDays.Value);
        activity?.SetTag("timely_filing.late_lines", lateLines.Count);
        if (lateLines.Count == 0)
        {
            activity?.SetTag("timely_filing.outcome", "timely");
            return ClaimAdjudicationStageResult.Pass(StageName);
        }

        var limitLabel = ruleName is null ? $"{limitDays}-day filing limit" : $"{limitDays}-day filing limit ({ruleName})";
        var receivedLabel = receivedAt.Value.ToString("yyyy-MM-dd");
        _logger.LogInformation(
            "Claim {ClaimVersionId}: {Late} of {Lines} line(s) received {Received}, past the {Limit}",
            LogSanitizer.Sanitize(context.ClaimVersionId), lateLines.Count, claim.ClaimLines.Count,
            receivedLabel, LogSanitizer.Sanitize(limitLabel));

        if (policy.Mode == TimelyFilingEnforcementMode.SoftValidation)
        {
            activity?.SetTag("timely_filing.outcome", "softvalidation");
            return ClaimAdjudicationStageResult.Pass(StageName);
        }

        var allLate = claim.ClaimLines.Count > 0 && lateLines.Count == claim.ClaimLines.Count;
        var reason = allLate
            ? $"Received {receivedLabel}, past the {limitLabel} (CARC {AdjudicationDenialCodes.TimelyFilingExpiredCarc})."
            : $"Line(s) {string.Join(", ", lateLines.Select(l => l.LineNumber))} received {receivedLabel}, past the {limitLabel}.";
        reason = Truncate(reason);

        if (ApprovedByExaminer(context, reason))
        {
            activity?.SetTag("timely_filing.outcome", "examiner_override");
            return ClaimAdjudicationStageResult.Pass(StageName);
        }

        if (allLate && policy.Mode == TimelyFilingEnforcementMode.Deny)
        {
            Deny(context, reason);
            activity?.SetTag("timely_filing.outcome", "deny");
            return ClaimAdjudicationStageResult.Deny(StageName, reason);
        }

        activity?.SetTag("timely_filing.outcome", "pend");
        return Pend(context, reason);
    }

    /// <summary>The limit for this claim and the name of the provider rule that set it, if one did.</summary>
    internal static (int? LimitDays, string? RuleName) LimitFor(TimelyFilingPolicy policy, AdapterClaim claim)
    {
        var npi = claim.BillingProviderNPI?.Trim() ?? string.Empty;
        var providerRules = (policy.ProviderRules ?? [])
            .Where(r => npi.Length > 0 && (r.BillingProviderNpis ?? []).Any(n => string.Equals(n?.Trim(), npi, StringComparison.Ordinal)))
            .ToList();
        var rule = providerRules.FirstOrDefault(r => r.LineOfBusiness == claim.LineOfBusiness)
                   ?? providerRules.FirstOrDefault(r => r.LineOfBusiness is null);
        if (rule is not null)
            return (rule.LimitDays, string.IsNullOrWhiteSpace(rule.Name) ? "provider contract" : rule.Name.Trim());

        if (policy.LineOfBusinessLimitDays is { } byLob && byLob.TryGetValue(claim.LineOfBusiness, out var lobDays))
            return (lobDays, null);

        return (policy.DefaultLimitDays, null);
    }

    /// <summary>
    /// The receipt date the limit is measured to: this claim's own, or for an
    /// adjustment version the chain's first version's. Null when unknown.
    /// Throws when the chain can't be read.
    /// </summary>
    private async Task<DateTime?> ReceiptDateAsync(ClaimAdjudicationContext context, CancellationToken ct)
    {
        var claim = context.Claim;
        if (string.IsNullOrWhiteSpace(claim.PredecessorVersionId))
            return ReceiptOf(claim.ReceivedDate, claim.SubmittedDate);

        var nextId = claim.PredecessorVersionId;
        for (var depth = 0; depth < MaxChainDepth; depth++)
        {
            ct.ThrowIfCancellationRequested();
            var version = await _repository.GetByIdAsync(nextId!).ConfigureAwait(false);
            if (version is null || !string.Equals(version.TenantId, context.TenantId, StringComparison.Ordinal))
                throw new InvalidOperationException($"Version {nextId} of the claim's chain was not found.");
            if (string.IsNullOrWhiteSpace(version.PredecessorVersionId))
                return ReceiptOf(version.ReceivedDate, version.SubmittedDate);
            nextId = version.PredecessorVersionId;
        }
        throw new InvalidOperationException($"The claim's version chain is longer than {MaxChainDepth} versions.");
    }

    private static DateTime? ReceiptOf(DateTime? receivedDate, DateTime submittedDate) =>
        receivedDate is { } r && r != default ? r : submittedDate != default ? submittedDate : null;

    internal static List<AdapterClaimLine> LateLines(AdapterClaim claim, DateTime receivedAt, int limitDays)
    {
        var received = receivedAt.Date;
        var late = new List<AdapterClaimLine>();
        foreach (var line in claim.ClaimLines)
        {
            var serviceDate = ClockStart(claim, line);
            if (serviceDate is null) continue;
            if (received > serviceDate.Value.Date.AddDays(limitDays)) late.Add(line);
        }
        return late;
    }

    /// <summary>The date a line's filing clock starts; null when the claim carries none.</summary>
    internal static DateTime? ClockStart(AdapterClaim claim, AdapterClaimLine line)
    {
        if (claim.ClaimType == ClaimType.Institutional)
        {
            var through = claim.ServiceDateTo != default ? claim.ServiceDateTo : claim.ServiceDateFrom;
            return through == default ? null : through;
        }
        if (line.ServiceDateFrom != default) return line.ServiceDateFrom;
        return claim.ServiceDateFrom == default ? null : claim.ServiceDateFrom;
    }

    private static ClaimAdjudicationStageResult PendOrPass(
        ClaimAdjudicationContext context, TimelyFilingPolicy policy, string reason)
    {
        if (policy.Mode == TimelyFilingEnforcementMode.SoftValidation || ApprovedByExaminer(context, reason))
            return ClaimAdjudicationStageResult.Pass(StageName);
        return Pend(context, reason);
    }

    private static ClaimAdjudicationStageResult Pend(ClaimAdjudicationContext context, string reason)
    {
        context.PendDetails = new PendDetails
        {
            PendCode = PendCode,
            PendReason = reason,
            PendedAt = DateTime.UtcNow,
        };
        return ClaimAdjudicationStageResult.Pend(StageName, reason);
    }

    /// <summary>The approval re-run's examiner reviewed exactly this pend: the limit is overridden.</summary>
    private static bool ApprovedByExaminer(ClaimAdjudicationContext context, string reason)
    {
        if (context.ExaminerApproval is not { } approval || !approval.Reviewed(PendCode, reason, exactReason: true))
            return false;
        context.ExaminerOverrides.Add($"{StageName}: {PendCode}: {reason}");
        return true;
    }

    private static void Deny(ClaimAdjudicationContext context, string reason)
    {
        var claim = context.Claim;
        const string carc = AdjudicationDenialCodes.TimelyFilingExpiredCarc;
        const string description = "The time limit for filing has expired";

        AdjudicationDenialCodes.Apply(context, carc, reason);
        context.AdjudicationResult.AdjustmentReasons.Add(new ClaimAdjustmentReason
        {
            GroupCode = GroupCode,
            ReasonCode = carc,
            Amount = claim.TotalChargeAmount,
            Description = description,
        });

        // One entry per claim line, in claim-line order, so the projection
        // stamps CO-29 on every line.
        context.LineAdjudicationResults = claim.ClaimLines
            .Select(l => new LineAdjudicationResult
            {
                AllowedAmount = 0m,
                PaidAmount = 0m,
                PatientResponsibility = 0m,
                AdjustmentReasons = new List<ClaimAdjustmentReason>
                {
                    new() { GroupCode = GroupCode, ReasonCode = carc, Amount = l.ChargeAmount, Description = description },
                },
            })
            .ToList();
    }

    private static string Truncate(string reason) => reason.Length > 500 ? reason[..500] : reason;
}
