namespace CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;

/// <summary>
/// Provides reference data (code sets, provider lists, etc.) for claim generation.
/// Abstracted to allow future connection to the CHO TerminologyService.
/// </summary>
public interface IReferenceDataProvider
{
    /// <summary>Returns a random ICD-10 diagnosis code appropriate for the given claim context.</summary>
    string GetDiagnosisCode(Random random, string context = "general");

    /// <summary>Returns a set of secondary diagnosis codes.</summary>
    IReadOnlyList<string> GetSecondaryDiagnosisCodes(Random random, int count);

    /// <summary>Returns a CPT/HCPCS procedure code for the given sub-type.</summary>
    (string Code, string Description, decimal BaseCharge) GetProcedureCode(Random random, string subType);

    /// <summary>Returns a CDT dental procedure code for the given category.</summary>
    (string Code, string Description, decimal BaseCharge) GetDentalCode(Random random, string category);

    /// <summary>Returns a UB-04 revenue code for the given institutional sub-type.</summary>
    (string Code, string Description) GetRevenueCode(Random random, string subType);

    /// <summary>Returns a set of modifiers for the given modifier scenario.</summary>
    IReadOnlyList<string> GetModifiers(Random random, string scenario);

    /// <summary>Returns a random DRG code with base rate.</summary>
    (string Code, string Description, decimal BaseRate) GetDrgCode(Random random);

    /// <summary>Returns a random valid place of service code.</summary>
    string GetPlaceOfServiceCode(Random random, string claimSubType);

    /// <summary>Returns common benefit plan identifiers.</summary>
    string GetBenefitPlanId(Random random);

    /// <summary>Generates a synthetic member.</summary>
    Models.SyntheticMember GenerateMember(Random random);

    /// <summary>Generates a synthetic provider.</summary>
    Models.SyntheticProvider GenerateProvider(Random random, string specialty = "general");
}
