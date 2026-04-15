using CloudHealthOffice.BenchmarkClaimGenerator.Models;

namespace CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;

/// <summary>
/// Realistic Texas Medicaid benefit plan templates for synthetic data generation.
/// Texas Medicaid has zero cost-sharing for most programs; CHIP has nominal copays.
/// </summary>
public static class BenefitPlanTemplates
{
    /// <summary>Generate all standard benefit plan templates.</summary>
    public static List<SyntheticBenefitPlan> CreateAll(DateTime effectiveDate)
    {
        return new List<SyntheticBenefitPlan>
        {
            CreateStarAdult(effectiveDate),
            CreateStarChild(effectiveDate),
            CreateChipPlanA(effectiveDate),
            CreateChipPlanB(effectiveDate),
            CreateStarPlusCommunity(effectiveDate),
            CreateStarPlusHcbs(effectiveDate),
            CreateStarKids(effectiveDate),
            CreateStarHealth(effectiveDate),
            CreateDentalChip(effectiveDate),
            CreateVisionChip(effectiveDate),
        };
    }

    /// <summary>STAR Adult — Full Medicaid, no cost sharing.</summary>
    public static SyntheticBenefitPlan CreateStarAdult(DateTime effectiveDate) => new()
    {
        PlanId = "PLN-STAR-ADULT-001",
        PlanName = "STAR Adult",
        PlanType = "Medicaid",
        LineOfBusiness = "Medicaid",
        MedicaidProgram = "STAR",
        EffectiveDate = effectiveDate,
        IndividualDeductible = 0m,
        FamilyDeductible = 0m,
        IndividualOopMax = 0m,
        FamilyOopMax = 0m,
        PcpCopay = 0m,
        SpecialistCopay = 0m,
        ErCopay = 0m,
        InpatientCopay = 0m,
        CoinsurancePercent = 0m,
        RequiresPcpReferral = true,
        Benefits = CreateMedicaidBenefits(0m, 0m, 0m, 0m),
    };

    /// <summary>STAR Child — Full Medicaid, no cost sharing.</summary>
    public static SyntheticBenefitPlan CreateStarChild(DateTime effectiveDate) => new()
    {
        PlanId = "PLN-STAR-CHILD-001",
        PlanName = "STAR Child",
        PlanType = "Medicaid",
        LineOfBusiness = "Medicaid",
        MedicaidProgram = "STAR",
        EffectiveDate = effectiveDate,
        IndividualDeductible = 0m,
        FamilyDeductible = 0m,
        IndividualOopMax = 0m,
        FamilyOopMax = 0m,
        PcpCopay = 0m,
        SpecialistCopay = 0m,
        ErCopay = 0m,
        InpatientCopay = 0m,
        CoinsurancePercent = 0m,
        RequiresPcpReferral = true,
        Benefits = CreateMedicaidBenefits(0m, 0m, 0m, 0m),
    };

    /// <summary>CHIP Plan A — Nominal copays.</summary>
    public static SyntheticBenefitPlan CreateChipPlanA(DateTime effectiveDate) => new()
    {
        PlanId = "PLN-CHIP-A-001",
        PlanName = "CHIP Plan A",
        PlanType = "Medicaid",
        LineOfBusiness = "Medicaid",
        MedicaidProgram = "CHIP",
        EffectiveDate = effectiveDate,
        IndividualDeductible = 0m,
        FamilyDeductible = 0m,
        IndividualOopMax = 0m,
        FamilyOopMax = 0m,
        PcpCopay = 5m,
        SpecialistCopay = 10m,
        ErCopay = 50m,
        InpatientCopay = 0m,
        CoinsurancePercent = 0m,
        RequiresPcpReferral = false,
        Benefits = CreateMedicaidBenefits(5m, 10m, 50m, 0m),
    };

    /// <summary>CHIP Plan B — Higher copays.</summary>
    public static SyntheticBenefitPlan CreateChipPlanB(DateTime effectiveDate) => new()
    {
        PlanId = "PLN-CHIP-B-001",
        PlanName = "CHIP Plan B",
        PlanType = "Medicaid",
        LineOfBusiness = "Medicaid",
        MedicaidProgram = "CHIP",
        EffectiveDate = effectiveDate,
        IndividualDeductible = 0m,
        FamilyDeductible = 0m,
        IndividualOopMax = 0m,
        FamilyOopMax = 0m,
        PcpCopay = 10m,
        SpecialistCopay = 20m,
        ErCopay = 75m,
        InpatientCopay = 0m,
        InpatientPerDiem = 100m,
        CoinsurancePercent = 0m,
        RequiresPcpReferral = false,
        Benefits = CreateMedicaidBenefits(10m, 20m, 75m, 100m),
    };

