namespace CloudHealthOffice.Tools.PaTimelinessChallenge;

/// <summary>
/// The CMS-0057-F decision timeframes, written out from the rule rather than
/// taken from CHO code:
/// expedited requests within 72 hours and standard requests within 7 calendar
/// days (168 hours) of receipt (42 CFR 422.568(b)(1), 422.572(a); 438.210(d);
/// 45 CFR 156.223 for the QHP reporting parallel). One extension of up to 14
/// calendar days is provided for Medicare Advantage (422.568(b)(1)(i),
/// 422.572(b)(1)) and Medicaid managed care (438.210(d)(1)(ii), (d)(2)(ii)).
/// The deadline is inclusive: a decision at exactly the deadline is on time.
/// </summary>
public static class PaTimeframes
{
    public const double ExpeditedHours = 72;
    public const double StandardHours = 168;
    public static readonly TimeSpan MaxExtension = TimeSpan.FromDays(14);

    public static bool IsExpedited(string levelOfService) =>
        string.Equals(levelOfService, "U", StringComparison.OrdinalIgnoreCase);

    public static double BaseDeadlineHours(string levelOfService) =>
        IsExpedited(levelOfService) ? ExpeditedHours : StandardHours;

    public static bool AllowsExtension(PaLineOfBusiness lineOfBusiness) =>
        lineOfBusiness is PaLineOfBusiness.Medicare or PaLineOfBusiness.Medicaid;
}
