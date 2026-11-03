using System.Globalization;
using System.Text;
using CloudHealthOffice.Infrastructure.Reporting;

namespace ClaimsService.Finance;

/// <summary>
/// One claim version as the Texas Claims Summary Report (UMCM 5.24.1) needs
/// it. <see cref="ReceivedAt"/> is the payer receipt instant of this version
/// (an adjustment's corrected claim is received on its own date).
/// </summary>
public sealed record ClaimsSummaryClaim(
    string ClaimNumber,
    int VersionNumber,
    string? MemberId,
    DateTime ServiceDateFrom,
    Models.ClaimType Form,
    DateTime ReceivedAt,
    DateTime? AdjudicatedAt,
    Models.ClaimStatus Status,
    decimal PlanPaid,
    string? DenialReasonCode,
    string? PendCode,
    bool ExactDuplicateFound,
    string? BillingProviderNpi,
    string? PlaceOfService,
    IReadOnlyList<string> ProcedureCodes,
    IReadOnlyList<string> RevenueCodes);

/// <summary>HHSC's codes for the Claims Summary Report (UMCM 5.24.1 v2.5, effective 2024-09-01).</summary>
public static class TexasClaimsSummaryCodes
{
    public static readonly IReadOnlyDictionary<string, string> Programs = new Dictionary<string, string>
    {
        ["01"] = "STAR", ["02"] = "STAR+PLUS", ["03"] = "STAR Health", ["04"] = "STAR Kids", ["05"] = "CHIP",
        ["06"] = "CMDS", ["07"] = "CHIP Dental", ["08"] = "MMP-Medicaid", ["09"] = "MMP-Medicare",
    };

    /// <summary>The MCO/DMO name codes listed in the 5.24.1 v2.5 layout.</summary>
    public static readonly IReadOnlySet<string> McoCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "AET", "AMC", "BCB", "BRV", "CFC", "CHC", "CHR", "CKC", "CMC", "DRC", "DTQ", "EP1", "FCR", "MCN",
        "MOL", "PRK", "SCW", "SET", "SND", "SUP", "TXC", "UHC", "UHD", "WLP",
    };

    /// <summary>Claim types CHO builds from claims (not PH, RC or AP, which come from the request).</summary>
    public static readonly IReadOnlySet<string> ClaimTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "AC", "BH", "LT", "VS", "DT", "AO", "UR", "MC", "MT",
    };

    /// <summary>The "Claim Types Crosswalk" tab: which claim types each program reports.</summary>
    private static readonly Dictionary<string, string[]> TypesByProgram = new()
    {
        ["01"] = ["AC", "BH", "VS", "PH", "MT"],
        ["02"] = ["AC", "BH", "LT", "VS", "PH", "AO", "UR", "MT"],
        ["03"] = ["AC", "BH", "VS", "DT", "PH", "MT"],
        ["04"] = ["AC", "BH", "LT", "VS", "PH", "AO", "UR", "MT"],
        ["05"] = ["AC", "BH", "VS", "PH"],
        ["06"] = ["DT"],
        ["07"] = ["DT"],
        ["08"] = ["AC", "BH", "LT", "VS", "PH", "AO", "UR", "MC", "MT"],
        ["09"] = ["AC", "BH", "LT", "VS", "PH", "AO", "UR", "MC", "MT"],
    };

    public static bool Reports(string program, string claimType) =>
        TypesByProgram.TryGetValue(program, out var types) && types.Contains(claimType);

    public static readonly IReadOnlyDictionary<string, string> RecoupmentReasons = new Dictionary<string, string>
    {
        ["01"] = "Audit Finding", ["02"] = "Billed Incorrect DRG", ["03"] = "Claim Billed in Error",
        ["04"] = "Claim Overpayment", ["05"] = "Claim Underpayment", ["06"] = "Coordination of Benefits (COB)",
        ["07"] = "Fee Schedule Change", ["08"] = "Manual Processing Error", ["09"] = "Negative Balance",
        ["10"] = "Retro Disenrollment", ["11"] = "RUG Change", ["12"] = "System Error", ["99"] = "Other",
    };

    public static readonly IReadOnlyDictionary<string, string> AdvancedPaymentReasons = new Dictionary<string, string>
    {
        ["01"] = "Provider Requested", ["02"] = "Facility Requested", ["99"] = "Other",
    };

    /// <summary>
    /// The claim form for a claim type on an 837 of the given kind ("Claim
    /// Types Crosswalk"): dental on ADA, nursing facility unit rate and
    /// Medicare coinsurance on NF 837i, otherwise UB04 (institutional) or
    /// CMS 1500 (professional).
    /// </summary>
    public static string Form(string claimType, Models.ClaimType form) => claimType switch
    {
        "DT" => "A",
        "UR" or "MC" => "N",
        _ => form switch
        {
            Models.ClaimType.Institutional => "U",
            Models.ClaimType.Dental => "A",
            _ => "C",
        },
    };
}