    /// <summary>STAR+PLUS Community — No cost sharing.</summary>
    public static SyntheticBenefitPlan CreateStarPlusCommunity(DateTime effectiveDate) => new()
    {
        PlanId = "PLN-SPLUS-COMM-001",
        PlanName = "STAR+PLUS Community",
        PlanType = "Medicaid",
        LineOfBusiness = "Medicaid",
        MedicaidProgram = "STAR+PLUS",
        EffectiveDate = effectiveDate,
        IndividualDeductible = 0m,
        FamilyDeductible = 0m,
        IndividualOopMax = 0m,
        FamilyOopMax = 0m,
        PcpCopay = 0m,
        SpecialistCopay = 0m,
        ErCopay = 0m,
        InpatientCopay = 0m,
        CoinsurancePercent = 0m,
        RequiresPcpReferral = true,
        Benefits = CreateMedicaidBenefits(0m, 0m, 0m, 0m),
    };

    /// <summary>STAR+PLUS HCBS — Home and community-based services, no cost sharing.</summary>
    public static SyntheticBenefitPlan CreateStarPlusHcbs(DateTime effectiveDate) => new()
    {
        PlanId = "PLN-SPLUS-HCBS-001",
        PlanName = "STAR+PLUS HCBS",
        PlanType = "Medicaid",
        LineOfBusiness = "Medicaid",
        MedicaidProgram = "STAR+PLUS",
        EffectiveDate = effectiveDate,
        IndividualDeductible = 0m,
        FamilyDeductible = 0m,
        IndividualOopMax = 0m,
        FamilyOopMax = 0m,
        PcpCopay = 0m,
        SpecialistCopay = 0m,
        ErCopay = 0m,
        InpatientCopay = 0m,
        CoinsurancePercent = 0m,
        RequiresPcpReferral = true,
        Benefits = CreateMedicaidBenefits(0m, 0m, 0m, 0m),
    };

    /// <summary>STAR Kids — No cost sharing.</summary>
    public static SyntheticBenefitPlan CreateStarKids(DateTime effectiveDate) => new()
    {
        PlanId = "PLN-SKIDS-001",
        PlanName = "STAR Kids",
        PlanType = "Medicaid",
        LineOfBusiness = "Medicaid",
        MedicaidProgram = "STAR Kids",
        EffectiveDate = effectiveDate,
        IndividualDeductible = 0m,
        FamilyDeductible = 0m,
        IndividualOopMax = 0m,
        FamilyOopMax = 0m,
        PcpCopay = 0m,
        SpecialistCopay = 0m,
        ErCopay = 0m,
        InpatientCopay = 0m,
        CoinsurancePercent = 0m,
        RequiresPcpReferral = true,
        Benefits = CreateMedicaidBenefits(0m, 0m, 0m, 0m),
    };

    /// <summary>STAR Health (foster care) — No cost sharing.</summary>
    public static SyntheticBenefitPlan CreateStarHealth(DateTime effectiveDate) => new()
    {
        PlanId = "PLN-SHEALTH-001",
        PlanName = "STAR Health",
        PlanType = "Medicaid",
        LineOfBusiness = "Medicaid",
        MedicaidProgram = "STAR Health",
        EffectiveDate = effectiveDate,
        IndividualDeductible = 0m,
        FamilyDeductible = 0m,
        IndividualOopMax = 0m,
        FamilyOopMax = 0m,
        PcpCopay = 0m,
        SpecialistCopay = 0m,
        ErCopay = 0m,
        InpatientCopay = 0m,
        CoinsurancePercent = 0m,
        RequiresPcpReferral = true,
        Benefits = CreateMedicaidBenefits(0m, 0m, 0m, 0m),
    };

    /// <summary>Dental CHIP — Dental-only plan with annual max.</summary>
    public static SyntheticBenefitPlan CreateDentalChip(DateTime effectiveDate) => new()
    {
        PlanId = "PLN-DENTAL-CHIP-001",
        PlanName = "Dental CHIP",
        PlanType = "Medicaid",
        LineOfBusiness = "Medicaid",
        MedicaidProgram = "CHIP",
        EffectiveDate = effectiveDate,
        IndividualDeductible = 0m,
        IndividualOopMax = 150m,
        PcpCopay = 0m,
        SpecialistCopay = 0m,
        ErCopay = 0m,
        InpatientCopay = 0m,
        CoinsurancePercent = 0m,
        DentalAnnualMax = 150m,
        Benefits = new List<SyntheticBenefit>
        {
            new() { ServiceCategory = "Preventive Dental", InNetworkCopay = 0m, DeductibleApplies = false },
            new() { ServiceCategory = "Restorative Dental", InNetworkCopay = 0m, DeductibleApplies = false },
            new() { ServiceCategory = "Orthodontics", InNetworkCopay = 0m, PriorAuthRequired = true },
        },
    };

