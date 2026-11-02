using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AuthorizationService.Models;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Programs.MediCal;
using CloudHealthOffice.PlanConfiguration;

namespace AuthorizationService.Services.StatePrograms;

/// <summary>What the APL 26-009 check decided for a prior authorization request.</summary>
public enum StateDentalPaOutcome
{
    /// <summary>Deny the whole request (278 A3) with <see cref="StateDentalPaDetermination.DenialReasonCode"/>.</summary>
    Deny,

    /// <summary>Pend the request (278 A4) for a reviewer, with the reason.</summary>
    Pend,
}

/// <summary>The check's decision and, for the reviewer and the provider, why.</summary>
public sealed record StateDentalPaDetermination(StateDentalPaOutcome Outcome, string Reason, string? DenialReasonCode = null);

/// <summary>
/// DHCS APL 26-009 for dental prior authorization: the same rule claims-service
/// applies at adjudication (<see cref="StateOnlyAdultDentalRule"/>, plan
/// setting <c>eligibility.mediCal.stateOnlyAdultDental</c>), applied when the
/// request arrives, so a Dental Managed Care Plan does not authorize services
/// for a member it no longer covers and a payer that keeps the member does not
/// approve a non-emergency service the member lost.
/// </summary>
public interface IStateOnlyAdultDentalPaCheck
{
    /// <summary>
    /// The decision for <paramref name="authorization"/>, or <c>null</c> when
    /// the rule does not touch it (no CDT code, before July 1, 2027, under 19,
    /// the plan has the rule off, a federally funded aid code, or an exception
    /// or emergency the plan covers). Never throws for an unreachable service:
    /// that is a pend, since an outage is never "covered".
    /// </summary>
    Task<StateDentalPaDetermination?> EvaluateAsync(Authorization authorization, CancellationToken ct = default);
}

