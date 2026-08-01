using System.Text.Json.Serialization;
using BenefitPlanService.Models.Benefits;
using BenefitRulePredicate = CloudHealthOffice.BenefitEngine.Domain.BenefitRulePredicate;

namespace BenefitPlanService.Models;

/// <summary>
/// Vendor-neutral response envelope returned by <see cref="Adapters.IBenefitPlanAdapter.GetPlanAsync"/>
/// and <see cref="Adapters.IBenefitPlanAdapter.GetPlanVersionAsync"/>.
///
/// <para>
/// The payload <see cref="Plan"/> is shaped to project cleanly onto a future
/// FHIR <c>InsurancePlan</c> resource (Section 5.8): the effective/termination
/// pair maps to <c>InsurancePlan.period</c>, plan-level cost-sharing maps to
/// <c>InsurancePlan.plan.specificCost</c>, network tiers map to
/// <c>InsurancePlan.network</c>, and document references map to
/// contained <c>DocumentReference</c> resources.
/// </para>
/// </summary>
public class BenefitPlanAdapterResponse
{
    /// <summary>Adapter that produced the response (e.g. "cho", "qnxt").</summary>
    public string Platform { get; set; } = string.Empty;

    /// <summary>Optional raw vendor response retained for audit / debugging.</summary>
    public string? RawResponse { get; set; }

    /// <summary>Plan payload. Null when the requested plan/version is not found.</summary>
    public AdapterBenefitPlan? Plan { get; set; }
}

/// <summary>
/// Vendor-neutral response envelope for <see cref="Adapters.IBenefitPlanAdapter.GetMemberBenefitViewAsync"/>.
/// </summary>
public class MemberBenefitViewAdapterResponse
{
    public string Platform { get; set; } = string.Empty;
    public string? RawResponse { get; set; }

    /// <summary>Member view payload. Null when the requested plan is not found.</summary>
    public AdapterMemberBenefitView? View { get; set; }
}

/// <summary>
/// Normalized plan DTO. Field shape mirrors <see cref="BenefitPlan"/> so the
/// CHO pass-through is lossless; round-trip mappers <see cref="From"/> and
/// <see cref="ToBenefitPlan"/> let the controller return the existing wire format.
/// </summary>
public class AdapterBenefitPlan
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string PlanId { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string Payer { get; set; } = string.Empty;
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public PlanType PlanType { get; set; }
    public MetalLevel? MetalLevel { get; set; }
    public LineOfBusiness LineOfBusiness { get; set; } = LineOfBusiness.Commercial;
    public FamilyAccumulatorModel FamilyAccumulatorModel { get; set; } = FamilyAccumulatorModel.Embedded;

    public List<AdapterBenefit> Benefits { get; set; } = new();
    public List<AdapterNetworkTier> NetworkTiers { get; set; } = new();
    public AdapterCostSharing CostSharing { get; set; } = new();
    public List<AdapterPlanDocumentReference> Documents { get; set; } = new();

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    // Version-chain identity (5.1)
    public string VersionId { get; set; } = string.Empty;
    public int VersionNumber { get; set; }
    public PlanVersionState VersionState { get; set; }
    public string? PredecessorVersionId { get; set; }
    public DateTime? PublishedAt { get; set; }
    public string? PublishedBy { get; set; }
    public DateTime? SupersededAt { get; set; }
    public string? SupersededByVersionId { get; set; }

    public static AdapterBenefitPlan From(BenefitPlan src) => new()
    {
        Id = src.Id,
        TenantId = src.TenantId,
        PlanId = src.PlanId,
        PlanName = src.PlanName,
        Payer = src.Payer,
        EffectiveDate = src.EffectiveDate,
        TerminationDate = src.TerminationDate,
        PlanType = src.PlanType,
        MetalLevel = src.MetalLevel,
        LineOfBusiness = src.LineOfBusiness,
        FamilyAccumulatorModel = src.FamilyAccumulatorModel,
        Benefits = src.Benefits.Select(AdapterBenefit.From).ToList(),
        NetworkTiers = src.NetworkTiers.Select(AdapterNetworkTier.From).ToList(),
        CostSharing = AdapterCostSharing.From(src.CostSharing),
        Documents = src.Documents.Select(AdapterPlanDocumentReference.From).ToList(),
        CreatedAt = src.CreatedAt,
        UpdatedAt = src.UpdatedAt,
        CreatedDate = src.CreatedDate,
        ModifiedDate = src.ModifiedDate,
        CreatedBy = src.CreatedBy,
        IsActive = src.IsActive,
        VersionId = src.VersionId,
        VersionNumber = src.VersionNumber,
        VersionState = src.VersionState,
        PredecessorVersionId = src.PredecessorVersionId,
        PublishedAt = src.PublishedAt,
        PublishedBy = src.PublishedBy,
        SupersededAt = src.SupersededAt,
        SupersededByVersionId = src.SupersededByVersionId,
    };

    public BenefitPlan ToBenefitPlan() => new()
    {
        Id = Id,
        TenantId = TenantId,
        PlanId = PlanId,
        PlanName = PlanName,
        Payer = Payer,
        EffectiveDate = EffectiveDate,
        TerminationDate = TerminationDate,
        PlanType = PlanType,
        MetalLevel = MetalLevel,
        LineOfBusiness = LineOfBusiness,
        FamilyAccumulatorModel = FamilyAccumulatorModel,
        Benefits = Benefits.Select(b => b.ToBenefit()).ToList(),
        NetworkTiers = NetworkTiers.Select(n => n.ToNetworkTier()).ToList(),
        CostSharing = CostSharing.ToCostSharing(),
        Documents = Documents.Select(d => d.ToPlanDocumentReference()).ToList(),
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        CreatedDate = CreatedDate,
        ModifiedDate = ModifiedDate,
        CreatedBy = CreatedBy,
        IsActive = IsActive,
        VersionId = VersionId,
        VersionNumber = VersionNumber,
        VersionState = VersionState,
        PredecessorVersionId = PredecessorVersionId,
        PublishedAt = PublishedAt,
        PublishedBy = PublishedBy,
        SupersededAt = SupersededAt,
        SupersededByVersionId = SupersededByVersionId,
    };
}

