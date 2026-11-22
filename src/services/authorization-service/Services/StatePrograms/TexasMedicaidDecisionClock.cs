using AuthorizationService.Models;

namespace AuthorizationService.Services.StatePrograms;

/// <summary>
/// Which tenants run the Texas Medicaid decision clock, and the calendar it
/// counts business days on (TX-05).
///
/// <code>
///   PriorAuthDeadlines:TexasMedicaid:Tenants:0     tenant ids the clock applies to (none by default)
///   PriorAuthDeadlines:TexasMedicaid:TimeZone      IANA zone business days are counted in (America/Chicago)
///   PriorAuthDeadlines:TexasMedicaid:Holidays:0    non-business dates, yyyy-MM-dd, as the plan's compliance team approves them
/// </code>
///
/// No holiday is built in. Which days count is a contract and compliance
/// decision, so the plan supplies the list; with none, only weekends are
/// skipped.
/// </summary>
public sealed class TexasMedicaidDecisionClockOptions
{
    public const string SectionName = "PriorAuthDeadlines:TexasMedicaid";

    public List<string> Tenants { get; set; } = [];

    public string TimeZone { get; set; } = "America/Chicago";

    public List<DateOnly> Holidays { get; set; } = [];

    public bool AppliesTo(string? tenantId) =>
        !string.IsNullOrEmpty(tenantId) && Tenants.Contains(tenantId, StringComparer.Ordinal);
}

/// <summary>
/// The Texas Medicaid managed care prior-authorization decision clocks (TX-05),
/// applied on top of the federal receipt clock in
/// <see cref="AuthorizationDeadlinePolicy"/>. The earlier deadline always wins,
/// so this can only shorten a deadline, never lengthen one.
///
/// <para>Tex. Gov't Code ch. 540, subch. G (eff. April 1, 2025), from receipt of the request:</para>
/// <list type="bullet">
///   <item>§540.0302(b)(3): hospitalized member, poststabilization care or a
///     life-threatening condition: <b>1 hour</b>.</item>
///   <item>§540.0302(b)(2): hospitalized member, an acute care inpatient
///     provider's request for services or equipment needed for discharge:
///     <b>72 hours</b>.</item>
///   <item>§540.0302(b)(1): any other request for a hospitalized member:
///     <b>1 business day</b>.</item>
///   <item>§540.0303(b)(1): member not hospitalized: <b>3 business days</b>.</item>
/// </list>
///
/// <para>
/// Scope and limits. Applies only to <see cref="LineOfBusiness.Medicaid"/>
/// requests of a tenant listed in <see cref="TexasMedicaidDecisionClockOptions.Tenants"/>.
/// The scenario is the one stated at intake; when none is stated, a concurrent
/// review is taken as a hospitalized member and anything else as not
/// hospitalized, and the policy version records that it was inferred. The
/// incomplete-documentation process (§540.0303(b)(2), (c)) and the
/// reconsideration timeline (§540.0306) are set by HHSC, not by statute, so
/// they are not modelled here; neither is any shorter contract or UMCM
/// timeframe. Those remain plan policy inputs.
/// </para>
///
/// <para>
/// Business days are counted in the configured time zone, skipping weekends
/// and the configured holidays. "Within N business days after receipt" is read
/// conservatively as the same local time of day on the Nth business day after
/// the day of receipt; a request received on a non-business day starts from
/// the beginning of the next business day.
/// </para>
/// </summary>
public static class TexasMedicaidDecisionClock
{
    public const string Version = "tx-gc-540-v1";

    /// <summary>Policy-version suffix when the scenario was inferred rather than stated.</summary>
    public const string InferredSuffix = "+inferred";

    /// <summary>
    /// Shortens <paramref name="authorization"/>'s decision deadline to the Texas
    /// clock when it applies and is earlier. Call after
    /// <see cref="AuthorizationDeadlinePolicy.Initialize"/>. Returns whether
    /// the Texas clock applied (whether or not it was the earlier one).
    /// </summary>
    public static bool Apply(Authorization authorization, TexasMedicaidDecisionClockOptions options)
    {
        if (!options.AppliesTo(authorization.TenantId)
            || authorization.LineOfBusiness != CloudHealthOffice.Infrastructure.Programs.LineOfBusiness.Medicaid)
            return false;

        var inferred = authorization.DecisionScenario == PaDecisionScenario.Unspecified;
        var scenario = EffectiveScenario(authorization);
        var receipt = AuthorizationDeadlinePolicy.Receipt(authorization);
        var texas = DueAt(receipt, scenario, options);
        var federal = AuthorizationDeadlinePolicy.Deadline(authorization);

        authorization.DecisionDueAt = texas < federal ? texas : federal;
        authorization.DecisionDeadlinePolicy =
            $"{AuthorizationDeadlinePolicy.Version}+{Version}:{scenario}{(inferred ? InferredSuffix : string.Empty)}";
        return true;
    }

    /// <summary>The stated scenario, or the one inferred from the request type.</summary>
    public static PaDecisionScenario EffectiveScenario(Authorization authorization) =>
        authorization.DecisionScenario != PaDecisionScenario.Unspecified
            ? authorization.DecisionScenario
            : authorization.AuthorizationType == AuthorizationType.ConcurrentReview
                ? PaDecisionScenario.Hospitalized
                : PaDecisionScenario.NotHospitalized;

    /// <summary>The Texas deadline, in UTC, for a request received at <paramref name="receivedAtUtc"/>.</summary>
    public static DateTime DueAt(DateTime receivedAtUtc, PaDecisionScenario scenario, TexasMedicaidDecisionClockOptions options)
    {
        var receipt = AuthorizationDeadlinePolicy.Utc(receivedAtUtc);
        return scenario switch
        {
            PaDecisionScenario.PoststabilizationOrLifeThreatening => receipt.AddHours(1),
            PaDecisionScenario.InpatientDischarge => receipt.AddHours(72),
            PaDecisionScenario.Hospitalized => AddBusinessDays(receipt, 1, options),
            _ => AddBusinessDays(receipt, 3, options),
        };
    }

    /// <summary>
    /// The same local time of day on the <paramref name="days"/>th business day
    /// after the day of <paramref name="utc"/>; from the start of the next
    /// business day when <paramref name="utc"/> is not on one.
    /// </summary>
    public static DateTime AddBusinessDays(DateTime utc, int days, TexasMedicaidDecisionClockOptions options)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone);
        var holidays = options.Holidays.ToHashSet();
        bool IsBusinessDay(DateOnly d) =>
            d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !holidays.Contains(d);

        var local = TimeZoneInfo.ConvertTimeFromUtc(AuthorizationDeadlinePolicy.Utc(utc), zone);
        var day = DateOnly.FromDateTime(local);
        var timeOfDay = local.TimeOfDay;
        if (!IsBusinessDay(day))
        {
            do day = day.AddDays(1); while (!IsBusinessDay(day));
            // As if received at the opening of that business day.
            timeOfDay = TimeSpan.Zero;
        }

        for (var counted = 0; counted < days;)
        {
            day = day.AddDays(1);
            if (IsBusinessDay(day)) counted++;
        }

        var dueLocal = day.ToDateTime(TimeOnly.FromTimeSpan(timeOfDay), DateTimeKind.Unspecified);
        // A local time skipped by a DST change moves forward to the first valid instant.
        while (zone.IsInvalidTime(dueLocal)) dueLocal = dueLocal.AddMinutes(1);
        return TimeZoneInfo.ConvertTimeToUtc(dueLocal, zone);
    }
}