    /// <summary>Vision CHIP — Vision-only plan with annual max.</summary>
    public static SyntheticBenefitPlan CreateVisionChip(DateTime effectiveDate) => new()
    {
        PlanId = "PLN-VISION-CHIP-001",
        PlanName = "Vision CHIP",
        PlanType = "Medicaid",
        LineOfBusiness = "Medicaid",
        MedicaidProgram = "CHIP",
        EffectiveDate = effectiveDate,
        IndividualDeductible = 0m,
        IndividualOopMax = 200m,
        PcpCopay = 0m,
        SpecialistCopay = 0m,
        ErCopay = 0m,
        InpatientCopay = 0m,
        CoinsurancePercent = 0m,
        VisionAnnualMax = 200m,
        Benefits = new List<SyntheticBenefit>
        {
            new() { ServiceCategory = "Vision Exam", InNetworkCopay = 0m, DeductibleApplies = false },
            new() { ServiceCategory = "Eyeglasses/Lenses", InNetworkCopay = 0m, DeductibleApplies = false },
        },
    };

    /// <summary>LOB distribution for member assignment: program name → weight.</summary>
    public static readonly (string Program, string PlanId, double Weight)[] LobDistribution =
    {
        ("STAR", "PLN-STAR-ADULT-001", 0.30),
        ("STAR", "PLN-STAR-CHILD-001", 0.20),
        ("CHIP", "PLN-CHIP-A-001", 0.10),
        ("CHIP", "PLN-CHIP-B-001", 0.10),
        ("STAR+PLUS", "PLN-SPLUS-COMM-001", 0.10),
        ("STAR+PLUS", "PLN-SPLUS-HCBS-001", 0.05),
        ("STAR Kids", "PLN-SKIDS-001", 0.10),
        ("STAR Health", "PLN-SHEALTH-001", 0.05),
    };

    /// <summary>Select a plan based on LOB distribution weights.</summary>
    public static (string Program, string PlanId) SelectByLobDistribution(Random random)
    {
        var roll = random.NextDouble();
        var cumulative = 0.0;
        foreach (var (program, planId, weight) in LobDistribution)
        {
            cumulative += weight;
            if (roll < cumulative)
                return (program, planId);
        }
        return (LobDistribution[^1].Program, LobDistribution[^1].PlanId);
    }

    private static List<SyntheticBenefit> CreateMedicaidBenefits(
        decimal pcpCopay, decimal specialistCopay, decimal erCopay, decimal inpatientPerDiem)
    {
        return new List<SyntheticBenefit>
        {
            new()
            {
                ServiceCategory = "Office Visit - PCP",
                InNetworkCopay = pcpCopay,
                DeductibleApplies = false,
                PriorAuthRequired = false,
            },
            new()
            {
                ServiceCategory = "Office Visit - Specialist",
                InNetworkCopay = specialistCopay,
                DeductibleApplies = false,
                PriorAuthRequired = false,
            },
            new()
            {
                ServiceCategory = "Emergency Room",
                InNetworkCopay = erCopay,
                DeductibleApplies = false,
                PriorAuthRequired = false,
            },
            new()
            {
                ServiceCategory = "Inpatient",
                InNetworkCopay = inpatientPerDiem,
                Description = inpatientPerDiem > 0 ? $"${inpatientPerDiem}/day" : "$0",
                DeductibleApplies = false,
                PriorAuthRequired = true,
            },
            new()
            {
                ServiceCategory = "Outpatient Surgery",
                InNetworkCopay = 0m,
                DeductibleApplies = false,
                PriorAuthRequired = true,
            },
            new()
            {
                ServiceCategory = "Lab/Pathology",
                InNetworkCopay = 0m,
                DeductibleApplies = false,
                PriorAuthRequired = false,
            },
            new()
            {
                ServiceCategory = "Behavioral Health",
                InNetworkCopay = pcpCopay,
                DeductibleApplies = false,
                PriorAuthRequired = false,
            },
            new()
            {
                ServiceCategory = "Preventive Care",
                InNetworkCopay = 0m,
                DeductibleApplies = false,
                PriorAuthRequired = false,
            },
        };
    }
}