/// <summary>
/// Vendor-neutral benefit DTO. Mirrors the discriminated-union shape of
/// <see cref="Benefit"/> so external adapters (today CHO; tomorrow QNXT,
/// Facets, HealthEdge) can populate the typed facets that line up with
/// each vendor's API. <see cref="From"/> dispatches on the runtime type of
/// the source <see cref="Benefit"/>; <see cref="ToBenefit"/> reconstructs
/// the matching subclass.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "benefitType",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToBaseType,
    IgnoreUnrecognizedTypeDiscriminators = true)]
[JsonDerivedType(typeof(AdapterMedicalBenefit), BenefitTypeDiscriminators.Medical)]
[JsonDerivedType(typeof(AdapterDentalBenefit), BenefitTypeDiscriminators.Dental)]
[JsonDerivedType(typeof(AdapterPharmacyBenefit), BenefitTypeDiscriminators.Pharmacy)]
[JsonDerivedType(typeof(AdapterBehavioralHealthBenefit), BenefitTypeDiscriminators.BehavioralHealth)]
[JsonDerivedType(typeof(AdapterVisionBenefit), BenefitTypeDiscriminators.Vision)]
[JsonDerivedType(typeof(AdapterDMEBenefit), BenefitTypeDiscriminators.DME)]
[JsonDerivedType(typeof(AdapterMaternityBenefit), BenefitTypeDiscriminators.Maternity)]
[JsonDerivedType(typeof(AdapterPreventiveBenefit), BenefitTypeDiscriminators.Preventive)]
public class AdapterBenefit
{
    public string Id { get; set; } = string.Empty;
    public string ServiceCategory { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsCovered { get; set; } = true;
    public List<string> CptCodes { get; set; } = new();
    public decimal? InNetworkCopay { get; set; }
    public decimal? OutNetworkCopay { get; set; }
    public decimal? InNetworkCoinsurance { get; set; }
    public decimal? OutNetworkCoinsurance { get; set; }
    public bool DeductibleApplies { get; set; } = true;
    public bool OopApplies { get; set; } = true;
    public bool PriorAuthRequired { get; set; }
    public decimal? CopayAmount { get; set; }
    public decimal? CoinsurancePercentage { get; set; }
    public bool RequiresPriorAuth { get; set; }
    public int? VisitLimit { get; set; }
    public string? VisitLimitPeriod { get; set; }
    public string? Limitations { get; set; }
    public decimal? AnnualMaximum { get; set; }
    public decimal? LifetimeMaximum { get; set; }
    public List<BenefitRulePredicate>? Rules { get; set; }

    /// <summary>
    /// Dispatch on the runtime type of <paramref name="b"/> and return the
    /// matching adapter subclass with all common-plus-typed facets copied.
    /// A base-class <see cref="Benefit"/> (legacy / pre-5.4 shape) maps to
    /// <see cref="AdapterMedicalBenefit"/>.
    /// </summary>
    public static AdapterBenefit From(Benefit b) => b switch
    {
        DentalBenefit dental => AdapterDentalBenefit.FromTyped(dental),
        PharmacyBenefit pharmacy => AdapterPharmacyBenefit.FromTyped(pharmacy),
        BehavioralHealthBenefit bh => AdapterBehavioralHealthBenefit.FromTyped(bh),
        VisionBenefit vision => AdapterVisionBenefit.FromTyped(vision),
        DMEBenefit dme => AdapterDMEBenefit.FromTyped(dme),
        MaternityBenefit maternity => AdapterMaternityBenefit.FromTyped(maternity),
        PreventiveBenefit preventive => AdapterPreventiveBenefit.FromTyped(preventive),
        MedicalBenefit medical => AdapterMedicalBenefit.FromTyped(medical),
        _ => AdapterMedicalBenefit.FromTyped(b), // base-class Benefit ⇒ medical (legacy default)
    };

    /// <summary>
    /// Reconstruct the matching <see cref="Benefit"/> subclass. Override
    /// in each concrete adapter type to populate the type-specific facets.
    /// </summary>
    public virtual Benefit ToBenefit()
    {
        var medical = new MedicalBenefit();
        CopyCommonTo(medical);
        return medical;
    }

    /// <summary>Copy common facets from this DTO onto a <see cref="Benefit"/>.</summary>
    protected void CopyCommonTo(Benefit b)
    {
        b.Id = Id;
        b.ServiceCategory = ServiceCategory;
        b.Description = Description;
        b.IsCovered = IsCovered;
        b.CptCodes = CptCodes.ToList();
        b.InNetworkCopay = InNetworkCopay;
        b.OutNetworkCopay = OutNetworkCopay;
        b.InNetworkCoinsurance = InNetworkCoinsurance;
        b.OutNetworkCoinsurance = OutNetworkCoinsurance;
        b.DeductibleApplies = DeductibleApplies;
        b.OopApplies = OopApplies;
        b.PriorAuthRequired = PriorAuthRequired;
        b.CopayAmount = CopayAmount;
        b.CoinsurancePercentage = CoinsurancePercentage;
        b.RequiresPriorAuth = RequiresPriorAuth;
        b.VisitLimit = VisitLimit;
        b.VisitLimitPeriod = VisitLimitPeriod;
        b.Limitations = Limitations;
        b.AnnualMaximum = AnnualMaximum;
        b.LifetimeMaximum = LifetimeMaximum;
        b.Rules = Rules?.Select(r => r).ToList();
    }

    /// <summary>Copy common facets from a <see cref="Benefit"/> onto this DTO.</summary>
    protected void CopyCommonFrom(Benefit b)
    {
        Id = b.Id;
        ServiceCategory = b.ServiceCategory;
        Description = b.Description;
        IsCovered = b.IsCovered;
        CptCodes = b.CptCodes.ToList();
        InNetworkCopay = b.InNetworkCopay;
        OutNetworkCopay = b.OutNetworkCopay;
        InNetworkCoinsurance = b.InNetworkCoinsurance;
        OutNetworkCoinsurance = b.OutNetworkCoinsurance;
        DeductibleApplies = b.DeductibleApplies;
        OopApplies = b.OopApplies;
        PriorAuthRequired = b.PriorAuthRequired;
        CopayAmount = b.CopayAmount;
        CoinsurancePercentage = b.CoinsurancePercentage;
        RequiresPriorAuth = b.RequiresPriorAuth;
        VisitLimit = b.VisitLimit;
        VisitLimitPeriod = b.VisitLimitPeriod;
        Limitations = b.Limitations;
        AnnualMaximum = b.AnnualMaximum;
        LifetimeMaximum = b.LifetimeMaximum;
        Rules = b.Rules?.Select(r => r.Clone()).ToList();
    }
}

public sealed class AdapterMedicalBenefit : AdapterBenefit
{
    public static AdapterMedicalBenefit FromTyped(Benefit src)
    {
        var dto = new AdapterMedicalBenefit();
        dto.CopyCommonFrom(src);
        return dto;
    }

