namespace AppealsService.Models;

/// <summary>
/// Grievance and appeal categories the state reports use. A grievance's
/// <see cref="Appeal.GrievanceCategory"/> is one of a plan's state lists; the
/// list is not enforced because each state defines its own.
/// </summary>
public static class GrievanceCategories
{
    /// <summary>
    /// Medi-Cal Dental Managed Care grievance categories, as listed in DHCS
    /// Dental All Plan Letter 25-008 (September 30, 2025), which updates the
    /// quarterly Grievance and Appeals reporting template of APL 24-001 to
    /// the CMS categories. Spelled as the APL spells them. This is the dental
    /// program's list; Medi-Cal medical managed care plans report under
    /// their own APLs.
    /// </summary>
    public static readonly IReadOnlyList<string> DhcsMediCalDental =
    [
        "Continuity of Care",
        "Geographic Access",
        "Language Access",
        "Out-of-Network",
        "Physical Access",
        "Injury",
        "Discrimination",
        "Fraud/Waste/Abuse",
        "PHI/Confidentiality/HIPAA",
        "Referral",
        "Case Management/Care Coordination",
        "Member Informing Materials",
        "Technology/Telephone",
        "Quality of Care",
        "Expedited Appeal Request Denied",
        "Plan's Reduction/Suspension/Termination of Previously Authorized Service",
        "Plan's Failure to Meet Timeframes for Resolution",
        "Denial of Payment Request",
        "Timely Access",
        "Provider Availability",
        "Scheduling",
        "Driver Punctuality",
        "Authorization",
        "Enrollment",
        "Eligibility",
        "Vehicle",
        "Disability Discrimination",
        "Assault/Harassment",
        "Inappropriate Care",
        "Provider/Staff Attitude",
        "Abuse/Neglect/Exploitation",
        "Timely Response to Authorization/Appeal Request",
        "Rural Member Denied Out of Network Request",
        "Plan Customer Service",
        "Denial of Request to Dispute Financial Liability",
        "Provider Balance Billing",
        "Provider Direct Member Billing",
    ];

    /// <summary>Medi-Cal Dental Managed Care appeal categories, DHCS Dental APL 25-008.</summary>
    public static readonly IReadOnlyList<string> DhcsMediCalDentalAppeals =
    [
        "Scope of Benefits",
        "Medical Necessity",
        "Payment",
        "Out of Network Appeals",
        "Other",
    ];

    /// <summary>Grievances with no category recorded are reported under this.</summary>
    public const string Uncategorized = "UNCATEGORIZED";
}