/// <summary>
/// A rule naming a claim's 5.24.1 claim type. Rules are tried in order and
/// the first whose every given condition matches wins; a claim no rule
/// matches is dental (DT) on an 837D and acute care (AC) otherwise.
/// </summary>
public sealed class ClaimsSummaryClaimTypeRule
{
    /// <summary>AC, BH, LT, VS, DT, AO, UR, MC or MT.</summary>
    public string ClaimType { get; set; } = string.Empty;

    /// <summary>837 kind: Professional, Institutional or Dental.</summary>
    public string? ClaimForm { get; set; }

    public List<string>? BillingProviderNpis { get; set; }

    /// <summary>Any line's procedure code starts with one of these (e.g. "A01" for NEMT, "H00" for BH).</summary>
    public List<string>? ProcedureCodePrefixes { get; set; }

    /// <summary>Any line's revenue code starts with one of these (e.g. "090", "091" for BH).</summary>
    public List<string>? RevenueCodePrefixes { get; set; }

    public List<string>? PlacesOfService { get; set; }

    public bool Matches(ClaimsSummaryClaim c)
    {
        if (ClaimForm != null && !string.Equals(ClaimForm, c.Form.ToString(), StringComparison.OrdinalIgnoreCase)) return false;
        if (BillingProviderNpis is { Count: > 0 } && !BillingProviderNpis.Contains(c.BillingProviderNpi ?? "", StringComparer.Ordinal)) return false;
        if (ProcedureCodePrefixes is { Count: > 0 }
            && !c.ProcedureCodes.Any(p => ProcedureCodePrefixes.Any(x => p.StartsWith(x, StringComparison.OrdinalIgnoreCase)))) return false;
        if (RevenueCodePrefixes is { Count: > 0 }
            && !c.RevenueCodes.Any(r => RevenueCodePrefixes.Any(x => r.StartsWith(x, StringComparison.Ordinal)))) return false;
        if (PlacesOfService is { Count: > 0 } && !PlacesOfService.Contains(c.PlaceOfService ?? "", StringComparer.Ordinal)) return false;
        return true;
    }
}

/// <summary>Counts the plan keeps outside CHO, entered for one program, claim type and claim form.</summary>
public sealed class ClaimsSummaryEnteredCount
{
    public string Program { get; set; } = string.Empty;
    public string ClaimType { get; set; } = string.Empty;
    /// <summary>U, C, N or A.</summary>
    public string ClaimForm { get; set; } = string.Empty;
    public int Claims { get; set; }
    /// <summary>Interest paid (interest penalties only).</summary>
    public decimal Amount { get; set; }
}

/// <summary>Pharmacy claims for one program, from the plan's PBM (section E).</summary>
public sealed class ClaimsSummaryPharmacy
{
    public string Program { get; set; } = string.Empty;
    public int ElectronicPaidOver18Claims { get; set; }
    public decimal ElectronicPaidOver18Amount { get; set; }
    public int ElectronicPaidWithin18Claims { get; set; }
    public decimal ElectronicPaidWithin18Amount { get; set; }
    public int ElectronicRejectedOver18Claims { get; set; }
    public int ElectronicRejectedWithin18Claims { get; set; }
    public int NonElectronicPaidOver21Claims { get; set; }
    public decimal NonElectronicPaidOver21Amount { get; set; }
    public int NonElectronicPaidWithin21Claims { get; set; }
    public decimal NonElectronicPaidWithin21Amount { get; set; }
    public int NonElectronicRejectedOver21Claims { get; set; }
    public int NonElectronicRejectedWithin21Claims { get; set; }
    public int InterestClaims { get; set; }
    public decimal InterestAmount { get; set; }
}

/// <summary>A provider recoupment (section F) or advanced payment (section G), entered by finance.</summary>
public sealed class ClaimsSummaryProviderPayment
{
    public string Program { get; set; } = string.Empty;
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? FacilityName { get; set; }
    /// <summary>HHSC's two-digit reason code.</summary>
    public string Reason { get; set; } = string.Empty;
    /// <summary>Required when the reason is 99 (Other).</summary>
    public string? ReasonOther { get; set; }
    public decimal Amount { get; set; }
    public DateOnly Date { get; set; }
}

public sealed class TexasClaimsSummaryRequest
{
    /// <summary>The claims month reported (yyyy-MM): claims adjudicated in it, in Texas time.</summary>
    public string ClaimsMonth { get; set; } = string.Empty;

    /// <summary>HHSC's three-character MCO/DMO name code, e.g. SUP.</summary>
    public string McoCode { get; set; } = string.Empty;

