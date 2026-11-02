using MongoDB.Bson.Serialization.Attributes;

namespace AppealsService.Reports.Texas;

/// <summary>
/// The codes a Texas Medicaid/CHIP plan reports for one appeal or complaint
/// in the HHSC monthly text files (UMCM 5.24.6 Member Appeals, v2.7, and
/// 5.24.5 Member and Provider Complaints) that CHO does not otherwise record:
/// the program, the member's state Medicaid/CHIP number, how the case came
/// in, and HHSC's category codes. Kept beside the appeal, one document per
/// case, so the appeal's own lifecycle is untouched. Every field is optional
/// here; what a file needs is checked when the file is built.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class TexasAgCodes
{
    public string TenantId { get; set; } = string.Empty;
    public string AppealId { get; set; } = string.Empty;

    // ── Both files ──────────────────────────────────────────────────────
    /// <summary>HHSC program: 01 STAR, 02 STAR+PLUS, 03 STAR Health, 04 STAR Kids, 05 CHIP, 06 CMDS, 07 CHIP Dental, 10 MMP.</summary>
    public string? Program { get; set; }
    /// <summary>The member's 9-digit state-issued Medicaid or CHIP number.</summary>
    public string? StateMemberId { get; set; }
    /// <summary>HHSC tracking ID (15). Defaults to one derived from the appeal number.</summary>
    public string? TrackingId { get; set; }
    /// <summary>V verbal, W written.</summary>
    public string? MethodReceived { get; set; }
    /// <summary>01 MCO, 02 DMO, 03 subcontractor.</summary>
    public string? ReceivedBy { get; set; }
    public string? ReceivedBySubcontractor { get; set; }
    public string? BenefitType { get; set; }

    // ── Member appeals (5.24.6) ─────────────────────────────────────────
    public string? OtherBenefitType { get; set; }
    public string? ServicesImpacted { get; set; }
    public string? OtherServicesImpacted { get; set; }
    public string? ReasonForDenial { get; set; }
    public string? OtherReasonForDenial { get; set; }
    /// <summary>01 one-day expedited, 72 hour expedited, 30 day standard. Defaults from the appeal's urgency.</summary>
    public string? AppealTimeframe { get; set; }
    /// <summary>01 MCO/DMO, 02 subcontractor, 03 member/provider. Defaults from the recorded extension.</summary>
    public string? ExtensionRequestedBy { get; set; }
    public string? AdverseBenefitDetermination { get; set; }
    public string? OtherAdverseBenefitDetermination { get; set; }
    /// <summary>OV, UP, PU, WD, DM. Defaults from the closure; required when the closure doesn't say.</summary>
    public string? AppealOutcome { get; set; }
    /// <summary>EXD, P2P or ADD: why an overturned appeal was overturned.</summary>
    public string? OverturnedOutcome { get; set; }
    public string? PriorAuthorizationNumber { get; set; }

    // ── Complaints (5.24.5) ─────────────────────────────────────────────
    /// <summary>M member, P provider. Defaults to M.</summary>
    public string? ComplaintType { get; set; }
    /// <summary>M member, P provider, L legally authorized representative, O other.</summary>
    public string? Complainant { get; set; }
    public string? OtherComplainant { get; set; }
    /// <summary>Y/N: the member is enrolled in MDCP (member complaints).</summary>
    public string? Mdcp { get; set; }
    /// <summary>CDnnn complaint disposition or RSnnn referral disposition.</summary>
    public string? Disposition { get; set; }
    public string? OtherDispositionDetail { get; set; }
    public string? ComplaintCategory { get; set; }
    public string? ComplaintCategoryDetail { get; set; }
    public string? OtherComplaintCategoryDetail { get; set; }
    /// <summary>Y/N. Defaults to N.</summary>
    public string? DisasterRelated { get; set; }
    /// <summary>CON, NOC or UTD for a resolved complaint (pending and withdrawn are derived).</summary>
    public string? ComplaintOutcome { get; set; }

    public DateTime UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;

    /// <summary>Earlier versions, newest last (at most <see cref="MaxHistory"/>), so a changed code stays on record.</summary>
    public List<TexasAgCodesRevision> History { get; set; } = new();
    public const int MaxHistory = 50;

    public TexasAgCodes CopyCodes() => (TexasAgCodes)MemberwiseClone();
}

public sealed class TexasAgCodesRevision
{
    public DateTime UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
    /// <summary>The codes as they were, as JSON.</summary>
    public string CodesJson { get; set; } = string.Empty;
}

