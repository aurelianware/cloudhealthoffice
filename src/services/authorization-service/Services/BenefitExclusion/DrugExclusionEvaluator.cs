using AuthorizationService.Models;

namespace AuthorizationService.Services.BenefitExclusion;

/// <summary>
/// Pure domain evaluation of whether a request is excluded by a resolved set of
/// benefit-plan exclusions. Has no external dependencies, so the decision is
/// deterministic and reproducible from its inputs (the request + the plan
/// exclusions). The composing <see cref="IAuthorizationExclusionService"/>
/// resolves the plan and calls this.
/// </summary>
public interface IDrugExclusionEvaluator
{
    BenefitExclusionDetermination Evaluate(
        Authorization authorization, IReadOnlyList<Models.BenefitExclusion> applicableExclusions);
}

public sealed class DrugExclusionEvaluator : IDrugExclusionEvaluator
{
    /// <summary>278 UM03 service-type code for pharmacy.</summary>
    private const string PharmacyServiceTypeCode = "88";

    public BenefitExclusionDetermination Evaluate(
        Authorization authorization, IReadOnlyList<Models.BenefitExclusion> applicableExclusions)
    {
        if (applicableExclusions.Count == 0)
            return BenefitExclusionDetermination.NotExcluded;

        // Build the requested drug/service identities (deterministic order:
        // the pharmacy service-type signal first, then each requested line).
        foreach (var (system, code) in RequestedCandidates(authorization))
        {
            foreach (var exclusion in applicableExclusions)
            {
                if (DrugServiceCodeNormalizer.Matches(exclusion.CodeSystem, exclusion.Code, system, code))
                {
                    return new BenefitExclusionDetermination
                    {
                        IsExcluded = true,
                        MatchedExclusion = exclusion,
                        RequestedCode = code,
                        NormalizedCode = DrugServiceCodeNormalizer.Normalize(system, code),
                    };
                }
            }
        }

        return BenefitExclusionDetermination.NotExcluded;
    }

    private static IEnumerable<(DrugServiceCodeSystem System, string Code)> RequestedCandidates(
        Authorization authorization)
    {
        // A pharmacy-type request (278 UM03 = 88) is itself a drug identity a
        // plan may exclude from the medical PA scope.
        if (string.Equals(authorization.ServiceTypeCode, PharmacyServiceTypeCode, StringComparison.Ordinal))
            yield return (DrugServiceCodeSystem.ServiceType, PharmacyServiceTypeCode);

        foreach (var service in authorization.RequestedServices)
        {
            if (string.IsNullOrWhiteSpace(service.ProcedureCode)) continue;
            yield return (ParseSystem(service.ProductOrServiceSystem), service.ProcedureCode);
        }
    }

    /// <summary>
    /// Maps a request's code-system hint (a short token or a FHIR system URI) to
    /// the internal enum. Unknown or absent hints resolve to Unspecified, which
    /// still matches an exclusion by code alone.
    /// </summary>
    internal static DrugServiceCodeSystem ParseSystem(string? system)
    {
        if (string.IsNullOrWhiteSpace(system)) return DrugServiceCodeSystem.Unspecified;

        var s = system.Trim().ToLowerInvariant();
        return s switch
        {
            "ndc" or "http://hl7.org/fhir/sid/ndc" => DrugServiceCodeSystem.Ndc,
            "rxnorm" or "http://www.nlm.nih.gov/research/umls/rxnorm" => DrugServiceCodeSystem.RxNorm,
            "hcpcs" or "j-code" or "jcode" or "https://bluebutton.cms.gov/resources/codesystem/hcpcs"
                or "urn:oid:2.16.840.1.113883.6.285" => DrugServiceCodeSystem.Hcpcs,
            "cpt" or "http://www.ama-assn.org/go/cpt" => DrugServiceCodeSystem.Cpt,
            "servicetype" or "service-type" => DrugServiceCodeSystem.ServiceType,
            _ => DrugServiceCodeSystem.Unspecified,
        };
    }
}