    public override Benefit ToBenefit()
    {
        var b = new MedicalBenefit();
        CopyCommonTo(b);
        return b;
    }
}

public sealed class AdapterDentalBenefit : AdapterBenefit
{
    public bool IsOrthodontic { get; set; }
    public bool IsImplant { get; set; }
    public decimal? LifetimeBenefitMaximum { get; set; }

    public static AdapterDentalBenefit FromTyped(DentalBenefit src)
    {
        var dto = new AdapterDentalBenefit
        {
            IsOrthodontic = src.IsOrthodontic,
            IsImplant = src.IsImplant,
            LifetimeBenefitMaximum = src.LifetimeBenefitMaximum,
        };
        dto.CopyCommonFrom(src);
        return dto;
    }

    public override Benefit ToBenefit()
    {
        var b = new DentalBenefit
        {
            IsOrthodontic = IsOrthodontic,
            IsImplant = IsImplant,
            LifetimeBenefitMaximum = LifetimeBenefitMaximum,
        };
        CopyCommonTo(b);
        return b;
    }
}

public sealed class AdapterPharmacyBenefit : AdapterBenefit
{
    public string? FormularyTier { get; set; }
    public bool IsSpecialtyDrug { get; set; }
    public bool RequiresStepTherapy { get; set; }
    public int? QuantityLimit { get; set; }
    public int? DaysSupply { get; set; }

