using System.Text.Json.Serialization;
using BenefitPlanService.Models;
using BenefitPlanService.Models.Benefits;

namespace BenefitPlanService.Templates;

/// <summary>
/// A state program's benefit plan template, loaded from
/// <c>schemas/benefit-plan-templates/{state}/*.json</c>. A template is the
/// state-defined part of a plan (covered services, cost sharing, limits) for
/// one program and population, with a source for every value and a
/// <c>status</c> that says whether it was read from a cited source
/// (<c>sourced</c>) or still needs checking against the plan's contract
/// (<c>verify</c>). Instantiating one creates a Draft plan the plan's
/// benefits team reviews before publishing.
/// See docs/integration/STATE-PROGRAM-BENEFIT-TEMPLATES.md.
/// </summary>
public sealed class BenefitPlanTemplate
{
    [JsonPropertyName("templateId")] public string TemplateId { get; set; } = string.Empty;
    [JsonPropertyName("state")] public string State { get; set; } = string.Empty;
    [JsonPropertyName("program")] public string Program { get; set; } = string.Empty;
    [JsonPropertyName("planName")] public string PlanName { get; set; } = string.Empty;
    [JsonPropertyName("population")] public string Population { get; set; } = string.Empty;

    /// <summary>[min, max] age in years, either end null when open; null when not age-based.</summary>
    [JsonPropertyName("ageRange")] public int?[]? AgeRange { get; set; }

    [JsonPropertyName("lineOfBusiness")] public LineOfBusiness LineOfBusiness { get; set; } = LineOfBusiness.Medicaid;
    [JsonPropertyName("planType")] public PlanType PlanType { get; set; } = PlanType.Medicaid;

