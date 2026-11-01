using ClaimsService.Models;
using ClaimsService.Services.Resolution;

namespace ClaimsService.Services.Adjudication.Tpl;

/// <summary>The kind of service a claim line is, for matching against a TPL resource's scope.</summary>
public enum TplServiceCategory
{
    InpatientHospital,
    OutpatientHospital,
    Professional,
    BehavioralHealth,
    Dme,
    Vision,
    Dental,
    LongTermCare,
}

/// <summary>
/// Pure rules: what a claim line is, and whether a TPL resource pays for it.
/// Kept apart from the stage so each state's cases test without a pipeline.
/// </summary>
public static class TplServiceRules
{
    /// <summary>Facility type codes (first two digits of the type of bill) for inpatient stays.</summary>
    private static readonly HashSet<string> InpatientFacilityTypes = ["11", "12", "18", "41", "81", "82"];

    /// <summary>Nursing facility and intermediate care.</summary>
    private static readonly HashSet<string> LongTermCareFacilityTypes = ["21", "22", "23", "65", "66"];

    public static TplServiceCategory Categorise(AdapterClaim claim, AdapterClaimLine line)
    {
        if (claim.ClaimType == ClaimType.Dental) return TplServiceCategory.Dental;

        if (claim.ClaimType == ClaimType.Institutional)
        {
            var facilityType = claim.Institutional?.FacilityTypeCode;
            if (facilityType is not null && LongTermCareFacilityTypes.Contains(facilityType))
                return TplServiceCategory.LongTermCare;
            if (facilityType is not null && InpatientFacilityTypes.Contains(facilityType))
                return TplServiceCategory.InpatientHospital;
            return TplServiceCategory.OutpatientHospital;
        }

        var code = (line.ProcedureCode ?? string.Empty).Trim().ToUpperInvariant();
        if (code.Length == 5)
        {
            // HCPCS E (DME), K (temporary DME), L (orthotics/prosthetics), A4-A9 (supplies).
            if (code[0] is 'E' or 'K' or 'L' || (code[0] == 'A' && code[1] is >= '4' and <= '9'))
                return TplServiceCategory.Dme;
            // V0-V2: vision materials; 92002-92499: ophthalmology services.
            if (code[0] == 'V' && code[1] is >= '0' and <= '2') return TplServiceCategory.Vision;
            if (code.All(char.IsDigit) && int.TryParse(code, out var cpt))
            {
                if (cpt is >= 92002 and <= 92499) return TplServiceCategory.Vision;
                if (cpt is >= 90785 and <= 90899) return TplServiceCategory.BehavioralHealth;
            }
            // HCPCS H0001-H2037: behavioral health and substance use.
            if (code[0] == 'H' && code.Skip(1).All(char.IsDigit)) return TplServiceCategory.BehavioralHealth;
        }
        return TplServiceCategory.Professional;
    }

    /// <summary>
    /// True when a resource with <paramref name="scope"/> pays for a service of
    /// <paramref name="category"/>. Medicare parts translate to what Medicare
    /// covers: A inpatient, SNF and hospice; B outpatient, professional and DME.
    /// Casualty covers only claims for an accident.
    /// </summary>
    public static bool Covers(TplServiceScope scope, TplServiceCategory category, bool accidentRelated)
    {
        var effective = scope & ~(TplServiceScope.MedicarePartA | TplServiceScope.MedicarePartB
                                  | TplServiceScope.MedicarePartC | TplServiceScope.MedicarePartD
                                  | TplServiceScope.Casualty);
        if (scope.HasFlag(TplServiceScope.MedicarePartC)) effective |= TplServiceScope.Medical;
        if (scope.HasFlag(TplServiceScope.MedicarePartD)) effective |= TplServiceScope.Pharmacy;
        if (scope.HasFlag(TplServiceScope.Casualty) && accidentRelated) effective |= TplServiceScope.Medical;

        var partA = scope.HasFlag(TplServiceScope.MedicarePartA);
        var partB = scope.HasFlag(TplServiceScope.MedicarePartB);

        return category switch
        {
            TplServiceCategory.InpatientHospital => effective.HasFlag(TplServiceScope.Hospital) || partA,
            TplServiceCategory.OutpatientHospital => effective.HasFlag(TplServiceScope.Hospital) || partB,
            TplServiceCategory.Professional => effective.HasFlag(TplServiceScope.Professional) || partB,
            TplServiceCategory.BehavioralHealth =>
                effective.HasFlag(TplServiceScope.BehavioralHealth) || effective.HasFlag(TplServiceScope.Professional) || partB,
            TplServiceCategory.Dme => effective.HasFlag(TplServiceScope.Dme) || partB,
            TplServiceCategory.Vision => effective.HasFlag(TplServiceScope.Vision),
            TplServiceCategory.Dental => effective.HasFlag(TplServiceScope.Dental),
            // Medicare A pays skilled nursing stays; custodial nursing facility
            // care is long-term care insurance's job.
            TplServiceCategory.LongTermCare => effective.HasFlag(TplServiceScope.LongTermCare) || partA,
            _ => false,
        };
    }