    public static AdapterPharmacyBenefit FromTyped(PharmacyBenefit src)
    {
        var dto = new AdapterPharmacyBenefit
        {
            FormularyTier = src.FormularyTier,
            IsSpecialtyDrug = src.IsSpecialtyDrug,
            RequiresStepTherapy = src.RequiresStepTherapy,
            QuantityLimit = src.QuantityLimit,
            DaysSupply = src.DaysSupply,
        };
        dto.CopyCommonFrom(src);
        return dto;
    }

    public override Benefit ToBenefit()
    {
        var b = new PharmacyBenefit
        {
            FormularyTier = FormularyTier,
            IsSpecialtyDrug = IsSpecialtyDrug,
            RequiresStepTherapy = RequiresStepTherapy,
            QuantityLimit = QuantityLimit,
            DaysSupply = DaysSupply,
        };
        CopyCommonTo(b);
        return b;
    }
}

public sealed class AdapterBehavioralHealthBenefit : AdapterBenefit
{
    public bool IsParityProtected { get; set; } = true;
    public string? ParityCategory { get; set; }

    public static AdapterBehavioralHealthBenefit FromTyped(BehavioralHealthBenefit src)
    {
        var dto = new AdapterBehavioralHealthBenefit
        {
            IsParityProtected = src.IsParityProtected,
            ParityCategory = src.ParityCategory,
        };
        dto.CopyCommonFrom(src);
        return dto;
    }

    public override Benefit ToBenefit()
    {
        var b = new BehavioralHealthBenefit
        {
            IsParityProtected = IsParityProtected,
            ParityCategory = ParityCategory,
        };
        CopyCommonTo(b);
        return b;
    }
}

public sealed class AdapterVisionBenefit : AdapterBenefit
{
    public bool IsRoutineExam { get; set; }
    public decimal? FrameAllowance { get; set; }
    public string? LensCoverageType { get; set; }