    /// <summary>Date the cited rules took effect (or the source's date), for the reviewer.</summary>
    [JsonPropertyName("effectiveFrom")] public string? EffectiveFrom { get; set; }

    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonPropertyName("costSharing")] public CostSharing CostSharing { get; set; } = new();

    /// <summary>Adjudication benefits; <c>serviceCategory</c> is a system-default service type code.</summary>
    [JsonPropertyName("benefits")] public List<TemplateBenefit> Benefits { get; set; } = new();

    /// <summary>Member-view rows (pharmacy tiers) that claims do not resolve to.</summary>
    [JsonPropertyName("displayBenefits")] public List<TemplateBenefit> DisplayBenefits { get; set; } = new();

    /// <summary>Services the state contracts to someone else (for example dental to a DMO).</summary>
    [JsonPropertyName("carveOuts")] public List<TemplateNote> CarveOuts { get; set; } = new();

    /// <summary>Program rules CHO's plan model cannot hold yet, and why.</summary>
    [JsonPropertyName("notModeled")] public List<TemplateNote> NotModeled { get; set; } = new();

    [JsonPropertyName("sources")] public Dictionary<string, TemplateSource> Sources { get; set; } = new();

    /// <summary>Count of values still marked <c>verify</c>.</summary>
    [JsonPropertyName("verifyCount")]
    public int VerifyCount =>
        Benefits.Concat(DisplayBenefits).Count(b => b.IsVerify)
        + CarveOuts.Concat(NotModeled).Count(n => string.Equals(n.Status, TemplateStatus.Verify, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Builds a Draft-ready plan. The plan id, name and payer are the plan's
    /// own; the template supplies benefits and cost sharing. Each benefit's
    /// description names its source so the plan document keeps provenance.
    /// </summary>
    public BenefitPlan ToBenefitPlan(string planId, string? planName, string payer, DateTime effectiveDate, DateTime? terminationDate, string? programId)
    {
        var plan = new BenefitPlan
        {
            PlanId = planId,
            PlanName = string.IsNullOrWhiteSpace(planName) ? PlanName : planName,
            Payer = payer,
            EffectiveDate = DateTime.SpecifyKind(effectiveDate.Date, DateTimeKind.Utc),
            TerminationDate = terminationDate is { } t ? DateTime.SpecifyKind(t.Date, DateTimeKind.Utc) : null,
            PlanType = PlanType,
            LineOfBusiness = LineOfBusiness,
            ProgramId = string.IsNullOrWhiteSpace(programId) ? null : programId,
            CostSharing = Clone(CostSharing),
        };

        foreach (var b in Benefits) plan.Benefits.Add(b.ToBenefit(this, display: false));
        foreach (var b in DisplayBenefits) plan.Benefits.Add(b.ToBenefit(this, display: true));
        return plan;
    }

    private static CostSharing Clone(CostSharing c) => new()
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
}

public static class TemplateStatus
{
    public const string Sourced = "sourced";
    public const string Verify = "verify";
}

public sealed class TemplateBenefit
{
    [JsonPropertyName("serviceCategory")] public string ServiceCategory { get; set; } = string.Empty;
    [JsonPropertyName("covered")] public bool Covered { get; set; } = true;
    [JsonPropertyName("copay")] public decimal? Copay { get; set; }

    /// <summary>Fraction (0.20), not percent.</summary>
    [JsonPropertyName("coinsurance")] public decimal? Coinsurance { get; set; }

    [JsonPropertyName("deductibleApplies")] public bool DeductibleApplies { get; set; }
    [JsonPropertyName("priorAuthRequired")] public bool PriorAuthRequired { get; set; }
    [JsonPropertyName("visitLimit")] public int? VisitLimit { get; set; }
    [JsonPropertyName("visitLimitPeriod")] public string? VisitLimitPeriod { get; set; }
    [JsonPropertyName("annualMaximum")] public decimal? AnnualMaximum { get; set; }
    [JsonPropertyName("limitations")] public string? Limitations { get; set; }
    [JsonPropertyName("formularyTier")] public string? FormularyTier { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = TemplateStatus.Verify;

    [JsonIgnore]
    public bool IsVerify => string.Equals(Status, TemplateStatus.Verify, StringComparison.OrdinalIgnoreCase);

    internal Benefit ToBenefit(BenefitPlanTemplate template, bool display)
    {
        Benefit benefit = display || FormularyTier is not null || ServiceCategory == "Pharmacy"
            ? new PharmacyBenefit { FormularyTier = FormularyTier ?? "Generic" }
            : ServiceCategory == "Preventive Care"
                ? new PreventiveBenefit { IsAcaPreventive = Copay is null or 0m && Coinsurance is null or 0m && !DeductibleApplies }
                : new MedicalBenefit();

        benefit.ServiceCategory = ServiceCategory;
        var sourceTitle = Source is not null && template.Sources.TryGetValue(Source, out var s) ? s.Title : Source;
        benefit.Description = $"{template.Program}: {ServiceCategory}"
            + (sourceTitle is null ? string.Empty : $" (source: {sourceTitle}{(IsVerify ? "; verify" : string.Empty)})");
        benefit.IsCovered = Covered;
        benefit.InNetworkCopay = Copay is > 0 ? Copay : null;
        benefit.InNetworkCoinsurance = Coinsurance ?? 0m;
        benefit.DeductibleApplies = DeductibleApplies;
        benefit.PriorAuthRequired = PriorAuthRequired;
        benefit.VisitLimit = VisitLimit;
        benefit.VisitLimitPeriod = VisitLimitPeriod;
        benefit.AnnualMaximum = AnnualMaximum;
        benefit.Limitations = Limitations;

        // Medicaid and CHIP managed care pay nothing out of network except
        // emergencies (priced in network by the engine) and services the MCO
        // authorizes; the member is not billed. Out of network is left at the
        // in-network cost share and network enforcement stays with
        // credentialing and authorization.
        benefit.OutNetworkCopay = benefit.InNetworkCopay;
        benefit.OutNetworkCoinsurance = benefit.InNetworkCoinsurance;
        return benefit;
    }
}

public sealed class TemplateNote
{
    [JsonPropertyName("item")] public string? Item { get; set; }
    [JsonPropertyName("service")] public string? Service { get; set; }
    [JsonPropertyName("to")] public string? To { get; set; }
    [JsonPropertyName("why")] public string? Why { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = TemplateStatus.Verify;
}

public sealed class TemplateSource
{
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("accessed")] public string? Accessed { get; set; }
}