    /// <summary>
    /// HHSC program code (01 STAR ... 09 MMP-Medicare) for each rate cell. A
    /// claim's program is its member's rate cell in the month of service.
    /// Claims in unmapped rate cells are left out and counted.
    /// </summary>
    public Dictionary<string, string> RateCellPrograms { get; set; } = new();

    public List<ClaimsSummaryClaimTypeRule> ClaimTypeRules { get; set; } = new();

    /// <summary>Denial reason codes that make a denial a duplicate. Default: CARC 18.</summary>
    public List<string>? DuplicateDenialCodes { get; set; }

    /// <summary>Denial reason codes for a claim denied as deficient (missing information). Default: CARC 16.</summary>
    public List<string>? DeficientDenialCodes { get; set; }

    /// <summary>Pend codes that mean the claim is waiting on information from the provider. Default: none.</summary>
    public List<string>? DeficientPendCodes { get; set; }

    /// <summary>
    /// Claims the provider appealed. An adjustment of one of these claims
    /// adjudicated in the month is an appealed claim, not an adjusted one.
    /// </summary>
    public List<string>? AppealedClaimNumbers { get; set; }

    /// <summary>Billing NPIs paid by capitation: their claims count as capitated service claims only.</summary>
    public List<string>? CapitatedBillingNpis { get; set; }

    /// <summary>Claims rejected before CHO received them (front-end 999/277CA rejections), by line.</summary>
    public List<ClaimsSummaryEnteredCount>? RejectedClaims { get; set; }

    /// <summary>Prompt-pay interest penalties paid, by line. CHO does not compute them.</summary>
    public List<ClaimsSummaryEnteredCount>? InterestPenalties { get; set; }

    public List<ClaimsSummaryPharmacy>? Pharmacy { get; set; }
    public List<ClaimsSummaryProviderPayment>? Recoupments { get; set; }
    public List<ClaimsSummaryProviderPayment>? AdvancedPayments { get; set; }

    public const int MaxEntries = 5000;

    public List<string> Validate(out DateOnly month)
    {
        var errors = new List<string>();
        if (!TexasClaimsLagReport.TryMonth(ClaimsMonth, out month)) errors.Add("claimsMonth must be yyyy-MM");
        if (!TexasClaimsSummaryCodes.McoCodes.Contains(McoCode ?? ""))
            errors.Add($"mcoCode must be one of HHSC's codes ({string.Join(", ", TexasClaimsSummaryCodes.McoCodes.Order())})");
        if (RateCellPrograms.Count == 0) errors.Add("rateCellPrograms must map at least one rate cell to an HHSC program code");
        foreach (var (cell, program) in RateCellPrograms)
            if (!TexasClaimsSummaryCodes.Programs.ContainsKey(program ?? ""))
                errors.Add($"Rate cell '{cell}': '{program}' is not an HHSC program code (01 to 09)");
        for (var i = 0; i < ClaimTypeRules.Count; i++)
        {
            var r = ClaimTypeRules[i];
            if (!TexasClaimsSummaryCodes.ClaimTypes.Contains(r.ClaimType ?? ""))
                errors.Add($"claimTypeRules[{i}]: '{r.ClaimType}' is not a claim type CHO builds from claims ({string.Join(", ", TexasClaimsSummaryCodes.ClaimTypes)})");
            if (r.ClaimForm != null && !Enum.TryParse<Models.ClaimType>(r.ClaimForm, true, out _))
                errors.Add($"claimTypeRules[{i}]: claimForm must be Professional, Institutional or Dental");
        }

        var entries = (RejectedClaims?.Count ?? 0) + (InterestPenalties?.Count ?? 0) + (Pharmacy?.Count ?? 0)
                      + (Recoupments?.Count ?? 0) + (AdvancedPayments?.Count ?? 0) + ClaimTypeRules.Count
                      + (AppealedClaimNumbers?.Count ?? 0) / 100;
        if (entries > MaxEntries) errors.Add($"At most {MaxEntries} entries in one request");

        foreach (var (name, list) in new[] { ("rejectedClaims", RejectedClaims), ("interestPenalties", InterestPenalties) })
            for (var i = 0; i < (list?.Count ?? 0); i++)
            {
                var e = list![i];
                if (!TexasClaimsSummaryCodes.Programs.ContainsKey(e.Program ?? "")) errors.Add($"{name}[{i}]: program must be 01 to 09");
                if (!TexasClaimsSummaryCodes.ClaimTypes.Contains(e.ClaimType ?? "")) errors.Add($"{name}[{i}]: claimType is not valid");
                if (e.ClaimForm is not ("U" or "C" or "N" or "A")) errors.Add($"{name}[{i}]: claimForm must be U, C, N or A");
                if (e.Claims < 0 || e.Amount < 0) errors.Add($"{name}[{i}]: claims and amount can't be negative");
            }

        for (var i = 0; i < (Pharmacy?.Count ?? 0); i++)
        {
            var p = Pharmacy![i];
            if (!TexasClaimsSummaryCodes.Programs.ContainsKey(p.Program ?? "")) errors.Add($"pharmacy[{i}]: program must be 01 to 09");
            else if (!TexasClaimsSummaryCodes.Reports(p.Program!, "PH")) errors.Add($"pharmacy[{i}]: program {p.Program} does not report pharmacy");
            if (Pharmacy.Count(x => x.Program == p.Program) > 1) errors.Add($"pharmacy[{i}]: program {p.Program} is entered more than once");
        }

        foreach (var (name, list, reasons) in new[]
                 {
                     ("recoupments", Recoupments, TexasClaimsSummaryCodes.RecoupmentReasons),
                     ("advancedPayments", AdvancedPayments, TexasClaimsSummaryCodes.AdvancedPaymentReasons),
                 })
            for (var i = 0; i < (list?.Count ?? 0); i++)
            {
                var p = list![i];
                if (!TexasClaimsSummaryCodes.Programs.ContainsKey(p.Program ?? "")) errors.Add($"{name}[{i}]: program must be 01 to 09");
                if (!reasons.ContainsKey(p.Reason ?? "")) errors.Add($"{name}[{i}]: reason must be one of {string.Join(", ", reasons.Keys)}");
                if (p.Reason == "99" && string.IsNullOrWhiteSpace(p.ReasonOther)) errors.Add($"{name}[{i}]: reasonOther is required when the reason is 99");
                if (string.IsNullOrWhiteSpace(p.FacilityName) && string.IsNullOrWhiteSpace(p.LastName))
                    errors.Add($"{name}[{i}]: give the provider's last name or the facility name");
                if (p.Amount <= 0) errors.Add($"{name}[{i}]: amount must be more than zero");
                if (p.Date == default) errors.Add($"{name}[{i}]: date is required (yyyy-MM-dd)");
                foreach (var (field, value, width) in new[] { ("firstName", p.FirstName, 50), ("lastName", p.LastName, 50), ("facilityName", p.FacilityName, 50), ("reasonOther", p.ReasonOther, 150) })
                    if (value != null && (value.Length > width || value.Any(c => c < 0x20 || c > 0x7E)))
                        errors.Add($"{name}[{i}]: {field} must be printable ASCII of at most {width} characters");
            }
        return errors;
    }
}

