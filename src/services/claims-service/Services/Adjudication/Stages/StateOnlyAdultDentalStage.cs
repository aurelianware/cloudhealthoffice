using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaimsService.Models;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Programs.MediCal;
using CloudHealthOffice.PlanConfiguration;

namespace ClaimsService.Services.Adjudication.Stages;

/// <summary>
/// DHCS APL 26-009: from July 1, 2027, Medi-Cal members aged 19 and older who
/// are not eligible for federally funded full-scope Medi-Cal lose
/// non-emergency dental (<see cref="StateOnlyAdultDentalRule"/>). Runs at
/// <see cref="Order"/> = 160, after provider integrity and before network,
/// pricing and benefits, on the CDT lines (D0000–D9999) of a claim whose plan
/// turns it on with the setting <c>eligibility.mediCal.stateOnlyAdultDental</c>.
///
/// <list type="bullet">
///   <item><description><b>CarveOut</b> (a Dental Managed Care Plan): every CDT
///     line is for a carved-out member → deny CO-109 (not covered by this
///     payer; bill Medi-Cal Dental FFS). Some lines carved out and others not
///     (a claim spanning the member's loss date) → pend <see cref="ScopePendCode"/>.</description></item>
///   <item><description><b>EmergencyOnly</b>: procedures not on the plan's
///     emergency list for an affected member → pend <see cref="ScopePendCode"/>
///     naming them. The examiner decides whether the service was an emergency
///     (severe pain, infection, injury); the list cannot.</description></item>
///   <item><description>No date of birth, or no aid code on file for the date
///     of service → pend <see cref="AidCodePendCode"/>: whether the member is
///     state-only is unknown, and an unknown is never "covered".</description></item>
///   <item><description>Setting unreadable because tenant-service is down →
///     transient pend <see cref="InpatientGroupingStage.SettingUnavailablePendCode"/>,
///     only for claims with a CDT line on or after July 1, 2027.</description></item>
/// </list>
///
/// <para>
/// An examiner approval passes a reviewed <see cref="ScopePendCode"/> or
/// <see cref="AidCodePendCode"/> pend (the orchestrator's override list).
/// The member lookup itself (not found, unavailable) is left to
/// <see cref="BenefitCalculationStage"/>.
/// </para>
/// </summary>
public sealed partial class StateOnlyAdultDentalStage : IClaimAdjudicationStage
{
    public const string StageName = "StateOnlyAdultDental";

    /// <summary>Pend code: services outside an affected member's dental scope.</summary>
    public const string ScopePendCode = "STATEDENTAL";

    /// <summary>Pend code: the rule needs an aid code or date of birth the member record does not have.</summary>
    public const string AidCodePendCode = "AIDCODE";

    /// <summary>CARC 109 — claim/service not covered by this payer/contractor; send it to the correct payer.</summary>
    public const string NotCoveredByThisPayerCarc = "109";

    public static readonly string SettingKey = PlanSettings.MediCalStateOnlyAdultDental.Key;

    private static readonly ActivitySource ActivitySource = new("ClaimsService.Adjudication");

    private readonly IPlanSettings _settings;
    private readonly ILogger<StateOnlyAdultDentalStage> _logger;

