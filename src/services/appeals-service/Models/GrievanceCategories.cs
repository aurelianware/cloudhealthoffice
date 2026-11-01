namespace AppealsService.Models;

/// <summary>
/// Grievance categories the state reports use. A grievance's
/// <see cref="Appeal.GrievanceCategory"/> is one of a plan's state lists; the
/// list is not enforced because each state defines its own.
/// </summary>
public static class GrievanceCategories
{
    /// <summary>
    /// Medi-Cal managed care grievance categories, as listed in DHCS
    /// All Plan Letter 24-001 (Grievance and Appeals) for the grievance and
    /// appeals deliverable template.
    /// </summary>
    public static readonly IReadOnlyList<string> DhcsMediCal =
    [
        "Continuity of Care",
        "Geographic access",
        "Language Access",
        "Out-Of-Network",
        "Physical Access",
        "Provider Availability / Scheduling",
        "Injury",
        "Discrimination",
        "Fraud / Waste / Abuse",
        "PHI / Confidentiality / HIPAA",
        "Authorization of Services",
        "Medi-Cal Eligibility and Enrollment",
        "Referral",
        "Case Management / Care Coordination",
        "Member Informing Materials",
        "Technology / Telephone",
        "Quality of Care",
        "Expedited Appeal Request Denied",
        "Plan's Reduction / Suspension / Termination of Previously Authorized Service",
        "Plan's Failure to Meet Timeframes for Resolution",
        "Customer Service Grievance",
        "Denial of Payment Request",
        "Payment Grievance",
        "Medical Necessity Grievance",
        "Other",
    ];

    /// <summary>Grievances with no category recorded are reported under this.</summary>
    public const string Uncategorized = "UNCATEGORIZED";
}