    /// <summary>The 837 says the claim is for an accident (CLM11 related causes or an accident date).</summary>
    public static bool IsAccidentRelated(AdapterClaim claim) =>
        claim.AccidentDate is not null
        || (claim.RelatedCausesCode is { } rc
            && rc.Split(':', ',', ' ').Any(c => c.Trim().ToUpperInvariant() is "AA" or "EM" or "OA"));

    /// <summary>Pediatric preventive visits; the age-banded codes are pediatric by definition.</summary>
    private static readonly HashSet<string> PediatricPreventiveVisitCodes =
        ["99381", "99382", "99383", "99384", "99391", "99392", "99393", "99394"];

    /// <summary>Preventive codes that are pediatric only when the member is.</summary>
    private static readonly HashSet<string> PreventiveCodesAnyAge =
    [
        "99385", "99395",                    // preventive visit 18-39
        "90460", "90461",                    // immunization administration with counselling (under 19)
        "90471", "90472", "90473", "90474",  // immunization administration
        "96110", "96127",                    // developmental / behavioral screening
        "99173", "99174", "99177",           // vision screening
        "92551", "92552",                    // hearing screening
        "36415", "83655",                    // venipuncture, lead screening
        "99188", "D1206",                    // fluoride varnish
    ];

    /// <summary>
    /// Pediatric preventive service (Social Security Act 1902(a)(25)(E):
    /// "preventive pediatric care, including EPSDT"). Federal law has Medicaid
    /// pay these and recover later. CHO's code list is a starting point;
    /// states add theirs (Texas THSteps checkups, Medi-Cal CHDP successor codes)
    /// in the state rule tables.
    /// </summary>
    public static bool IsPediatricPreventive(AdapterClaim claim, AdapterClaimLine line, DateTime? dateOfBirth, int ageLimit)
    {
        var code = (line.ProcedureCode ?? string.Empty).Trim().ToUpperInvariant();
        if (PediatricPreventiveVisitCodes.Contains(code)) return true;

        var serviceDate = line.ServiceDateFrom != default ? line.ServiceDateFrom : claim.ServiceDateFrom;
        if (dateOfBirth is not { } dob || AgeOn(dob, serviceDate) >= ageLimit) return false;
        if (PreventiveCodesAnyAge.Contains(code)) return true;

        // Well-child and immunization encounters by diagnosis.
        return claim.DiagnosisCodes.Any(d =>
        {
            var dx = d.Code.Replace(".", string.Empty).ToUpperInvariant();
            return dx.StartsWith("Z0012", StringComparison.Ordinal)
                   || dx.StartsWith("Z0011", StringComparison.Ordinal)
                   || dx == "Z23";
        }) && (code.StartsWith("99", StringComparison.Ordinal) || code.StartsWith("904", StringComparison.Ordinal));
    }

    public static int AgeOn(DateTime dateOfBirth, DateTime date)
    {
        var age = date.Year - dateOfBirth.Year;
        if (date.Date < dateOfBirth.Date.AddYears(age)) age--;
        return age;
    }
}
