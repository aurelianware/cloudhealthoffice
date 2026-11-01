namespace PremiumBillingService.StateCapitation;

/// <summary>Where a state's 820 puts a member-month's rate cell.</summary>
public enum RateCellSource
{
    /// <summary>A REF after the RMR (2300B) with <see cref="StateCapitationProfile.RateCellQualifier"/>.</summary>
    Reference = 1,

    /// <summary>RMR02 itself, when RMR01 is <see cref="StateCapitationProfile.RateCellQualifier"/>.</summary>
    RemittanceReference = 2,
}

/// <summary>
/// How one state's Medicaid capitation 820 and rate sheet are read: which
/// segment carries the rate cell, how a partial month is paid, and how far
/// apart expected and received may be before a member-month is a variance.
///
/// <para>
/// The built-in profiles are starting points, not companion-guide facts:
/// <see cref="Verified"/> is false until a plan checks them against the
/// state's current 820 companion guide and a real file. A plan overrides any
/// of it in its own configuration.
/// </para>
/// </summary>
public sealed record StateCapitationProfile
{
    /// <summary>Two-letter state code.</summary>
    public required string State { get; init; }

    /// <summary>Program label for reports (e.g. "Texas Medicaid managed care", "Medi-Cal managed care").</summary>
    public required string Program { get; init; }

    public RateCellSource RateCellSource { get; init; } = RateCellSource.Reference;

    /// <summary>REF01 (or RMR01) qualifier whose value is the rate cell.</summary>
    public required string RateCellQualifier { get; init; }

    /// <summary>
    /// Expected pay for a member enrolled part of a month: false pays the full
    /// month (the default), true prorates by days enrolled.
    /// </summary>
    public bool ProrateByDays { get; init; }

    /// <summary>A member-month whose received amount is within this of expected is paid in full.</summary>
    public decimal Tolerance { get; init; } = 0.01m;

    /// <summary>Whether the qualifiers above were checked against the state's companion guide and a real file.</summary>
    public bool Verified { get; init; }

    /// <summary>
    /// Texas HHSC managed care (STAR, STAR+PLUS, STAR Kids, STAR Health, CHIP).
    /// Rate cells are the HHSC premium rate groups (program, service area,
    /// risk group). The REF qualifier is a placeholder until checked against
    /// the HHSC 820 companion guide. Unverified.
    /// </summary>
    public static StateCapitationProfile Texas { get; } = new()
    {
        State = "TX",
        Program = "Texas Medicaid and CHIP managed care",
        RateCellSource = RateCellSource.Reference,
        RateCellQualifier = "ZZ",
        ProrateByDays = false,
    };

    /// <summary>
    /// California DHCS Medi-Cal managed care. Rate cells are the DHCS rate
    /// categories (county or rating region with aid code group, e.g. Child,
    /// Adult, ACA expansion, SPD, LTC). The REF qualifier is a placeholder
    /// until checked against the DHCS 820 companion guide. Unverified.
    /// </summary>
    public static StateCapitationProfile California { get; } = new()
    {
        State = "CA",
        Program = "Medi-Cal managed care",
        RateCellSource = RateCellSource.Reference,
        RateCellQualifier = "ZZ",
        ProrateByDays = false,
    };

    /// <summary>The built-in profile for a state, or null.</summary>
    public static StateCapitationProfile? ForState(string state) => state.ToUpperInvariant() switch
    {
        "TX" => Texas,
        "CA" => California,
        _ => null,
    };
}
