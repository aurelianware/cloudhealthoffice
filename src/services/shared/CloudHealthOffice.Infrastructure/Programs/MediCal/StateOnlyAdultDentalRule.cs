using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudHealthOffice.Infrastructure.Programs.MediCal;

/// <summary>
/// A member's aid code (Medi-Cal) or eligibility category for a span of dates,
/// as the state's eligibility file sends it. <see cref="EndDate"/> is the last
/// day it applies; null = open-ended.
/// </summary>
public sealed record AidCodeSpan(string Code, DateTime EffectiveDate, DateTime? EndDate = null)
{
    public bool ActiveOn(DateTime date) =>
        EffectiveDate.Date <= date.Date && (EndDate is null || EndDate.Value.Date >= date.Date);
}

/// <summary>How a plan applies <see cref="StateOnlyAdultDentalRule"/>.</summary>
public enum StateOnlyAdultDentalMode
{
    /// <summary>The rule is off.</summary>
    Off,

    /// <summary>
    /// A Medi-Cal Dental Managed Care Plan: affected members are carved out of
    /// the plan to Medi-Cal Dental fee-for-service (APL 26-009), so the plan
    /// pays and authorizes nothing for them, emergencies included, and the
    /// pregnancy and foster youth exceptions go to FFS too.
    /// </summary>
    CarveOut,

    /// <summary>
    /// A payer that keeps covering the members (Medi-Cal Dental FFS or its
    /// administrator): only emergency dental services are covered, except for
    /// members in a pregnancy or foster youth aid code, who keep full dental.
    /// </summary>
    EmergencyOnly,
}

/// <summary>
/// The plan's configuration of the rule: every aid code is data the plan sets
/// from DHCS's aid code master chart, never code. Read from the plan setting
/// <c>eligibility.mediCal.stateOnlyAdultDental</c>.
/// </summary>
public sealed class StateOnlyAdultDentalOptions
{
    public StateOnlyAdultDentalMode Mode { get; init; } = StateOnlyAdultDentalMode.Off;

    /// <summary>
    /// Aid codes of members who are not eligible for federally funded
    /// full-scope Medi-Cal but receive state-funded full scope (W&amp;I 14007.8).
    /// </summary>
    public IReadOnlyList<string> StateOnlyAidCodes { get; init; } = [];

    /// <summary>
    /// County-designated pregnancy (including one year postpartum) and foster
    /// youth / former foster youth aid codes: members in one keep full dental
    /// (<see cref="StateOnlyAdultDentalMode.EmergencyOnly"/> only).
    /// </summary>
    public IReadOnlyList<string> ExceptionAidCodes { get; init; } = [];

    /// <summary>
    /// CDT codes the payer treats as emergency dental services ("treatment for
    /// severe pain, infections, injuries, and other urgent conditions").
    /// <see cref="StateOnlyAdultDentalMode.EmergencyOnly"/> only; any other
    /// procedure on an affected member goes to review.
    /// </summary>
    public IReadOnlyList<string> EmergencyProcedureCodes { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Reads the setting's JSON. Null, blank or <c>"off"</c> = off. Throws
    /// <see cref="JsonException"/> on JSON that is not the options' shape, so
    /// a broken setting is noticed instead of silently turning the rule off.
    /// </summary>
    public static StateOnlyAdultDentalOptions Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new StateOnlyAdultDentalOptions();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Null) return new StateOnlyAdultDentalOptions();
        if (doc.RootElement.ValueKind == JsonValueKind.String
            && string.Equals(doc.RootElement.GetString(), "off", StringComparison.OrdinalIgnoreCase))
            return new StateOnlyAdultDentalOptions();
        return JsonSerializer.Deserialize<StateOnlyAdultDentalOptions>(doc.RootElement.GetRawText(), Json)
            ?? new StateOnlyAdultDentalOptions();
    }
}

/// <summary>What <see cref="StateOnlyAdultDentalRule.Evaluate"/> decided.</summary>
public enum StateOnlyAdultDentalOutcome
{
    /// <summary>The rule does not touch this service: off, before the date, under 19, or a federally funded aid code.</summary>
    NotApplicable,

    /// <summary>An affected member in a pregnancy or foster youth aid code: full dental (EmergencyOnly mode).</summary>
    Exception,

    /// <summary>An affected member whose services are all on the emergency list (EmergencyOnly mode).</summary>
    EmergencyCovered,

    /// <summary>An affected member and procedures not on the emergency list: review them (EmergencyOnly mode).</summary>
    NonEmergencyProcedures,

    /// <summary>An affected member of a Dental MCP: carved out to Medi-Cal Dental FFS (CarveOut mode).</summary>
    CarvedOut,

    /// <summary>The rule could not be decided: no date of birth, or no aid code on file for the date. Review.</summary>
    Unknown,
}

/// <summary>The decision and, for the examiner and the EOB, why.</summary>
public sealed record StateOnlyAdultDentalDecision(
    StateOnlyAdultDentalOutcome Outcome,
    string Reason,
    DateTime? LossDate = null,
    IReadOnlyList<string>? NonEmergencyProcedureCodes = null)
{
    public static readonly StateOnlyAdultDentalDecision NotApplicable = new(StateOnlyAdultDentalOutcome.NotApplicable, string.Empty);
}

