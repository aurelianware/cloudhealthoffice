namespace CloudHealthOffice.BenchmarkClaimGenerator.Models;

/// <summary>
/// Represents a single service line on a synthetic claim.
/// </summary>
public class ClaimLine
{
    /// <summary>Line number (1-based).</summary>
    public int LineNumber { get; set; }

    /// <summary>Procedure code (CPT, HCPCS, CDT, or revenue code).</summary>
    public string ProcedureCode { get; set; } = string.Empty;

    /// <summary>Procedure code description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>List of modifiers applied to this line.</summary>
    public List<string> Modifiers { get; set; } = new();

    /// <summary>Revenue code (for institutional claims).</summary>
    public string? RevenueCode { get; set; }

    /// <summary>Diagnosis code pointer indices (1-based references to claim-level diagnoses).</summary>
    public List<int> DiagnosisPointers { get; set; } = new();

    /// <summary>Units/quantity of service.</summary>
    public decimal Units { get; set; }

    /// <summary>Billed charge amount.</summary>
    public decimal ChargeAmount { get; set; }

    /// <summary>Date of service for this line.</summary>
    public DateTime ServiceDate { get; set; }

    /// <summary>Service end date (for date ranges).</summary>
    public DateTime? ServiceEndDate { get; set; }

    /// <summary>Place of service code.</summary>
    public string? PlaceOfService { get; set; }

    /// <summary>National Drug Code (for drug claims).</summary>
    public string? NdcCode { get; set; }

    /// <summary>Tooth number (for dental claims).</summary>
    public string? ToothNumber { get; set; }

    /// <summary>Tooth surface codes (for dental claims).</summary>
    public List<string>? ToothSurfaces { get; set; }
}