/// <summary>The figures of one line of sections B to D (UB04/CMS 1500, NF 837i or ADA).</summary>
public sealed class ClaimsSummaryFigures
{
    /// <summary>Paid clean claims, oldest bucket first: over 90 days, the middle bucket, within 30 (10 for NF 837i) days.</summary>
    public int[] CleanPaidClaims { get; set; } = new int[3];
    public decimal[] CleanPaidAmount { get; set; } = new decimal[3];
    public int[] CleanDeniedClaims { get; set; } = new int[3];
    /// <summary>Appealed claims, buckets over 90, 31 to 90 and within 30 days.</summary>
    public int[] AppealedPaidClaims { get; set; } = new int[3];
    public decimal[] AppealedPaidAmount { get; set; } = new decimal[3];
    public int[] AppealedDeniedClaims { get; set; } = new int[3];
    public int AdjustedClaims { get; set; }
    public decimal AdjustedPaid { get; set; }
    public int RejectedClaims { get; set; }
    public int DuplicateClaims { get; set; }
    public int DeficientDeniedClaims { get; set; }
    public int DeficientPendedClaims { get; set; }
    public int OtherUnprocessedClaims { get; set; }
    public int CapitatedClaims { get; set; }
    public int InterestClaims { get; set; }
    public decimal InterestPaid { get; set; }
    /// <summary>Whole days from receipt to adjudication, averaged over the claims processed in the month.</summary>
    public int AverageProcessingDays { get; set; }

    internal long ProcessingDays;
    internal int Processed;
}

public sealed record ClaimsSummaryLine(string Program, string ClaimType, string ClaimForm, ClaimsSummaryFigures Figures);

public sealed record ClaimsSummaryExclusion(string Reason, int Claims, IReadOnlyList<string> Examples);

public sealed record TexasClaimsSummary(
    DateOnly ClaimsMonth,
    int StateFiscalYear,
    string McoCode,
    IReadOnlyList<ClaimsSummaryLine> Lines,
    IReadOnlyList<ClaimsSummaryPharmacy> Pharmacy,
    IReadOnlyList<ClaimsSummaryProviderPayment> Recoupments,
    IReadOnlyList<ClaimsSummaryProviderPayment> AdvancedPayments,
    IReadOnlyList<ClaimsSummaryExclusion> Excluded,
    IReadOnlyList<string> Notes)
{
    public IReadOnlyList<string> Programs =>
        Lines.Select(l => l.Program).Concat(Pharmacy.Select(p => p.Program))
            .Concat(Recoupments.Select(r => r.Program)).Concat(AdvancedPayments.Select(a => a.Program))
            .Distinct().Order(StringComparer.Ordinal).ToList();
}