    public static AdapterVisionBenefit FromTyped(VisionBenefit src)
    {
        var dto = new AdapterVisionBenefit
        {
            IsRoutineExam = src.IsRoutineExam,
            FrameAllowance = src.FrameAllowance,
            LensCoverageType = src.LensCoverageType,
        };
        dto.CopyCommonFrom(src);
        return dto;
    }

    public override Benefit ToBenefit()
    {
        var b = new VisionBenefit
        {
            IsRoutineExam = IsRoutineExam,
            FrameAllowance = FrameAllowance,
            LensCoverageType = LensCoverageType,
        };
        CopyCommonTo(b);
        return b;
    }
}

public sealed class AdapterDMEBenefit : AdapterBenefit
{
    public bool RequiresFitting { get; set; }
    public int? FittingPeriodDays { get; set; }
    public bool IsRental { get; set; }
    public int? MaxRentalMonths { get; set; }

    public static AdapterDMEBenefit FromTyped(DMEBenefit src)
    {
        var dto = new AdapterDMEBenefit
        {
            RequiresFitting = src.RequiresFitting,
            FittingPeriodDays = src.FittingPeriodDays,
            IsRental = src.IsRental,
            MaxRentalMonths = src.MaxRentalMonths,
        };
        dto.CopyCommonFrom(src);
        return dto;
    }

    public override Benefit ToBenefit()
    {
        var b = new DMEBenefit
        {
            RequiresFitting = RequiresFitting,
            FittingPeriodDays = FittingPeriodDays,
            IsRental = IsRental,
            MaxRentalMonths = MaxRentalMonths,
        };
        CopyCommonTo(b);
        return b;
    }
}

public sealed class AdapterMaternityBenefit : AdapterBenefit
{
    public bool CoversPrenatal { get; set; }
    public bool CoversDelivery { get; set; }
    public bool CoversPostpartum { get; set; }

    // Explicit camelCase wire name so the all-caps C# acronym doesn't leak
    // through and produce `coversNICU` on the wire — matches the model.
    [JsonPropertyName("coversNicu")]
    public bool CoversNICU { get; set; }

    public static AdapterMaternityBenefit FromTyped(MaternityBenefit src)
    {
        var dto = new AdapterMaternityBenefit
        {
            CoversPrenatal = src.CoversPrenatal,
            CoversDelivery = src.CoversDelivery,
            CoversPostpartum = src.CoversPostpartum,
            CoversNICU = src.CoversNICU,
        };
        dto.CopyCommonFrom(src);
        return dto;
    }

    public override Benefit ToBenefit()
    {
        var b = new MaternityBenefit
        {
            CoversPrenatal = CoversPrenatal,
            CoversDelivery = CoversDelivery,
            CoversPostpartum = CoversPostpartum,
            CoversNICU = CoversNICU,
        };
        CopyCommonTo(b);
        return b;
    }
}

public sealed class AdapterPreventiveBenefit : AdapterBenefit
{
    public bool IsAcaPreventive { get; set; }
    public string? UspstfRecommendationGrade { get; set; }

    public static AdapterPreventiveBenefit FromTyped(PreventiveBenefit src)
    {
        var dto = new AdapterPreventiveBenefit
        {
            IsAcaPreventive = src.IsAcaPreventive,
            UspstfRecommendationGrade = src.UspstfRecommendationGrade,
        };
        dto.CopyCommonFrom(src);
        return dto;
    }

    public override Benefit ToBenefit()
    {
        var b = new PreventiveBenefit
        {
            IsAcaPreventive = IsAcaPreventive,
            UspstfRecommendationGrade = UspstfRecommendationGrade,
        };
        CopyCommonTo(b);
        return b;
    }
}

public class AdapterNetworkTier
{
    public string Id { get; set; } = string.Empty;
    public string TierName { get; set; } = string.Empty;
    public int TierLevel { get; set; }

    /// <summary>
    /// Reference to <c>Organization.OrganizationId</c> in provider-service
    /// (capability 5.5). Nullable during migration; see
    /// <see cref="NetworkTier.NetworkId"/>.
    /// </summary>
    public string? NetworkId { get; set; }