/// <summary>
/// DHCS APL 26-009 (supersedes 26-005), per SB 164 (2026): from July 1, 2027,
/// members aged 19 and older who are not eligible for federally funded
/// full-scope Medi-Cal lose non-emergency dental benefits, except members in a
/// county-designated pregnancy (including one year postpartum) or foster youth
/// aid code. They are carved out of Dental Managed Care Plans to Medi-Cal
/// Dental fee-for-service; members under 19 stay in the plan.
///
/// <para>
/// A member who turns 19 after July 1, 2027 loses dental coverage on the first
/// of their birth month when the birthday is the first of the month, and
/// otherwise on the first of the following month.
/// </para>
///
/// <para>
/// The dates and the age are the APL's and live here; which aid codes are
/// state-only, which are exceptions and which procedures are emergencies are
/// the plan's data (<see cref="StateOnlyAdultDentalOptions"/>).
/// </para>
/// </summary>
public static class StateOnlyAdultDentalRule
{
    /// <summary>Authority cited in reasons and notes.</summary>
    public const string Authority = "DHCS APL 26-009";

    /// <summary>The first date of service the rule applies to (SB 164 delayed it from July 1, 2026).</summary>
    public static readonly DateTime EffectiveDate = new(2027, 7, 1);

    /// <summary>The age from which members lose non-emergency dental.</summary>
    public const int Age = 19;

    /// <summary>
    /// The date the member loses non-emergency dental under the rule: the later
    /// of <see cref="EffectiveDate"/> and the APL's aging-out date for their 19th birthday.
    /// </summary>
    public static DateTime LossDate(DateTime dateOfBirth)
    {
        // A Feb 29 birth date turns 19 on Feb 28 (AddYears); either way the loss date is March 1.
        var birthday = dateOfBirth.Date.AddYears(Age);
        var agingOut = birthday.Day == 1
            ? birthday
            : new DateTime(birthday.Year, birthday.Month, 1).AddMonths(1);
        return agingOut > EffectiveDate ? agingOut : EffectiveDate;
    }

    /// <summary>
    /// Decides the rule for one member on one date of service and the procedures
    /// billed or requested that day.
    /// </summary>
    public static StateOnlyAdultDentalDecision Evaluate(
        StateOnlyAdultDentalOptions options,
        DateTime? dateOfBirth,
        DateTime serviceDate,
        IEnumerable<AidCodeSpan> aidCodes,
        IEnumerable<string> procedureCodes)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Mode == StateOnlyAdultDentalMode.Off) return StateOnlyAdultDentalDecision.NotApplicable;
        var dos = serviceDate.Date;
        if (dos < EffectiveDate) return StateOnlyAdultDentalDecision.NotApplicable;

        if (dateOfBirth is null)
            return new(StateOnlyAdultDentalOutcome.Unknown,
                $"{Authority}: the member has no date of birth on file, so whether they are 19 or older on {dos:yyyy-MM-dd} is unknown.");

        var lossDate = LossDate(dateOfBirth.Value);
        if (dos < lossDate) return StateOnlyAdultDentalDecision.NotApplicable;

        var active = aidCodes.Where(a => a.ActiveOn(dos)).Select(a => a.Code.Trim()).ToList();
        if (active.Count == 0)
            return new(StateOnlyAdultDentalOutcome.Unknown,
                $"{Authority}: the member is {Age} or older on {dos:yyyy-MM-dd} and has no aid code on file for that date, " +
                "so whether they are eligible for federally funded full-scope Medi-Cal is unknown.", lossDate);

        var stateOnly = active.FirstOrDefault(c => Contains(options.StateOnlyAidCodes, c));
        if (stateOnly is null) return StateOnlyAdultDentalDecision.NotApplicable;

        if (options.Mode == StateOnlyAdultDentalMode.CarveOut)
            return new(StateOnlyAdultDentalOutcome.CarvedOut,
                $"{Authority}: members {Age} and older not eligible for federally funded full-scope Medi-Cal (aid code {stateOnly}) " +
                $"are carved out of Dental Managed Care to Medi-Cal Dental fee-for-service from {lossDate:yyyy-MM-dd}; " +
                "bill Medi-Cal Dental FFS.", lossDate);

        var exception = active.FirstOrDefault(c => Contains(options.ExceptionAidCodes, c));
        if (exception is not null)
            return new(StateOnlyAdultDentalOutcome.Exception,
                $"{Authority}: aid code {exception} is a pregnancy or foster youth exception; full dental is covered.", lossDate);

        var nonEmergency = procedureCodes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim().ToUpperInvariant())
            .Where(c => !Contains(options.EmergencyProcedureCodes, c))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (nonEmergency.Count == 0)
            return new(StateOnlyAdultDentalOutcome.EmergencyCovered,
                $"{Authority}: emergency dental services only for aid code {stateOnly}; every procedure is on the emergency list.", lossDate);

        return new(StateOnlyAdultDentalOutcome.NonEmergencyProcedures,
            $"{Authority}: from {lossDate:yyyy-MM-dd} members {Age} and older in aid code {stateOnly} " +
            $"(not eligible for federally funded full-scope Medi-Cal) have emergency dental services only; " +
            $"not on the emergency list: {string.Join(", ", nonEmergency)}.",
            lossDate, nonEmergency);
    }

    private static bool Contains(IReadOnlyList<string> list, string code) =>
        list.Any(c => string.Equals(c?.Trim(), code, StringComparison.OrdinalIgnoreCase));
}