/// <summary>
/// Reads the plan setting at the request's line of business and the member's
/// benefit plan (from active coverage on the requested date), then the
/// member's aid codes and date of birth from member-service.
///
/// <list type="bullet">
///   <item><description><b>CarveOut</b>: every requested service is a CDT code
///     → deny <see cref="CarvedOutReasonCode"/> (request it from Medi-Cal
///     Dental fee-for-service); a request mixing dental and other codes → pend.</description></item>
///   <item><description><b>EmergencyOnly</b>: requested procedures not on the
///     plan's emergency list → pend naming them; the reviewer decides whether
///     the service treats an emergency.</description></item>
///   <item><description>No date of birth or no aid code on file for the date,
///     or coverage-service, member-service or tenant-service unreachable, or
///     the setting unreadable → pend.</description></item>
/// </list>
/// </summary>
public sealed partial class StateOnlyAdultDentalPaCheck(
    IPlanSettings settings,
    IStateDentalMemberLookup members,
    ILogger<StateOnlyAdultDentalPaCheck> logger) : IStateOnlyAdultDentalPaCheck
{
    /// <summary>Denial reason code: the member is carved out of the plan to Medi-Cal Dental fee-for-service.</summary>
    public const string CarvedOutReasonCode = "STATEDENTAL-FFS";

    public static readonly string SettingKey = PlanSettings.MediCalStateOnlyAdultDental.Key;

    private static readonly ActivitySource ActivitySource = new("AuthorizationService.StateOnlyAdultDental");

    public async Task<StateDentalPaDetermination?> EvaluateAsync(Authorization authorization, CancellationToken ct = default)
    {
        var requested = authorization.RequestedServices ?? [];
        var dentalCodes = requested.Where(s => IsCdt(s.ProcedureCode)).Select(s => s.ProcedureCode.Trim().ToUpperInvariant()).ToList();
        if (dentalCodes.Count == 0) return null;

        var dos = authorization.RequestedServiceDateFrom.Date;
        if (dos < StateOnlyAdultDentalRule.EffectiveDate) return null;

        // The request's own date of birth, when it has one, settles "under 19"
        // without asking anyone; member-service's is preferred for the rule.
        var requestDob = authorization.PatientDateOfBirth == default ? (DateTime?)null : authorization.PatientDateOfBirth.Date;
        if (requestDob is { } early && dos < StateOnlyAdultDentalRule.LossDate(early)) return null;

        using var activity = ActivitySource.StartActivity("PriorAuth.StateOnlyAdultDental", ActivityKind.Internal);
        var tenantId = authorization.TenantId;
        var memberId = authorization.MemberId;

        string? benefitPlan;
        try
        {
            benefitPlan = BenefitPlanFor(await members.ActiveCoverageAsync(tenantId, memberId, dos, ct).ConfigureAwait(false),
                authorization.CoverageId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Coverage lookup failed for member {MemberId}; dental request pended", LogSanitizer.MaskId(memberId));
            return Pend($"{StateOnlyAdultDentalRule.Authority}: could not read the member's coverage for {dos:yyyy-MM-dd}; review before deciding.");
        }

        var scope = new SettingScope { LineOfBusiness = authorization.LineOfBusiness.ToString(), BenefitPlan = benefitPlan };
        var setting = await settings.ResolveAsync(tenantId, SettingKey, scope, dos, ct).ConfigureAwait(false);
        if (setting.IsFallback)
        {
            activity?.SetTag("state_dental.outcome", "setting_unavailable");
            return Pend($"{StateOnlyAdultDentalRule.Authority}: could not read {SettingKey} for a dental request on or after " +
                        $"{StateOnlyAdultDentalRule.EffectiveDate:yyyy-MM-dd}; review before deciding.");
        }

        StateOnlyAdultDentalOptions options;
        try
        {
            options = StateOnlyAdultDentalOptions.Parse(setting.Value);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Unreadable {SettingKey} for tenant {TenantId}", SettingKey, LogSanitizer.Sanitize(tenantId));
            return Pend($"{SettingKey} is not valid ({ex.Message}); {StateOnlyAdultDentalRule.Authority} could not be applied.");
        }

        if (options.Mode == StateOnlyAdultDentalMode.Off)
        {
            activity?.SetTag("state_dental.outcome", "off");
            return null;
        }

        StateDentalMember? member;
        try
        {
            member = await members.MemberAsync(tenantId, memberId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Member lookup failed for member {MemberId}; dental request pended", LogSanitizer.MaskId(memberId));
            return Pend($"{StateOnlyAdultDentalRule.Authority}: could not read the member's aid codes; review before deciding.");
        }

        var decision = StateOnlyAdultDentalRule.Evaluate(
            options, member?.DateOfBirth ?? requestDob, dos, member?.AidCodes ?? [], dentalCodes);
        activity?.SetTag("state_dental.outcome", decision.Outcome.ToString());

        switch (decision.Outcome)
        {
            case StateOnlyAdultDentalOutcome.CarvedOut when dentalCodes.Count == requested.Count:
                logger.LogInformation(
                    "Prior authorization {AuthNumber} denied: member carved out to Medi-Cal Dental FFS",
                    LogSanitizer.Sanitize(authorization.AuthorizationNumber));
                return new(StateDentalPaOutcome.Deny, Truncate(decision.Reason), CarvedOutReasonCode);
            case StateOnlyAdultDentalOutcome.CarvedOut:
                return Pend($"Dental services: {decision.Reason}");
            case StateOnlyAdultDentalOutcome.NonEmergencyProcedures:
                return Pend($"{decision.Reason} Approve only if the service treats an emergency.");
            case StateOnlyAdultDentalOutcome.Unknown:
                return Pend(decision.Reason);
            default:
                return null;
        }
    }

    /// <summary>
    /// The benefit plan whose setting applies: the plan of the coverage the
    /// request names when it is active on the date, else the only active plan.
    /// Several active plans and none named → <c>null</c> (line-of-business and
    /// tenant levels only).
    /// </summary>
    public static string? BenefitPlanFor(IReadOnlyList<ActiveCoveragePlan> active, string? coverageId)
    {
        if (!string.IsNullOrWhiteSpace(coverageId)
            && active.FirstOrDefault(c => string.Equals(c.CoverageId, coverageId.Trim(), StringComparison.Ordinal)) is { } named)
            return named.PlanId;
        var plans = active.Select(c => c.PlanId).Distinct(StringComparer.Ordinal).ToList();
        return plans.Count == 1 ? plans[0] : null;
    }

    private static StateDentalPaDetermination Pend(string reason) => new(StateDentalPaOutcome.Pend, Truncate(reason));

    private static string Truncate(string reason) => reason.Length <= 500 ? reason : reason[..500];

    /// <summary>A CDT code: D followed by four digits.</summary>
    internal static bool IsCdt(string? code) => !string.IsNullOrWhiteSpace(code) && CdtCode().IsMatch(code.Trim());

    [GeneratedRegex("^[Dd][0-9]{4}$")]
    private static partial Regex CdtCode();
}