    /// <summary>
    /// Legacy embedded roster snapshot. Preserved on the wire during the
    /// 5.5 migration window. Removed in a follow-up PR.
    /// </summary>
    [Obsolete("Use NetworkId. See docs/architecture/network-tier-organization-reference.md.")]
    public List<string> ProviderNpis { get; set; } = new();

#pragma warning disable CS0618 // Round-trip through obsolete field is required during the 5.5 migration window
    public static AdapterNetworkTier From(NetworkTier n) => new()
    {
        Id = n.Id,
        TierName = n.TierName,
        TierLevel = n.TierLevel,
        NetworkId = n.NetworkId,
        ProviderNpis = n.ProviderNpis.ToList(),
    };

    public NetworkTier ToNetworkTier() => new()
    {
        Id = Id,
        TierName = TierName,
        TierLevel = TierLevel,
        NetworkId = NetworkId,
        ProviderNpis = ProviderNpis.ToList(),
    };
#pragma warning restore CS0618
}

public class AdapterCostSharing
{
    public decimal Coinsurance { get; set; }
    public decimal MonthlyPremium { get; set; }
    public decimal IndividualDeductible { get; set; }
    public decimal FamilyDeductible { get; set; }
    public decimal IndividualOutOfPocketMax { get; set; }
    public decimal FamilyOutOfPocketMax { get; set; }
    public decimal InNetworkDeductible { get; set; }
    public decimal OutOfNetworkDeductible { get; set; }
    public decimal InNetworkOutOfPocketMax { get; set; }
    public decimal OutOfNetworkOutOfPocketMax { get; set; }
    public decimal? OutNetworkIndividualDeductible { get; set; }
    public decimal? OutNetworkFamilyDeductible { get; set; }
    public decimal? OutNetworkIndividualOutOfPocketMax { get; set; }
    public decimal? OutNetworkFamilyOutOfPocketMax { get; set; }

    public static AdapterCostSharing From(CostSharing c) => new()
    {
        Coinsurance = c.Coinsurance,
        MonthlyPremium = c.MonthlyPremium,
        IndividualDeductible = c.IndividualDeductible,
        FamilyDeductible = c.FamilyDeductible,
        IndividualOutOfPocketMax = c.IndividualOutOfPocketMax,
        FamilyOutOfPocketMax = c.FamilyOutOfPocketMax,
        InNetworkDeductible = c.InNetworkDeductible,
        OutOfNetworkDeductible = c.OutOfNetworkDeductible,
        InNetworkOutOfPocketMax = c.InNetworkOutOfPocketMax,
        OutOfNetworkOutOfPocketMax = c.OutOfNetworkOutOfPocketMax,
        OutNetworkIndividualDeductible = c.OutNetworkIndividualDeductible,
        OutNetworkFamilyDeductible = c.OutNetworkFamilyDeductible,
        OutNetworkIndividualOutOfPocketMax = c.OutNetworkIndividualOutOfPocketMax,
        OutNetworkFamilyOutOfPocketMax = c.OutNetworkFamilyOutOfPocketMax,
    };

