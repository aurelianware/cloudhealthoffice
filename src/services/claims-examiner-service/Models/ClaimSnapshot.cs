using CloudHealthOffice.Events;

namespace ClaimsExaminerService.Models;

/// <summary>
/// Subset of the claim shape returned by GET /api/claims/{id} on claims-service.
/// Only the fields the AI examiner needs to reason about an NCCI pend are mapped;
/// extra fields in the wire payload are ignored. Keep this minimal — every
/// field added here is one more thing that can drift out of sync with the
/// claims-service Claim model.
/// </summary>
public class ClaimSnapshot
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string BillingProviderNPI { get; set; } = string.Empty;
    public string? BillingProviderName { get; set; }
    public string? RenderingProviderNPI { get; set; }
    public string PlaceOfServiceCode { get; set; } = "11";
    public int LineOfBusiness { get; set; }
    public decimal TotalChargeAmount { get; set; }
    public DateTime ServiceDateFrom { get; set; }
    public DateTime ServiceDateTo { get; set; }
    public int Status { get; set; }

    public List<DiagnosisSnapshot> DiagnosisCodes { get; set; } = new();
    public List<ClaimLineSnapshot> ClaimLines { get; set; } = new();

    public PendDetails? PendDetails { get; set; }
}

public class DiagnosisSnapshot
{
    public string Code { get; set; } = string.Empty;
    public string CodeQualifier { get; set; } = "ABK";
    public int PointerNumber { get; set; }
    public string? Description { get; set; }
}

public class ClaimLineSnapshot
{
    public int LineNumber { get; set; }
    public string ProcedureCode { get; set; } = string.Empty;
    public string? ProcedureDescription { get; set; }
    public List<string> Modifiers { get; set; } = new();
    public List<int> DiagnosisPointers { get; set; } = new();
    public decimal Units { get; set; }
    public decimal ChargeAmount { get; set; }
    public DateTime ServiceDateFrom { get; set; }
    public DateTime ServiceDateTo { get; set; }
    public string? PlaceOfServiceCode { get; set; }
    public string? RevenueCode { get; set; }
}

/// <summary>
/// Outbound payload for PUT /api/claims/{id}/ai-examination on claims-service.
/// Field names must match claims-service's AiExamination model exactly.
/// </summary>
public class AiExaminationDto
{
    public string RecommendedDisposition { get; set; } = "EscalateToHuman";
    public double ConfidenceScore { get; set; }
    public string? Rationale { get; set; }
    public List<string> PolicyCitations { get; set; } = new();
    public string? ModelId { get; set; }
    public string? PromptVersion { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}