/// <summary>
/// HHSC's monthly Claims Summary Report (UMCM 5.24.1 v2.5): for each program,
/// claim type and claim form, the claims adjudicated in the claims month by
/// days from receipt, with adjusted, duplicate, deficient, unprocessed and
/// capitated counts, plus pharmacy figures, recoupments and advanced
/// payments, written as the fixed-width text file uploaded to TexConnect.
///
/// <para>
/// A claim version adjudicated in the month (Texas time) is: capitated if its
/// billing NPI is listed; else, when it is an adjustment (version 2 or
/// later), appealed if its claim number was appealed and adjusted otherwise;
/// else a duplicate or deficient denial by its denial code; else clean.
/// Paid means pay or paid, denied means deny or denied; voids count as
/// adjusted with nothing paid. Claims received by the month's end and not
/// adjudicated by then are deficient-pended (by pend code) or other
/// unprocessed. CHO never guesses a program: a claim whose member has no
/// mapped rate cell for the month of service is left out and counted.
/// </para>
/// </summary>
public static class TexasClaimsSummaryReport
{
    public static readonly TimeZoneInfo Central = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    public const string NoMember = "No member on the claim";
    public const string NotEnrolled = "Member has no expected member-month (rate cell) for the month of service";
    public const string UnmappedRateCell = "Member's rate cell is not mapped to an HHSC program";
    public const string TypeNotReported = "The claim type is not one the program reports (Claim Types Crosswalk)";

    private static readonly string[] DefaultDuplicateCodes = ["18"];
    private static readonly string[] DefaultDeficientCodes = ["16"];

    public static int StateFiscalYear(DateOnly month) => month.Month >= 9 ? month.Year + 1 : month.Year;