    public CostSharing ToCostSharing() => new()
    {
        Coinsurance = Coinsurance,
        MonthlyPremium = MonthlyPremium,
        IndividualDeductible = IndividualDeductible,
        FamilyDeductible = FamilyDeductible,
        IndividualOutOfPocketMax = IndividualOutOfPocketMax,
        FamilyOutOfPocketMax = FamilyOutOfPocketMax,
        InNetworkDeductible = InNetworkDeductible,
        OutOfNetworkDeductible = OutOfNetworkDeductible,
        InNetworkOutOfPocketMax = InNetworkOutOfPocketMax,
        OutOfNetworkOutOfPocketMax = OutOfNetworkOutOfPocketMax,
        OutNetworkIndividualDeductible = OutNetworkIndividualDeductible,
        OutNetworkFamilyDeductible = OutNetworkFamilyDeductible,
        OutNetworkIndividualOutOfPocketMax = OutNetworkIndividualOutOfPocketMax,
        OutNetworkFamilyOutOfPocketMax = OutNetworkFamilyOutOfPocketMax,
    };
}

public class AdapterPlanDocumentReference
{
    public string Id { get; set; } = string.Empty;
    public PlanDocumentType DocType { get; set; }
    public string Location { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public long? Size { get; set; }
    public string? ContentHashSha256 { get; set; }
    public string? Version { get; set; }
    public DateTime? EffectiveDate { get; set; }
    public string? DisplayName { get; set; }

    public static AdapterPlanDocumentReference From(PlanDocumentReference d) => new()
    {
        Id = d.Id,
        DocType = d.DocType,
        Location = d.Location,
        ContentType = d.ContentType,
        Size = d.Size,
        ContentHashSha256 = d.ContentHashSha256,
        Version = d.Version,
        EffectiveDate = d.EffectiveDate,
        DisplayName = d.DisplayName,
    };

    public PlanDocumentReference ToPlanDocumentReference() => new()
    {
        Id = Id,
        DocType = DocType,
        Location = Location,
        ContentType = ContentType,
        Size = Size,
        ContentHashSha256 = ContentHashSha256,
        Version = Version,
        EffectiveDate = EffectiveDate,
        DisplayName = DisplayName,
    };
}

/// <summary>
/// Normalized member-view DTO mirroring <see cref="MemberBenefitView"/>.
/// </summary>
public class AdapterMemberBenefitView
{
    public string PlanId { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string Payer { get; set; } = string.Empty;
    public string PlanType { get; set; } = string.Empty;
    public string? MetalLevel { get; set; }
    public string LineOfBusiness { get; set; } = string.Empty;
    public DateTime AsOfDate { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public string PlanVersion { get; set; } = string.Empty;
    public string FamilyAccumulatorModel { get; set; } = "Embedded";
    public AdapterCostSharing CostSharing { get; set; } = new();
    public List<AdapterCategorizedBenefit> Categories { get; set; } = new();
    public List<AdapterPlanDocumentLink> Documents { get; set; } = new();

    public static AdapterMemberBenefitView From(MemberBenefitView v) => new()
    {
        PlanId = v.PlanId,
        PlanName = v.PlanName,
        Payer = v.Payer,
        PlanType = v.PlanType,
        MetalLevel = v.MetalLevel,
        LineOfBusiness = v.LineOfBusiness,
        AsOfDate = v.AsOfDate,
        EffectiveDate = v.EffectiveDate,
        TerminationDate = v.TerminationDate,
        PlanVersion = v.PlanVersion,
        FamilyAccumulatorModel = v.FamilyAccumulatorModel,
        CostSharing = AdapterCostSharing.From(v.CostSharing),
        Categories = v.Categories.Select(AdapterCategorizedBenefit.From).ToList(),
        Documents = v.Documents.Select(AdapterPlanDocumentLink.From).ToList(),
    };

    public MemberBenefitView ToMemberBenefitView() => new()
    {
        PlanId = PlanId,
        PlanName = PlanName,
        Payer = Payer,
        PlanType = PlanType,
        MetalLevel = MetalLevel,
        LineOfBusiness = LineOfBusiness,
        AsOfDate = AsOfDate,
        EffectiveDate = EffectiveDate,
        TerminationDate = TerminationDate,
        PlanVersion = PlanVersion,
        FamilyAccumulatorModel = FamilyAccumulatorModel,
        CostSharing = CostSharing.ToCostSharing(),
        Categories = Categories.Select(c => c.ToCategorizedBenefit()).ToList(),
        Documents = Documents.Select(d => d.ToPlanDocumentLink()).ToList(),
    };
}

public class AdapterCategorizedBenefit
{
    public string Category { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ServiceCategory { get; set; } = string.Empty;
    public string? Description { get; set; }
    public AdapterNetworkTierBenefit InNetwork { get; set; } = new();
    public AdapterNetworkTierBenefit? OutOfNetwork { get; set; }
    public bool DeductibleApplies { get; set; }
    public bool OopApplies { get; set; }
    public bool PriorAuthRequired { get; set; }
    public int? VisitLimit { get; set; }
    public string? VisitLimitPeriod { get; set; }
    public decimal? AnnualMaximum { get; set; }
    public decimal? LifetimeMaximum { get; set; }
    public string? Limitations { get; set; }
    public AdapterPharmacyDetail? Pharmacy { get; set; }

    public static AdapterCategorizedBenefit From(CategorizedBenefit c) => new()
    {
        Category = c.Category,
        DisplayName = c.DisplayName,
        ServiceCategory = c.ServiceCategory,
        Description = c.Description,
        InNetwork = AdapterNetworkTierBenefit.From(c.InNetwork),
        OutOfNetwork = c.OutOfNetwork is null ? null : AdapterNetworkTierBenefit.From(c.OutOfNetwork),
        DeductibleApplies = c.DeductibleApplies,
        OopApplies = c.OopApplies,
        PriorAuthRequired = c.PriorAuthRequired,
        VisitLimit = c.VisitLimit,
        VisitLimitPeriod = c.VisitLimitPeriod,
        AnnualMaximum = c.AnnualMaximum,
        LifetimeMaximum = c.LifetimeMaximum,
        Limitations = c.Limitations,
        Pharmacy = c.Pharmacy is null ? null : AdapterPharmacyDetail.From(c.Pharmacy),
    };

    public CategorizedBenefit ToCategorizedBenefit() => new()
    {
        Category = Category,
        DisplayName = DisplayName,
        ServiceCategory = ServiceCategory,
        Description = Description,
        InNetwork = InNetwork.ToNetworkTierBenefit(),
        OutOfNetwork = OutOfNetwork?.ToNetworkTierBenefit(),
        DeductibleApplies = DeductibleApplies,
        OopApplies = OopApplies,
        PriorAuthRequired = PriorAuthRequired,
        VisitLimit = VisitLimit,
        VisitLimitPeriod = VisitLimitPeriod,
        AnnualMaximum = AnnualMaximum,
        LifetimeMaximum = LifetimeMaximum,
        Limitations = Limitations,
        Pharmacy = Pharmacy?.ToPharmacyDetail(),
    };
}

public class AdapterNetworkTierBenefit
{
    public string TierName { get; set; } = string.Empty;
    public decimal? Copay { get; set; }
    public decimal? Coinsurance { get; set; }

    public static AdapterNetworkTierBenefit From(NetworkTierBenefit n) => new()
    {
        TierName = n.TierName,
        Copay = n.Copay,
        Coinsurance = n.Coinsurance,
    };

    public NetworkTierBenefit ToNetworkTierBenefit() => new()
    {
        TierName = TierName,
        Copay = Copay,
        Coinsurance = Coinsurance,
    };
}

public class AdapterPharmacyDetail
{
    public string? TierLabel { get; set; }
    public string? CanonicalTier { get; set; }
    public bool IsSpecialty { get; set; }

    public static AdapterPharmacyDetail From(PharmacyDetail p) => new()
    {
        TierLabel = p.TierLabel,
        CanonicalTier = p.CanonicalTier,
        IsSpecialty = p.IsSpecialty,
    };

    public PharmacyDetail ToPharmacyDetail() => new()
    {
        TierLabel = TierLabel,
        CanonicalTier = CanonicalTier,
        IsSpecialty = IsSpecialty,
    };
}

public class AdapterPlanDocumentLink
{
    public string DocType { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public long? Size { get; set; }
    public string? ContentHashSha256 { get; set; }
    public string? Version { get; set; }
    public DateTime? EffectiveDate { get; set; }

    public static AdapterPlanDocumentLink From(PlanDocumentLink p) => new()
    {
        DocType = p.DocType,
        DisplayName = p.DisplayName,
        Location = p.Location,
        ContentType = p.ContentType,
        Size = p.Size,
        ContentHashSha256 = p.ContentHashSha256,
        Version = p.Version,
        EffectiveDate = p.EffectiveDate,
    };

    public PlanDocumentLink ToPlanDocumentLink() => new()
    {
        DocType = DocType,
        DisplayName = DisplayName,
        Location = Location,
        ContentType = ContentType,
        Size = Size,
        ContentHashSha256 = ContentHashSha256,
        Version = Version,
        EffectiveDate = EffectiveDate,
    };
}
