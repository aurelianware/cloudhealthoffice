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

public class AdapterBenefit
{
    public string Id { get; set; } = string.Empty;
    public string ServiceCategory { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
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

    public static AdapterBenefit From(Benefit b) => new()
    {
        Id = b.Id,
        ServiceCategory = b.ServiceCategory,
        Description = b.Description,
        CptCodes = b.CptCodes.ToList(),
        InNetworkCopay = b.InNetworkCopay,
        OutNetworkCopay = b.OutNetworkCopay,
        InNetworkCoinsurance = b.InNetworkCoinsurance,
        OutNetworkCoinsurance = b.OutNetworkCoinsurance,
        DeductibleApplies = b.DeductibleApplies,
        OopApplies = b.OopApplies,
        PriorAuthRequired = b.PriorAuthRequired,
        CopayAmount = b.CopayAmount,
        CoinsurancePercentage = b.CoinsurancePercentage,
        RequiresPriorAuth = b.RequiresPriorAuth,
        VisitLimit = b.VisitLimit,
        VisitLimitPeriod = b.VisitLimitPeriod,
        Limitations = b.Limitations,
        AnnualMaximum = b.AnnualMaximum,
        LifetimeMaximum = b.LifetimeMaximum,
    };

    public Benefit ToBenefit() => new()
    {
        Id = Id,
        ServiceCategory = ServiceCategory,
        Description = Description,
        CptCodes = CptCodes.ToList(),
        InNetworkCopay = InNetworkCopay,
        OutNetworkCopay = OutNetworkCopay,
        InNetworkCoinsurance = InNetworkCoinsurance,
        OutNetworkCoinsurance = OutNetworkCoinsurance,
        DeductibleApplies = DeductibleApplies,
        OopApplies = OopApplies,
        PriorAuthRequired = PriorAuthRequired,
        CopayAmount = CopayAmount,
        CoinsurancePercentage = CoinsurancePercentage,
        RequiresPriorAuth = RequiresPriorAuth,
        VisitLimit = VisitLimit,
        VisitLimitPeriod = VisitLimitPeriod,
        Limitations = Limitations,
        AnnualMaximum = AnnualMaximum,
        LifetimeMaximum = LifetimeMaximum,
    };
}

public class AdapterNetworkTier
{
    public string Id { get; set; } = string.Empty;
    public string TierName { get; set; } = string.Empty;
    public int TierLevel { get; set; }
    public List<string> ProviderNpis { get; set; } = new();

    public static AdapterNetworkTier From(NetworkTier n) => new()
    {
        Id = n.Id,
        TierName = n.TierName,
        TierLevel = n.TierLevel,
        ProviderNpis = n.ProviderNpis.ToList(),
    };

    public NetworkTier ToNetworkTier() => new()
    {
        Id = Id,
        TierName = TierName,
        TierLevel = TierLevel,
        ProviderNpis = ProviderNpis.ToList(),
    };
}

public class AdapterCostSharing
{
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