    public StateOnlyAdultDentalStage(IPlanSettings settings, ILogger<StateOnlyAdultDentalStage> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public string Name => StageName;
    public int Order => 160;
    public bool IsRequired => false;

    public async Task<ClaimAdjudicationStageResult> ExecuteAsync(ClaimAdjudicationContext context, CancellationToken ct)
    {
        var claim = context.Claim;
        var dentalLines = claim.ClaimLines
            .Where(l => IsCdt(l.ProcedureCode))
            .Select(l => (Line: l, Date: ServiceDate(l, claim)))
            .ToList();

        // Nothing the rule can touch: no CDT line on or after its first date.
        if (!dentalLines.Any(l => l.Date >= StateOnlyAdultDentalRule.EffectiveDate))
            return ClaimAdjudicationStageResult.Pass(StageName);

        // An unknown member is BenefitCalculationStage's to deny or pend.
        if (context.MemberLookup is MemberLookupOutcome.NotFound or MemberLookupOutcome.Unavailable)
            return ClaimAdjudicationStageResult.Pass(StageName);

        using var activity = ActivitySource.StartActivity("Adjudication.StateOnlyAdultDental", ActivityKind.Internal);
        activity?.SetTag("claim.versionId", context.ClaimVersionId);

        var asOf = dentalLines.Max(l => l.Date);
        var setting = await _settings
            .ResolveAsync(context.TenantId, SettingKey, context.SettingScopeForClaim(), asOf, ct)
            .ConfigureAwait(false);

        if (setting.IsFallback)
        {
            var reason = $"{ClaimAdjudicationOrchestrator.ServiceUnavailableReason}: could not read {SettingKey} " +
                         $"for a dental claim on or after {StateOnlyAdultDentalRule.EffectiveDate:yyyy-MM-dd} ({StateOnlyAdultDentalRule.Authority}); " +
                         "retried on an examiner approval re-run";
            RecordPend(context, InpatientGroupingStage.SettingUnavailablePendCode, reason);
            activity?.SetTag("state_dental.outcome", "setting_unavailable");
            return ClaimAdjudicationStageResult.Pend(StageName, reason).AsCheckUnavailable(context);
        }

        StateOnlyAdultDentalOptions options;
        try
        {
            options = StateOnlyAdultDentalOptions.Parse(setting.Value);
        }
        catch (JsonException ex)
        {
            var reason = $"{SettingKey} is not valid ({ex.Message}); {StateOnlyAdultDentalRule.Authority} could not be applied.";
            _logger.LogError(ex, "Unreadable {SettingKey} for tenant {TenantId}", SettingKey, LogSanitizer.Sanitize(context.TenantId));
            RecordPend(context, ScopePendCode, reason);
            return ClaimAdjudicationStageResult.Pend(StageName, reason);
        }

        if (options.Mode == StateOnlyAdultDentalMode.Off)
        {
            activity?.SetTag("state_dental.outcome", "off");
            return ClaimAdjudicationStageResult.Pass(StageName);
        }

        var member = context.ResolvedMember;
        var aidCodes = member?.AidCodes ?? [];
        var decisions = dentalLines
            .GroupBy(l => l.Date)
            .Select(g => (Lines: g.Select(x => x.Line).ToList(),
                Decision: StateOnlyAdultDentalRule.Evaluate(
                    options, member?.DateOfBirth, g.Key, aidCodes, g.Select(x => x.Line.ProcedureCode))))
            .ToList();

        var notes = decisions
            .Where(d => d.Decision.Outcome is StateOnlyAdultDentalOutcome.Exception or StateOnlyAdultDentalOutcome.EmergencyCovered)
            .Select(d => d.Decision.Reason)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var unknown = decisions.FirstOrDefault(d => d.Decision.Outcome == StateOnlyAdultDentalOutcome.Unknown);
        if (unknown.Decision is not null)
        {
            activity?.SetTag("state_dental.outcome", "unknown");
            return PendWith(context, AidCodePendCode, unknown.Decision.Reason, notes);
        }

        var carvedOut = decisions.Where(d => d.Decision.Outcome == StateOnlyAdultDentalOutcome.CarvedOut).ToList();
        if (carvedOut.Count > 0)
        {
            var reason = carvedOut[0].Decision.Reason;
            if (carvedOut.Sum(d => d.Lines.Count) == dentalLines.Count && dentalLines.Count == claim.ClaimLines.Count)
            {
                activity?.SetTag("state_dental.outcome", "carved_out");
                _logger.LogInformation(
                    "StateOnlyAdultDentalStage denied claim {ClaimVersionId}: member carved out to Medi-Cal Dental FFS",
                    LogSanitizer.Sanitize(context.ClaimVersionId));
                AdjudicationDenialCodes.Apply(context, NotCoveredByThisPayerCarc, reason);
                return ClaimAdjudicationStageResult.Deny(StageName, reason);
            }

            var lines = string.Join(", ", carvedOut.SelectMany(d => d.Lines).Select(l => l.LineNumber).Order());
            activity?.SetTag("state_dental.outcome", "carved_out_partial");
            return PendWith(context, ScopePendCode, $"Lines {lines}: {reason}", notes);
        }

        var outOfScope = decisions.Where(d => d.Decision.Outcome == StateOnlyAdultDentalOutcome.NonEmergencyProcedures).ToList();
        if (outOfScope.Count > 0)
        {
            var codes = outOfScope.SelectMany(d => d.Decision.NonEmergencyProcedureCodes ?? []).ToHashSet(StringComparer.Ordinal);
            var lines = string.Join(", ", outOfScope
                .SelectMany(d => d.Lines)
                .Where(l => codes.Contains(l.ProcedureCode.Trim().ToUpperInvariant()))
                .Select(l => l.LineNumber)
                .Order());
            activity?.SetTag("state_dental.outcome", "non_emergency");
            return PendWith(context, ScopePendCode,
                $"Lines {lines}: {outOfScope[0].Decision.Reason} Approve only if the service treated an emergency.", notes);
        }

        activity?.SetTag("state_dental.outcome", notes.Count > 0 ? "covered" : "not_applicable");
        return new ClaimAdjudicationStageResult { StageName = StageName, Notes = notes };
    }

    private static ClaimAdjudicationStageResult PendWith(
        ClaimAdjudicationContext context, string code, string reason, IReadOnlyList<string> notes)
    {
        reason = reason.Length <= 500 ? reason : reason[..500];
        RecordPend(context, code, reason);
        var pend = ClaimAdjudicationStageResult.Pend(StageName, reason);
        return notes.Count == 0 ? pend : new ClaimAdjudicationStageResult
        {
            StageName = pend.StageName,
            Continue = pend.Continue,
            Outcome = pend.Outcome,
            Reason = pend.Reason,
            Notes = notes,
        };
    }

    /// <summary>
    /// Sets this stage's pend. The orchestrator keeps an earlier stage's pend
    /// as the claim's and adds this one to its additional reasons.
    /// </summary>
    private static void RecordPend(ClaimAdjudicationContext context, string code, string reason) =>
        context.PendDetails = new PendDetails
        {
            PendCode = code,
            PendReason = reason.Length <= 500 ? reason : reason[..500],
            PendedAt = DateTime.UtcNow,
        };

    private static DateTime ServiceDate(AdapterClaimLine line, AdapterClaim claim) =>
        (line.ServiceDateFrom != default ? line.ServiceDateFrom : claim.ServiceDateFrom).Date;

    /// <summary>A CDT code: D followed by four digits.</summary>
    internal static bool IsCdt(string? code) => !string.IsNullOrWhiteSpace(code) && CdtCode().IsMatch(code.Trim());

    [GeneratedRegex("^[Dd][0-9]{4}$")]
    private static partial Regex CdtCode();
}