    /// <summary>The month's first and next month's first instants in Texas time, as UTC.</summary>
    public static (DateTime FromUtc, DateTime BeforeUtc) Window(DateOnly month)
    {
        static DateTime Utc(DateOnly d) => TimeZoneInfo.ConvertTimeToUtc(d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), Central);
        return (Utc(month), Utc(month.AddMonths(1)));
    }

    public static DateOnly TexasDate(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Central));

    public static TexasClaimsSummary Build(
        DateOnly month,
        TexasClaimsSummaryRequest request,
        IEnumerable<ClaimsSummaryClaim> adjudicated,
        IEnumerable<ClaimsSummaryClaim> open,
        IEnumerable<ExpectedRateCell> rateCells)
    {
        var (_, before) = Window(month);
        var cellOf = new Dictionary<(string, DateOnly), string>();
        foreach (var r in rateCells) cellOf[(r.MemberId, r.Month)] = r.RateCell.Trim();
        var programs = request.RateCellPrograms.ToDictionary(kv => kv.Key.Trim(), kv => kv.Value, StringComparer.Ordinal);
        var duplicateCodes = new HashSet<string>(request.DuplicateDenialCodes ?? [.. DefaultDuplicateCodes], StringComparer.OrdinalIgnoreCase);
        var deficientCodes = new HashSet<string>(request.DeficientDenialCodes ?? [.. DefaultDeficientCodes], StringComparer.OrdinalIgnoreCase);
        var deficientPends = new HashSet<string>(request.DeficientPendCodes ?? [], StringComparer.OrdinalIgnoreCase);
        var appealed = new HashSet<string>(request.AppealedClaimNumbers ?? [], StringComparer.Ordinal);
        var capitated = new HashSet<string>(request.CapitatedBillingNpis ?? [], StringComparer.Ordinal);

        var lines = new SortedDictionary<(string, string, string), ClaimsSummaryFigures>();
        var excluded = new Dictionary<string, (HashSet<string> Claims, SortedSet<string> Examples)>();
        var notes = new List<string>();

        (ClaimsSummaryFigures? Line, string Form) LineFor(ClaimsSummaryClaim c)
        {
            string? reason = null, example = null, program = null;
            var serviceMonth = new DateOnly(c.ServiceDateFrom.Year, c.ServiceDateFrom.Month, 1);
            if (string.IsNullOrEmpty(c.MemberId)) reason = NoMember;
            else if (!cellOf.TryGetValue((c.MemberId, serviceMonth), out var cell)) reason = NotEnrolled;
            else if (!programs.TryGetValue(cell, out program)) { reason = UnmappedRateCell; example = cell; }

            var type = request.ClaimTypeRules.FirstOrDefault(r => r.Matches(c))?.ClaimType
                       ?? (c.Form == Models.ClaimType.Dental ? "DT" : "AC");
            if (reason == null && !TexasClaimsSummaryCodes.Reports(program!, type)) { reason = TypeNotReported; example = $"{program}/{type}"; }

            if (reason != null)
            {
                if (!excluded.TryGetValue(reason, out var e)) excluded[reason] = e = ([], new SortedSet<string>(StringComparer.Ordinal));
                e.Claims.Add(c.ClaimNumber);
                if (example != null && e.Examples.Count < 20) e.Examples.Add(example);
                return (null, "");
            }
            var key = (program!, type, TexasClaimsSummaryCodes.Form(type, c.Form));
            if (!lines.TryGetValue(key, out var f)) lines[key] = f = new ClaimsSummaryFigures();
            return (f, key.Item3);
        }

        foreach (var c in adjudicated)
        {
            if (c.AdjudicatedAt is not { } at) continue;
            var (f, form) = LineFor(c);
            if (f is null) continue;

            var paid = c.Status is Models.ClaimStatus.Approved or Models.ClaimStatus.Paid or Models.ClaimStatus.PartiallyPaid;
            var denied = c.Status is Models.ClaimStatus.Deny or Models.ClaimStatus.Denied;
            var voided = c.Status == Models.ClaimStatus.Voided;
            if (!paid && !denied && !voided) continue;

            var days = Math.Max(0, TexasDate(at).DayNumber - TexasDate(c.ReceivedAt).DayNumber);
            f.ProcessingDays += days;
            f.Processed++;

            if (c.BillingProviderNpi != null && capitated.Contains(c.BillingProviderNpi)) { f.CapitatedClaims++; continue; }

            if (c.VersionNumber > 1 || voided)
            {
                if (!voided && appealed.Contains(c.ClaimNumber))
                {
                    var b = Bucket(days, 30);
                    if (paid) { f.AppealedPaidClaims[b]++; f.AppealedPaidAmount[b] += c.PlanPaid; }
                    else f.AppealedDeniedClaims[b]++;
                }
                else
                {
                    f.AdjustedClaims++;
                    if (paid) f.AdjustedPaid += c.PlanPaid;
                }
                continue;
            }

            if (denied && (c.ExactDuplicateFound || (c.DenialReasonCode != null && duplicateCodes.Contains(c.DenialReasonCode)))) { f.DuplicateClaims++; continue; }
            if (denied && c.DenialReasonCode != null && deficientCodes.Contains(c.DenialReasonCode)) { f.DeficientDeniedClaims++; continue; }

            // NF 837i clean claims are bucketed at 10 days, the others at 30.
            var clean = Bucket(days, form == "N" ? 10 : 30);
            if (paid) { f.CleanPaidClaims[clean]++; f.CleanPaidAmount[clean] += c.PlanPaid; }
            else f.CleanDeniedClaims[clean]++;
        }

        foreach (var c in open)
        {
            if (c.ReceivedAt >= before || c.Status == Models.ClaimStatus.Voided) continue;
            if (c.AdjudicatedAt is { } at && at < before) continue;
            var (f, _) = LineFor(c);
            if (f is null) continue;
            if (c.Status == Models.ClaimStatus.Pended && c.PendCode != null && deficientPends.Contains(c.PendCode)) f.DeficientPendedClaims++;
            else f.OtherUnprocessedClaims++;
        }

        foreach (var (list, apply) in new (List<ClaimsSummaryEnteredCount>?, Action<ClaimsSummaryFigures, ClaimsSummaryEnteredCount>)[]
                 {
                     (request.RejectedClaims, (f, e) => f.RejectedClaims += e.Claims),
                     (request.InterestPenalties, (f, e) => { f.InterestClaims += e.Claims; f.InterestPaid += e.Amount; }),
                 })
            foreach (var e in list ?? [])
            {
                var key = (e.Program, e.ClaimType, e.ClaimForm);
                if (!lines.TryGetValue(key, out var f)) lines[key] = f = new ClaimsSummaryFigures();
                apply(f, e);
            }

        foreach (var f in lines.Values)
            f.AverageProcessingDays = f.Processed == 0 ? 0 : (int)Math.Round((decimal)f.ProcessingDays / f.Processed, MidpointRounding.AwayFromZero);

        if (excluded.Count > 0)
            notes.Add("Some claims were left out (see excluded); check the rate cell mapping, the claim type rules and the expected member-months loaded");
        if (request.InterestPenalties is null)
            notes.Add("No interest penalties were entered: CHO does not compute prompt-pay interest, so interest fields are zero");
        if (request.RejectedClaims is null)
            notes.Add("No rejected claims were entered: claims rejected before intake (999/277CA) are not in the claim store, so rejected counts are zero");
        if (request.AppealedClaimNumbers is null)
            notes.Add("No appealed claim numbers were entered: every adjustment is reported as adjusted, none as appealed");
        if (request.DeficientPendCodes is null)
            notes.Add("No deficient pend codes were given: every claim open at month end is reported as other unprocessed");
        foreach (var r in request.Recoupments ?? [])
            if (r.Amount < 50_000m)
                notes.Add($"A recoupment of {r.Amount.ToString("0.00", CultureInfo.InvariantCulture)} is under the $50,000 HHSC asks to be listed; it is still written");

        return new TexasClaimsSummary(month, StateFiscalYear(month), request.McoCode,
            lines.Select(l => new ClaimsSummaryLine(l.Key.Item1, l.Key.Item2, l.Key.Item3, l.Value)).ToList(),
            (request.Pharmacy ?? []).OrderBy(p => p.Program, StringComparer.Ordinal).ToList(),
            request.Recoupments ?? [], request.AdvancedPayments ?? [],
            excluded.Select(e => new ClaimsSummaryExclusion(e.Key, e.Value.Claims.Count, e.Value.Examples.ToList()))
                .OrderByDescending(e => e.Claims).ToList(),
            notes);
    }

    /// <summary>0: over 90 days, 1: more than <paramref name="low"/> up to 90, 2: within <paramref name="low"/>.</summary>
    private static int Bucket(int days, int low) => days > 90 ? 0 : days > low ? 1 : 2;

    // ── The text file ─────────────────────────────────────────────────────

    private static readonly string[] Buckets = ["Over90", "Mid", "Low"];

    private static IEnumerable<FixedWidthField> ClaimSection(string p, int start)
    {
        var at = start;
        FixedWidthField F(string name, int width) { var f = new FixedWidthField(p + name, at, width); at += width; return f; }
        foreach (var b in Buckets) { yield return F($"CleanPaidClaims{b}", 7); yield return F($"CleanPaidAmount{b}", 15); }
        foreach (var b in Buckets) yield return F($"CleanDeniedClaims{b}", 7);
        foreach (var b in Buckets) { yield return F($"AppealedPaidClaims{b}", 7); yield return F($"AppealedPaidAmount{b}", 15); }
        foreach (var b in Buckets) yield return F($"AppealedDeniedClaims{b}", 7);
        yield return F("AdjustedClaims", 7);
        yield return F("AdjustedPaid", 15);
        foreach (var n in new[] { "Rejected", "Duplicate", "DeficientDenied", "DeficientPended", "OtherUnprocessed", "Capitated", "InterestClaims" })
            yield return F(n, 7);
        yield return F("InterestPaid", 15);
        yield return F("AverageProcessingDays", 5);
    }

    private static IEnumerable<FixedWidthField> PaymentSection(string p, int start) =>
    [
        new(p + "FirstName", start, 50), new(p + "LastName", start + 50, 50), new(p + "FacilityName", start + 100, 50),
        new(p + "Reason", start + 150, 2), new(p + "ReasonOther", start + 152, 150), new(p + "Amount", start + 302, 15),
        new(p + "Date", start + 317, 8),
    ];

    private static readonly string[] PharmacyFields =
    [
        "ElectronicPaidOver18Claims", "ElectronicPaidOver18Amount", "ElectronicPaidWithin18Claims", "ElectronicPaidWithin18Amount",
        "ElectronicRejectedOver18Claims", "ElectronicRejectedWithin18Claims",
        "NonElectronicPaidOver21Claims", "NonElectronicPaidOver21Amount", "NonElectronicPaidWithin21Claims", "NonElectronicPaidWithin21Amount",
        "NonElectronicRejectedOver21Claims", "NonElectronicRejectedWithin21Claims", "InterestClaims", "InterestAmount",
    ];

    /// <summary>The 5.24.1 v2.5 record: 1,603 characters, every section on each line, unused ones blank.</summary>
    public static readonly IReadOnlyList<FixedWidthField> Layout = FixedWidthRecord.Layout(
    [
        new("Sequence", 1, 6), new("McoName", 7, 3), new("ClaimsMonth", 10, 2), new("StateFiscalYear", 12, 4),
        new("Program", 16, 2), new("ClaimType", 18, 2), new("ClaimForm", 20, 1),
        .. ClaimSection("B.", 21), .. ClaimSection("C.", 286), .. ClaimSection("D.", 551),
        .. PharmacyFields.Select((n, i) => new FixedWidthField("E." + n, 816 + PharmacyOffset(i), n.EndsWith("Amount") ? 15 : 7)),
        .. PaymentSection("F.", 954), .. PaymentSection("G.", 1279),
    ]);

    private static int PharmacyOffset(int index) =>
        PharmacyFields.Take(index).Sum(n => n.EndsWith("Amount") ? 15 : 7);

    public static string Count(int n) => n.ToString(CultureInfo.InvariantCulture);
    public static string Money(decimal d) => Math.Round(d, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// One program's text file: a line per claim type and form, then
    /// pharmacy, recoupments and advanced payments, numbered from 1. Lines
    /// end in CRLF. Throws <see cref="ArgumentException"/> when a figure
    /// doesn't fit its field.
    /// </summary>
    public static string Text(TexasClaimsSummary report, string program)
    {
        var sb = new StringBuilder();
        var seq = 0;
        FixedWidthRecord Head(string type, string? form)
        {
            var r = new FixedWidthRecord(Layout)
                .Set("Sequence", (++seq).ToString("000000", CultureInfo.InvariantCulture))
                .Set("McoName", report.McoCode)
                .Set("ClaimsMonth", report.ClaimsMonth.Month.ToString("00", CultureInfo.InvariantCulture))
                .Set("StateFiscalYear", report.StateFiscalYear.ToString("0000", CultureInfo.InvariantCulture))
                .Set("Program", program)
                .Set("ClaimType", type)
                .Set("ClaimForm", form);
            return r;
        }

        foreach (var line in report.Lines.Where(l => l.Program == program))
        {
            var p = line.ClaimForm switch { "N" => "C.", "A" => "D.", _ => "B." };
            var f = line.Figures;
            var r = Head(line.ClaimType, line.ClaimForm);
            for (var i = 0; i < 3; i++)
            {
                r.Set($"{p}CleanPaidClaims{Buckets[i]}", Count(f.CleanPaidClaims[i]))
                 .Set($"{p}CleanPaidAmount{Buckets[i]}", Money(f.CleanPaidAmount[i]))
                 .Set($"{p}CleanDeniedClaims{Buckets[i]}", Count(f.CleanDeniedClaims[i]))
                 .Set($"{p}AppealedPaidClaims{Buckets[i]}", Count(f.AppealedPaidClaims[i]))
                 .Set($"{p}AppealedPaidAmount{Buckets[i]}", Money(f.AppealedPaidAmount[i]))
                 .Set($"{p}AppealedDeniedClaims{Buckets[i]}", Count(f.AppealedDeniedClaims[i]));
            }
            r.Set($"{p}AdjustedClaims", Count(f.AdjustedClaims)).Set($"{p}AdjustedPaid", Money(f.AdjustedPaid))
             .Set($"{p}Rejected", Count(f.RejectedClaims)).Set($"{p}Duplicate", Count(f.DuplicateClaims))
             .Set($"{p}DeficientDenied", Count(f.DeficientDeniedClaims)).Set($"{p}DeficientPended", Count(f.DeficientPendedClaims))
             .Set($"{p}OtherUnprocessed", Count(f.OtherUnprocessedClaims)).Set($"{p}Capitated", Count(f.CapitatedClaims))
             .Set($"{p}InterestClaims", Count(f.InterestClaims)).Set($"{p}InterestPaid", Money(f.InterestPaid))
             .Set($"{p}AverageProcessingDays", Count(f.AverageProcessingDays));
            sb.Append(r).Append("\r\n");
        }

        foreach (var ph in report.Pharmacy.Where(x => x.Program == program))
        {
            var r = Head("PH", "P");
            foreach (var n in PharmacyFields)
            {
                var v = typeof(ClaimsSummaryPharmacy).GetProperty(n)!.GetValue(ph)!;
                r.Set("E." + n, v is decimal d ? Money(d) : Count((int)v));
            }
            sb.Append(r).Append("\r\n");
        }

        foreach (var (type, prefix, list) in new[] { ("RC", "F.", report.Recoupments), ("AP", "G.", report.AdvancedPayments) })
            foreach (var e in list.Where(x => x.Program == program))
            {
                var r = Head(type, null)
                    .Set(prefix + "FirstName", e.FirstName).Set(prefix + "LastName", e.LastName).Set(prefix + "FacilityName", e.FacilityName)
                    .Set(prefix + "Reason", e.Reason).Set(prefix + "ReasonOther", e.Reason == "99" ? e.ReasonOther : null)
                    .Set(prefix + "Amount", Money(e.Amount)).Set(prefix + "Date", e.Date.ToString("MMddyyyy", CultureInfo.InvariantCulture));
                sb.Append(r).Append("\r\n");
            }
        return sb.ToString();
    }

    public static string FileName(TexasClaimsSummary report, string program) =>
        $"ClaimsSummary-{report.McoCode}-{program}-SFY{report.StateFiscalYear}-{report.ClaimsMonth:MM}.txt";
}
