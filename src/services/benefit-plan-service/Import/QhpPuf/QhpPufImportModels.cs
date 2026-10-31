using System.Text.Json.Serialization;
using BenefitPlanService.Models;

namespace BenefitPlanService.Import.QhpPuf;

/// <summary>Which plans in the PUF files to import.</summary>
public sealed class QhpPufImportOptions
{
    /// <summary>Hard cap on plans per request, whatever the caller asks for.</summary>
    public const int MaxPlansCeiling = 2000;

    /// <summary>Two-letter state. At least one of state, issuer or plan ids is required.</summary>
    public string? StateCode { get; init; }

    /// <summary>Five-digit HIOS issuer id.</summary>
    public string? IssuerId { get; init; }

    /// <summary>14-character standard component ids or 17-character plan ids (with CSR suffix).</summary>
    public IReadOnlySet<string> PlanIds { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// CSR variant suffixes to keep ("00" off exchange, "01" on exchange,
    /// "02"/"03" AI/AN, "04"-"06" silver CSR). Empty keeps every variant.
    /// </summary>
    public IReadOnlySet<string> CsrVariants { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Optional plan year filter (BusinessYear).</summary>
    public int? BusinessYear { get; init; }

    public int MaxPlans { get; init; } = 200;

    /// <summary>True (the default) maps and reports without writing anything.</summary>
    public bool DryRun { get; init; } = true;

    /// <summary>Include the mapped plan documents in the response. Defaults on for a dry run.</summary>
    public bool IncludePlans { get; init; }

    public bool HasSelector =>
        !string.IsNullOrWhiteSpace(StateCode) || !string.IsNullOrWhiteSpace(IssuerId) || PlanIds.Count > 0;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QhpPlanImportOutcome
{
    /// <summary>Dry run: would be created as a Draft.</summary>
    WouldCreate,
    /// <summary>Created as a Draft (not published).</summary>
    Created,
    /// <summary>A plan with this plan id already exists for the tenant; left alone.</summary>
    Exists,
    /// <summary>Not imported (dental-only plan, no benefit rows, ...).</summary>
    Skipped,
    /// <summary>Plan validation refused it (for example an ACA limit check).</summary>
    Invalid,
}

/// <summary>A QHP benefit row CHO did not turn into a benefit.</summary>
public sealed record QhpUnmappedBenefit(
    [property: JsonPropertyName("benefitName")] string BenefitName,
    [property: JsonPropertyName("isCovered")] bool IsCovered,
    [property: JsonPropertyName("copayInnTier1")] string CopayInnTier1,
    [property: JsonPropertyName("coinsInnTier1")] string CoinsInnTier1);

public sealed class QhpPlanImportResult
{
    [JsonPropertyName("planId")] public string PlanId { get; set; } = string.Empty;
    [JsonPropertyName("standardComponentId")] public string StandardComponentId { get; set; } = string.Empty;
    [JsonPropertyName("planName")] public string PlanName { get; set; } = string.Empty;
    [JsonPropertyName("issuerId")] public string IssuerId { get; set; } = string.Empty;
    [JsonPropertyName("issuerName")] public string IssuerName { get; set; } = string.Empty;
    [JsonPropertyName("stateCode")] public string StateCode { get; set; } = string.Empty;
    [JsonPropertyName("businessYear")] public string BusinessYear { get; set; } = string.Empty;
    [JsonPropertyName("metalLevel")] public string MetalLevel { get; set; } = string.Empty;
    [JsonPropertyName("csrVariationType")] public string CsrVariationType { get; set; } = string.Empty;
    [JsonPropertyName("outcome")] public QhpPlanImportOutcome Outcome { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("versionId")] public string? VersionId { get; set; }

    /// <summary>Places where the CHO plan approximates the QHP plan. A reviewer should read these before publishing.</summary>
    [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = new();

    /// <summary>CHO categories filled, with the QHP benefit each came from.</summary>
    [JsonPropertyName("categoriesFilled")] public Dictionary<string, string> CategoriesFilled { get; set; } = new();

    /// <summary>CHO adjudication categories the plan has no QHP row for. Claims in these categories will not find a cost share.</summary>
    [JsonPropertyName("categoriesMissing")] public List<string> CategoriesMissing { get; set; } = new();

    /// <summary>QHP rows that matched a category already filled from a preferred row.</summary>
    [JsonPropertyName("foldedBenefits")] public List<string> FoldedBenefits { get; set; } = new();

    [JsonPropertyName("unmappedBenefits")] public List<QhpUnmappedBenefit> UnmappedBenefits { get; set; } = new();

    /// <summary>Raw plan-level references for follow-up loaders (network, service area, formulary, SBC).</summary>
    [JsonPropertyName("references")] public Dictionary<string, string> References { get; set; } = new();

    [JsonPropertyName("plan")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BenefitPlan? Plan { get; set; }
}

public sealed class QhpPufImportReport
{
    [JsonPropertyName("dryRun")] public bool DryRun { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = "CMS Exchange QHP Public Use Files";
    [JsonPropertyName("plansMatched")] public int PlansMatched { get; set; }
    [JsonPropertyName("plansProcessed")] public int PlansProcessed { get; set; }

    /// <summary>True when more plans matched than <see cref="QhpPufImportOptions.MaxPlans"/>; narrow the filter or page by issuer.</summary>
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }

    [JsonPropertyName("outcomes")] public Dictionary<string, int> Outcomes { get; set; } = new();

    /// <summary>Unmapped QHP benefit names across the run, with the number of plans that carry each.</summary>
    [JsonPropertyName("unmappedBenefitCounts")] public Dictionary<string, int> UnmappedBenefitCounts { get; set; } = new();

    [JsonPropertyName("plans")] public List<QhpPlanImportResult> Plans { get; set; } = new();
}
