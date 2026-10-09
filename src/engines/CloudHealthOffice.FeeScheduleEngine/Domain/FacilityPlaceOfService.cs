using System.Collections.Frozen;

namespace CloudHealthOffice.FeeScheduleEngine.Domain;

/// <summary>
/// The single definition of which CMS place-of-service (POS) codes take the
/// <b>facility</b> rate under the Medicare Physician Fee Schedule (the site-of-service
/// payment differential: facility PE RVUs, or a flat line's
/// <c>FeeScheduleLine.FacilityRate</c>). Every other POS, including blank and unknown
/// codes, takes the non-facility rate.
///
/// <para><b>Source.</b> Medicare Claims Processing Manual, Pub. 100-04, Chapter 12,
/// §20.4.2 "Site of Service Payment Differential" (Rev. 12823, effective 2024-10-08),
/// which lists the facility settings below and names every other POS — including
/// 01, 03, 04, 09, 10, 11, 12–17, 20, 25, 27, 32, 33, 49, 50, 54, 55, 57, 58, 60, 62,
/// 65, 71, 72, 81 and 99 — as paid at the non-facility rate. Code meanings follow the
/// CMS Place of Service Code Set.</para>
///
/// <para><b>Telehealth (02 / 10).</b> From CY 2024 (CY 2024 MPFS final rule; MLN
/// MM13452) POS 02 "telehealth provided other than in patient's home" is paid at the
/// facility rate and POS 10 "telehealth provided in patient's home" at the
/// non-facility rate. POS 10 did not exist before 2022, and for CY 2022–2023 Medicare
/// telehealth claims were generally billed with the in-person POS plus modifier 95, so the
/// classification is not date-dependent: 10 is non-facility for every date of service.</para>
///
/// <para><b>Not modelled here.</b> §20.4.2 also says the professional component of a
/// diagnostic test has one rate in every setting, and outpatient therapy and CORF
/// services always take the non-facility rate. Those are code-level properties carried
/// by the fee schedule (equal facility and non-facility values), not POS rules. A POS
/// 24 (ASC) or 31 (SNF, Part A resident) distinction finer than the POS code is
/// likewise not visible on the claim and is not modelled.</para>
///
/// <para>Commercial contracts that pay one rate everywhere are unaffected: a line with
/// no facility price prices at its single rate whatever this returns.</para>
/// </summary>
public static class FacilityPlaceOfService
{
    /// <summary>POS codes paid at the MPFS facility rate (Pub. 100-04, Ch. 12, §20.4.2).</summary>
    public static readonly FrozenSet<string> Codes = new[]
    {
        "02", // Telehealth provided other than in patient's home
        "19", // Off campus - outpatient hospital
        "21", // Inpatient hospital
        "22", // On campus - outpatient hospital
        "23", // Emergency room - hospital
        "24", // Ambulatory surgical center
        "26", // Military treatment facility
        "31", // Skilled nursing facility (Part A resident)
        "34", // Hospice (inpatient care)
        "41", // Ambulance - land
        "42", // Ambulance - air or water
        "51", // Inpatient psychiatric facility
        "52", // Psychiatric facility - partial hospitalization
        "53", // Community mental health center
        "56", // Psychiatric residential treatment center
        "61", // Comprehensive inpatient rehabilitation facility
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// True when <paramref name="placeOfServiceCode"/> takes the facility rate.
    /// Blank, unknown and every unlisted code (for example 10, 11, 12, 20, 49, 81, 99)
    /// take the non-facility rate. Surrounding whitespace is ignored.
    /// </summary>
    public static bool IsFacility(string? placeOfServiceCode)
        => !string.IsNullOrWhiteSpace(placeOfServiceCode) && Codes.Contains(placeOfServiceCode.Trim());
}