/// <summary>The HHSC value lists, from the UMCM 5.24.5 and 5.24.6 workbooks.</summary>
public static class TexasAgCodeLists
{
    public static readonly IReadOnlyDictionary<string, string> Programs = new Dictionary<string, string>
    {
        ["01"] = "STAR", ["02"] = "STAR+PLUS", ["03"] = "STAR Health", ["04"] = "STAR Kids",
        ["05"] = "CHIP", ["06"] = "CMDS", ["07"] = "CHIP Dental", ["10"] = "MMP",
    };

    /// <summary>The MCO/DMO name codes as listed in the 5.24.5/5.24.6 v2.7 layouts.</summary>
    public static readonly IReadOnlySet<string> McoCodes = new HashSet<string>
    {
        "AET", "AMC", "BCB", "BRV", "CFC", "CHC", "CHR", "CKC", "CMC", "DRC", "DTQ", "EP1", "FCR", "MCN",
        "MOL", "PRK", "SCW", "SET", "SND", "SUP", "TXC", "UHC", "UHD", "WLP",
    };

    public static readonly IReadOnlySet<string> MethodReceived = new HashSet<string> { "V", "W" };
    public static readonly IReadOnlySet<string> ReceivedBy = new HashSet<string> { "01", "02", "03" };
    public static readonly IReadOnlySet<string> YesNo = new HashSet<string> { "Y", "N" };

    public static readonly IReadOnlySet<string> AppealBenefitTypes = new HashSet<string>
        { "AC", "BH", "DT", "LT", "MT", "NF", "OB", "PC", "PH", "SN", "ST", "VS", "OH" };
    public static readonly IReadOnlySet<string> ServicesImpacted = new HashSet<string>
        { "DEN", "SPT", "OCT", "PHC", "PHT", "PDN", "PCS", "PAS", "CPW", "DME", "MET", "OTH" };
    public static readonly IReadOnlySet<string> ReasonsForDenial = new HashSet<string>
        { "DSP", "FIN", "NCA", "NCC", "NPA", "OON", "IND", "OTH" };
    public static readonly IReadOnlySet<string> AppealTimeframes = new HashSet<string> { "01", "72", "30" };
    public static readonly IReadOnlySet<string> ExtensionRequestedBy = new HashSet<string> { "00", "01", "02", "03" };
    public static readonly IReadOnlySet<string> AdverseBenefitDeterminations = new HashSet<string> { "CD", "PD", "RD", "TE", "DN", "OD" };
    public static readonly IReadOnlySet<string> AppealOutcomes = new HashSet<string> { "OV", "UP", "PU", "WD", "DM" };
    public static readonly IReadOnlySet<string> OverturnedOutcomes = new HashSet<string> { "EXD", "P2P", "ADD" };

    public static readonly IReadOnlySet<string> ComplaintTypes = new HashSet<string> { "M", "P" };
    public static readonly IReadOnlySet<string> Complainants = new HashSet<string> { "M", "P", "L", "O" };
    public static readonly IReadOnlySet<string> ComplaintBenefitTypes = new HashSet<string>
        { "AC", "AS", "BH", "CP", "DT", "IN", "LS", "LT", "MT", "NF", "OB", "OT", "PC", "PH", "PT", "SN", "ST", "SU", "VS" };
    public static readonly IReadOnlySet<string> Dispositions = new HashSet<string>(
        Enumerable.Range(1, 24).Select(i => $"CD{i:000}").Concat(Enumerable.Range(1, 9).Select(i => $"RS{i:000}")));
    public const string OtherDisposition = "CD017";
    public static readonly IReadOnlySet<string> ComplaintCategories = new HashSet<string>
        { "AC", "CP", "CS", "EV", "MT", "PP", "PC", "PS", "QC", "VA" };

    /// <summary>Complaint category detail codes (Complaint Categories tab); each starts with its category.</summary>
    public static readonly IReadOnlySet<string> ComplaintCategoryDetails = new HashSet<string>(
        Codes("AC", 13).Concat(Codes("CP", 7)).Concat(Codes("CS", 9)).Concat(Codes("EV", 8)).Concat(Codes("MT", 10))
            .Concat(Codes("PP", 3)).Concat(Codes("PS", 11)).Concat(Codes("PC", 7)).Concat(Codes("QC", 7)).Concat(Codes("VA", 1)));

    /// <summary>"Other" details that need the free-text detail (5.24.5 row 26 validation).</summary>
    public static readonly IReadOnlySet<string> OtherCategoryDetails = new HashSet<string>
        { "AC13", "CP05", "CS05", "PS04", "EV06", "MT08", "PC07", "QC06" };
    public static readonly IReadOnlySet<string> ComplaintOutcomes = new HashSet<string> { "CON", "NOC", "UTD" };

    private static IEnumerable<string> Codes(string prefix, int count) => Enumerable.Range(1, count).Select(i => $"{prefix}{i:00}");
}
