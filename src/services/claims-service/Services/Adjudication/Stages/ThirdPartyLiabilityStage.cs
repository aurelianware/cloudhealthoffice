using System.Diagnostics;
using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Services.Adjudication.Tpl;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.CobEngine.Domain;
using Microsoft.Extensions.Options;

namespace ClaimsService.Services.Adjudication.Stages;

/// <summary>
/// Medicaid third-party liability. Medicaid is the payer of last resort
/// (42 CFR 433.139): when the state's TPL file shows the member has another
/// payer for the service, the claim is cost-avoided (denied CO-22 / MA04 so
/// the provider bills that payer first), unless the service is one Medicaid
/// must pay and recover afterwards.
///
/// <para>Runs at 276, right after <see cref="CoordinationOfBenefitsStage"/>
/// (275) and before benefits (300). Only Medicaid claims; off by default
/// (<see cref="ThirdPartyLiabilityOptions.Mode"/>).</para>
///
/// <list type="bullet">
///   <item><description>The 837 already carries the other payer's adjudication
///     (2320 / 2430) → pass; COB pays the balance.</description></item>
///   <item><description>A Medicaid-primary plan (Texas STAR Health) → pass.</description></item>
///   <item><description>Every line covered by an active resource that is not
///     pay-and-chase-only, and not an exempt service → cost avoid.</description></item>
///   <item><description>Some lines covered, others not → pend for an examiner
///     (adjudication denies whole claims; a split needs a person).</description></item>
///   <item><description>Covered only by pay-and-chase resources (Medi-Cal OHC
///     code A) or exempt services (pediatric preventive) → pass, with those
///     lines recorded for recovery.</description></item>
///   <item><description>TPL lookup failed → pend (never a denial signal).</description></item>
/// </list>
/// </summary>
public sealed class ThirdPartyLiabilityStage : IClaimAdjudicationStage
{
    public const string StageName = "ThirdPartyLiability";
    public const string PendCode = "TPL";
    public const string GroupCode = "CO";

    public const string LookupUnavailableReason = "tpl-lookup-unavailable";
    public const string PartialScopeReason = "tpl-partial-scope";
    public const string CostAvoidReason = "tpl-cost-avoid";
    public const string PayAndChaseReason = "tpl-pay-and-chase";
    public const string NoTplReason = "tpl-none";
    public const string OtherPayerBilledReason = "tpl-other-payer-billed";
    public const string MedicaidPrimaryPlanReason = "tpl-medicaid-primary-plan";

    private static readonly ActivitySource ActivitySource = new("ClaimsService.Adjudication");

    private readonly ITplClient _tpl;
    private readonly ThirdPartyLiabilityOptions _options;
    private readonly ILogger<ThirdPartyLiabilityStage> _logger;

    public ThirdPartyLiabilityStage(
        ITplClient tpl, IOptions<ThirdPartyLiabilityOptions> options, ILogger<ThirdPartyLiabilityStage> logger)
    {
        _tpl = tpl;
        _options = options.Value;
        _logger = logger;
    }

    public string Name => StageName;
    public int Order => 276;
    public bool IsRequired => true;

