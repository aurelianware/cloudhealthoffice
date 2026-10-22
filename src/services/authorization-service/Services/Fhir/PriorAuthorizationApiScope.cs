using AuthorizationService.Models;
using AuthorizationService.Services.BenefitExclusion;

namespace AuthorizationService.Services.Fhir;

/// <summary>
/// Which prior authorizations CMS-0057-F puts inside the PA-data APIs
/// (Patient Access, Provider Access, Payer-to-Payer), and which it leaves out.
///
/// <para>
/// The rule is a property of the item, not of the plan: the regulation excludes
/// <em>drugs</em> from the APIs it requires, whatever a particular plan's
/// exclusion list happens to say. So this is a pure, deterministic test on the
/// record — no plan lookup, no network call, and therefore safe to apply to
/// every record of a page.
/// </para>
///
/// <para>
/// This is deliberately NOT <see cref="IDrugExclusionEvaluator"/>. That one
/// answers a different question — "did this plan's configured exclusions
/// exclude this request from medical PA at decision time" — and needs the
/// resolved plan to answer it. Both share
/// <see cref="DrugServiceCodeNormalizer"/>'s system parsing so "NDC" means the
/// same thing in each.
/// </para>
/// </summary>
public static class PriorAuthorizationApiScope
{
    /// <summary>278 UM03 service-type code for pharmacy.</summary>
    private const string PharmacyServiceTypeCode = "88";

    /// <summary>
    /// True when the record may be served through the CMS-0057-F PA-data APIs:
    /// it is not a drug authorization.
    /// </summary>
    public static bool IsInScope(Authorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        return !IsDrugAuthorization(authorization);
    }

    /// <summary>
    /// True when the record is a drug authorization, which CMS-0057-F excludes.
    /// A record counts as a drug authorization when it is a pharmacy-type
    /// request (278 UM03 = 88) or when ANY requested line is identified in a
    /// drug code system (NDC or RxNorm), or is an HCPCS J-code (the
    /// physician-administered drug range).
    ///
    /// <para>
    /// Any line, not every line: a record mixing a drug with a medical service
    /// still discloses the drug, and the regulation excludes drug data rather
    /// than drug-only records.
    /// </para>
    /// </summary>
    public static bool IsDrugAuthorization(Authorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);

        if (string.Equals(authorization.ServiceTypeCode, PharmacyServiceTypeCode, StringComparison.Ordinal))
            return true;

        foreach (var service in authorization.RequestedServices)
        {
            if (string.IsNullOrWhiteSpace(service.ProcedureCode)) continue;

            var system = DrugExclusionEvaluator.ParseSystem(service.ProductOrServiceSystem);
            if (system is DrugServiceCodeSystem.Ndc or DrugServiceCodeSystem.RxNorm)
                return true;

            if (IsHcpcsDrugCode(system, service.ProcedureCode))
                return true;
        }

        return false;
    }

    /// <summary>
    /// HCPCS Level II J-codes (J0000–J9999) are physician-administered drugs.
    /// Applied when the line names HCPCS explicitly, and when it names no
    /// system at all: a bare "J1745" is a drug whether or not the submitter
    /// labelled its code system, and excluding it is the fail-closed reading.
    /// A line that names a non-drug system explicitly is taken at its word.
    /// </summary>
    private static bool IsHcpcsDrugCode(DrugServiceCodeSystem system, string code)
    {
        if (system is not (DrugServiceCodeSystem.Hcpcs or DrugServiceCodeSystem.Unspecified))
            return false;

        var trimmed = code.Trim();
        if (trimmed.Length != 5) return false;
        if (trimmed[0] is not ('J' or 'j')) return false;

        for (var i = 1; i < trimmed.Length; i++)
        {
            if (!char.IsAsciiDigit(trimmed[i])) return false;
        }
        return true;
    }
}