    public async Task<ClaimAdjudicationStageResult> ExecuteAsync(ClaimAdjudicationContext context, CancellationToken ct)
    {
        if (_options.Mode == TplEnforcementMode.Off || context.Claim.LineOfBusiness != LineOfBusiness.Medicaid)
            return ClaimAdjudicationStageResult.Pass(StageName);

        using var activity = ActivitySource.StartActivity("Adjudication.ThirdPartyLiability", ActivityKind.Internal);
        activity?.SetTag("tpl.mode", _options.Mode.ToString());
        var claim = context.Claim;

        if (IsMedicaidPrimaryPlan(context))
            return Record(context, activity, TplDecision.NoTpl, MedicaidPrimaryPlanReason,
                "Medicaid pays before other insurance on this plan.");

        if (OtherPayerBilled(claim))
            return Record(context, activity, TplDecision.OtherPayerBilled, OtherPayerBilledReason,
                "The 837 carries the other payer's adjudication; coordination of benefits pays the balance.");

        var serviceDate = EarliestServiceDate(claim);
        var resources = await _tpl.GetActiveAsync(context.TenantId, claim.MemberId, serviceDate, ct).ConfigureAwait(false);
        if (resources is null)
        {
            const string message = "Third-party liability lookup failed; cannot tell whether another payer covers this claim.";
            if (_options.Mode == TplEnforcementMode.SoftValidation)
            {
                Record(context, activity, TplDecision.Pended, LookupUnavailableReason, message);
                return ClaimAdjudicationStageResult.Pass(StageName).AsCheckUnavailable(context, LookupUnavailableReason);
            }
            return PendResult(context, activity, LookupUnavailableReason, message, [], [], [])
                .AsCheckUnavailable(context, LookupUnavailableReason);
        }

        var active = resources.Where(r => r.EffectiveDate.Date <= serviceDate.Date
                                          && (r.EndDate is null || r.EndDate.Value.Date >= serviceDate.Date)).ToList();
        if (active.Count == 0)
            return Record(context, activity, TplDecision.NoTpl, NoTplReason, null);

        var accident = TplServiceRules.IsAccidentRelated(claim);
        var dob = context.ResolvedMember?.DateOfBirth;
        var costAvoid = new List<int>();
        var payAndChase = new List<TplPayAndChaseLine>();
        var drivers = new Dictionary<string, TplResource>();

        foreach (var line in claim.ClaimLines)
        {
            var category = TplServiceRules.Categorise(claim, line);
            var covering = active.Where(r => TplServiceRules.Covers(r.Scope, category, accident)).ToList();
            if (covering.Count == 0) continue;
            foreach (var r in covering) drivers[r.Id] = r;

            if (covering.All(r => r.PayAndChaseOnly))
            {
                payAndChase.Add(new(line.LineNumber, "Other coverage is pay-and-chase only (" +
                    string.Join(", ", covering.Select(Describe).Distinct()) + ")."));
            }
            else if (_options.PayPediatricPreventive
                     && TplServiceRules.IsPediatricPreventive(claim, line, dob, _options.PediatricAgeLimit))
            {
                payAndChase.Add(new(line.LineNumber, "Pediatric preventive service: paid without regard to TPL (SSA 1902(a)(25)(E))."));
            }
            else if (StatePaysAndChases(covering, line, claim, dob) is { } stateReason)
            {
                payAndChase.Add(new(line.LineNumber, stateReason));
            }
            else
            {
                costAvoid.Add(line.LineNumber);
            }
        }

        var driverList = drivers.Values.OrderBy(r => r.CarrierName, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
        var carriers = string.Join("; ", driverList.Select(Describe).Distinct());

        if (costAvoid.Count == 0)
        {
            return payAndChase.Count == 0
                ? Record(context, activity, TplDecision.NoTpl, NoTplReason,
                    "Other coverage is on file but does not cover these services.")
                : Record(context, activity, TplDecision.PayAndChase, PayAndChaseReason,
                    $"Medicaid pays and recovers from {carriers}.", driverList, [], payAndChase);
        }

        var allLines = costAvoid.Count == claim.ClaimLines.Count;
        var reasonCode = allLines ? CostAvoidReason : PartialScopeReason;
        var lineList = string.Join(", ", costAvoid);
        var pendMessage = allLines
            ? $"Other coverage pays for every line: {carriers}. Bill that payer first, or approve to pay and recover."
            : $"Other coverage pays for line(s) {lineList} but not the rest: {carriers}. " +
              "Deny the covered lines (CO-22) or approve to pay and recover.";

        if (_options.Mode == TplEnforcementMode.SoftValidation)
            return Record(context, activity, allLines ? TplDecision.CostAvoided : TplDecision.Pended, reasonCode,
                pendMessage + " (soft validation: not applied)", driverList, costAvoid, payAndChase);

        if (ApprovedByExaminer(context, pendMessage))
        {
            // The examiner chose to pay: every covered line becomes a recovery.
            var recovered = payAndChase.Concat(costAvoid.Select(n =>
                new TplPayAndChaseLine(n, "Examiner approved payment; recover from the other payer."))).ToList();
            return Record(context, activity, TplDecision.PayAndChase, PayAndChaseReason,
                $"Examiner approved payment; Medicaid recovers from {carriers}.", driverList, [], recovered);
        }

        if (allLines && _options.Mode == TplEnforcementMode.CostAvoid)
        {
            var denyReason = $"Cost avoided: other coverage pays for these services ({carriers}). " +
                             "Bill that payer first, then submit with its adjudication.";
            Deny(context, denyReason);
            Record(context, activity, TplDecision.CostAvoided, CostAvoidReason, denyReason, driverList, costAvoid, payAndChase);
            _logger.LogInformation("Claim {ClaimVersionId} cost-avoided for TPL ({Count} resource(s))",
                context.ClaimVersionId, driverList.Count);
            return ClaimAdjudicationStageResult.Deny(StageName, denyReason);
        }

        return PendResult(context, activity, reasonCode, pendMessage, driverList, costAvoid, payAndChase);
    }

    private ClaimAdjudicationStageResult PendResult(
        ClaimAdjudicationContext context, Activity? activity, string reasonCode, string message,
        IReadOnlyList<TplResource> drivers, IReadOnlyList<int> costAvoid, IReadOnlyList<TplPayAndChaseLine> payAndChase)
    {
        Record(context, activity, TplDecision.Pended, reasonCode, message, drivers, costAvoid, payAndChase);
        context.PendDetails = new PendDetails
        {
            PendCode = PendCode,
            PendReason = message,
            PendedAt = DateTime.UtcNow,
        };
        return ClaimAdjudicationStageResult.Pend(StageName, message);
    }

    private static ClaimAdjudicationStageResult Record(
        ClaimAdjudicationContext context, Activity? activity, TplDecision decision, string reasonCode, string? message,
        IReadOnlyList<TplResource>? drivers = null, IReadOnlyList<int>? costAvoid = null,
        IReadOnlyList<TplPayAndChaseLine>? payAndChase = null)
    {
        activity?.SetTag("tpl.decision", decision.ToString());
        activity?.SetTag("tpl.reason", reasonCode);
        context.AdjudicationResult.ThirdPartyLiability = new ClaimTplRecord
        {
            Decision = decision.ToString(),
            ReasonCode = reasonCode,
            Message = message,
            Resources = (drivers ?? []).Select(r => new ClaimTplResource
            {
                TplPolicyId = r.Id,
                State = r.State,
                CarrierCode = r.CarrierCode,
                CarrierName = r.CarrierName,
                PolicyNumber = r.PolicyNumber,
                StateCoverageCode = r.StateCoverageCode,
            }).ToList(),
            CostAvoidLines = (costAvoid ?? []).ToList(),
            PayAndChaseLines = (payAndChase ?? []).Select(p => new ClaimTplPayAndChaseLine
            {
                LineNumber = p.LineNumber,
                Reason = p.Reason,
            }).ToList(),
            DecidedAt = DateTime.UtcNow,
        };
        context.TplResult = new TplOutcome
        {
            Decision = decision,
            ReasonCode = reasonCode,
            Message = message,
            Resources = (drivers ?? []).Select(r =>
                new TplOutcomeResource(r.Id, r.State, r.CarrierName, r.PolicyNumber, r.StateCoverageCode)).ToList(),
            CostAvoidLines = costAvoid ?? [],
            PayAndChaseLines = payAndChase ?? [],
        };
        return ClaimAdjudicationStageResult.Pass(StageName);
    }

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
        const string carc = AdjudicationDenialCodes.CoveredByAnotherPayerCarc;
        const string description = "This care may be covered by another payer per coordination of benefits";

        AdjudicationDenialCodes.Apply(context, carc, reason, AdjudicationDenialCodes.MissingPrimaryPayerRarc);
        context.AdjudicationResult.AdjustmentReasons.Add(new ClaimAdjustmentReason
        {
            GroupCode = GroupCode,
            ReasonCode = carc,
            Amount = claim.TotalChargeAmount,
            Description = description,
        });
        // One entry per claim line, in claim-line order, so the projection
        // stamps CO-22 on every line.
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

    /// <summary>The state rule that has Medicaid pay this line first, if any.
    /// The state is the covering TPL row's (the state file it came from).</summary>
    private string? StatePaysAndChases(
        IReadOnlyList<TplResource> covering, AdapterClaimLine line, AdapterClaim claim, DateTime? dob)
    {
        var serviceDate = line.ServiceDateFrom != default ? line.ServiceDateFrom : claim.ServiceDateFrom;
        var underAge = dob is { } d && TplServiceRules.AgeOn(d, serviceDate) < _options.PediatricAgeLimit;
        foreach (var state in covering.Select(r => r.State).Where(s => !string.IsNullOrWhiteSpace(s))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var rules = TplStateRules.For(state, _options.States);
            if (rules.PaysAndChases(line.ProcedureCode, underAge))
                return $"{state.ToUpperInvariant()} pays {line.ProcedureCode} without billing other insurance first" +
                       (string.IsNullOrWhiteSpace(rules.Authority) ? "." : $" ({rules.Authority}).");
        }
        return null;
    }

    private bool IsMedicaidPrimaryPlan(ClaimAdjudicationContext context)
    {
        if (_options.MedicaidPrimaryPlanCodes.Count == 0) return false;
        var plan = context.ResolvedPlan;
        var keys = new[] { plan?.PlanCode, plan?.Id, context.Claim.BenefitPlanId };
        return keys.Any(k => k is not null
                             && _options.MedicaidPrimaryPlanCodes.Contains(k, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The provider already billed another payer: 2320 with a payment,
    /// claim adjustments or 2430 line adjudications, or this plan billed as a later payer.</summary>
    internal static bool OtherPayerBilled(AdapterClaim claim) =>
        (claim.OtherPayers ?? []).Any(p => p.PaidAmount is not null || p.ClaimAdjustments.Count > 0 || p.LineAdjudications.Count > 0)
        || PayerResponsibility.ToSequence(claim.PayerResponsibilityCode) >= 2;

    private static DateTime EarliestServiceDate(AdapterClaim claim)
    {
        var lineDates = claim.ClaimLines.Where(l => l.ServiceDateFrom != default).Select(l => l.ServiceDateFrom).ToList();
        if (lineDates.Count > 0) return lineDates.Min();
        return claim.ServiceDateFrom != default ? claim.ServiceDateFrom : DateTime.UtcNow.Date;
    }

    private static string Describe(TplResource r) =>
        string.IsNullOrWhiteSpace(r.PolicyNumber) ? r.CarrierName : $"{r.CarrierName} policy {r.PolicyNumber}";
}
