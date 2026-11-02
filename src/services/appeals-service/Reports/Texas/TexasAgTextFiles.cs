using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AppealsService.Models;
using static AppealsService.Models.AppealInsightSets;

namespace AppealsService.Reports.Texas;

/// <summary>Which HHSC monthly text file.</summary>
public enum TexasAgFile
{
    /// <summary>UMCM 5.24.6 Member Appeals Report Text File Layout (v2.7, effective 2024-09-01).</summary>
    MemberAppeals,
    /// <summary>UMCM 5.24.5 Member and Provider Complaints Text File Layout.</summary>
    Complaints,
}

/// <summary>What the plan reports the file as: the reporting month, program and its HHSC codes.</summary>
public sealed record TexasAgFileRequest(int Year, int Month, string Program, string McoCode, string PlanCode);

/// <summary>A case in the file.</summary>
public sealed record TexasAgFileRow(int Sequence, string TrackingId, string AppealId, string MemberId, bool Pending);

/// <summary>A case the file should carry but can't yet: what is missing or wrong. It is left out of the file.</summary>
public sealed record TexasAgFileProblem(string AppealId, string AppealNumber, string Field, string Problem);

public sealed record TexasAgFileResult(
    TexasAgFile File,
    TexasAgFileRequest Request,
    int StateFiscalYear,
    string Text,
    IReadOnlyList<TexasAgFileRow> Items,
    IReadOnlyList<TexasAgFileProblem> Problems)
{
    /// <summary>Every case that belongs in the file is in it.</summary>
    public bool Complete => Problems.Count == 0;
}

/// <summary>
/// Builds HHSC's monthly member appeals (UMCM 5.24.6) and member and provider
/// complaints (UMCM 5.24.5) fixed-width text files for one program, uploaded
/// by the plan to TexConnect, due 45 days after the reporting month.
///
/// A file carries every case of its kind received by the end of the month
/// that was not resolved before the month began: those resolved in the month
/// with their resolution date, the rest as pending (no date; PN / PND),
/// under the same tracking id every month. Dates are Texas (Central) calendar
/// dates. Complaints are CHO grievances; appeals are every other case type.
///
/// What CHO records maps across (dates, urgency, the 14-day extension and
/// who asked for it, the outcome of a decided case); HHSC's own codes come
/// from the case's <see cref="TexasAgCodes"/>. A case missing a code the
/// layout requires is not written (a malformed row would fail the upload)
/// and is listed in <see cref="TexasAgFileResult.Problems"/> instead.
/// </summary>
public static class TexasAgTextFiles
{
    public static readonly TimeZoneInfo Central = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    public static readonly IReadOnlyList<FixedWidthField> AppealsLayout = FixedWidthRecord.Layout(
        new("Sequence", 1, 6), new("McoName", 7, 3), new("ReportingMonth", 10, 2), new("StateFiscalYear", 12, 4),
        new("Program", 16, 2), new("PlanCode", 18, 2), new("TrackingId", 20, 15), new("MemberId", 35, 9),
        new("DateReceived", 44, 8), new("MethodReceived", 52, 1), new("ReceivedBy", 53, 2), new("ReceivedBySubcontractor", 55, 50),
        new("BenefitType", 105, 2), new("OtherBenefitType", 107, 15), new("ServicesImpacted", 122, 3), new("OtherServicesImpacted", 125, 15),
        new("ReasonForDenial", 140, 3), new("OtherReasonForDenial", 143, 15), new("AppealTimeframe", 158, 2), new("ExtensionNeeded", 160, 1),
        new("ExtensionRequestedBy", 161, 2), new("ResolutionDate", 163, 8), new("AdverseBenefitDetermination", 171, 2),
        new("OtherAdverseBenefitDetermination", 173, 15), new("AppealOutcome", 188, 2), new("OverturnedOutcome", 190, 3),
        new("PriorAuthorizationNumber", 193, 50));

    public static readonly IReadOnlyList<FixedWidthField> ComplaintsLayout = FixedWidthRecord.Layout(
        new("Sequence", 1, 6), new("McoName", 7, 3), new("ReportingMonth", 10, 2), new("StateFiscalYear", 12, 4),
        new("Program", 16, 2), new("PlanCode", 18, 2), new("TrackingId", 20, 15), new("ComplaintType", 35, 1),
        new("Complainant", 36, 1), new("OtherComplainant", 37, 10), new("MemberId", 47, 9), new("Mdcp", 56, 1),
        new("ProviderNpi", 57, 10), new("DateReceived", 67, 8), new("MethodReceived", 75, 1), new("ReceivedBy", 76, 2),
        new("ReceivedBySubcontractor", 78, 50), new("InitialContactComplaint", 128, 1), new("BenefitType", 129, 2),
        new("Disposition", 131, 5), new("OtherDispositionDetail", 136, 50), new("ComplaintCategory", 186, 2),
        new("ComplaintCategoryDetail", 188, 4), new("OtherComplaintCategoryDetail", 192, 50), new("DisasterRelated", 242, 1),
        new("DateResolved", 243, 8), new("ComplaintOutcome", 251, 3));

    private static readonly Regex AppealNumberPattern = new(@"^APL-\d{2}(\d{6})-([A-Z0-9]{8})$", RegexOptions.CultureInvariant);
    private static readonly Regex NineDigits = new(@"^\d{9}$", RegexOptions.CultureInvariant);
    private static readonly Regex TenDigits = new(@"^\d{10}$", RegexOptions.CultureInvariant);
    private static readonly Regex TwoAlnum = new(@"^[A-Za-z0-9]{2}$", RegexOptions.CultureInvariant);

    /// <summary>Texas state fiscal year: September through August, named for the year it ends.</summary>
    public static int StateFiscalYear(int year, int month) => month >= 9 ? year + 1 : year;

    /// <summary>The first instant of the month and of the next, in UTC, for Texas calendar months.</summary>
    public static (DateTime Start, DateTime End) MonthUtc(int year, int month) =>
        (TimeZoneInfo.ConvertTimeToUtc(new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Unspecified), Central),
         TimeZoneInfo.ConvertTimeToUtc(new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMonths(1), Central));

    public static DateOnly TexasDate(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Central));

    /// <summary>The request's problems, or none.</summary>
    public static IEnumerable<string> Validate(TexasAgFileRequest r)
    {
        if (r.Month is < 1 or > 12 || r.Year is < 2000 or > 2100) yield return "month must be a calendar month, yyyy-MM.";
        if (!TexasAgCodeLists.Programs.ContainsKey(r.Program ?? "")) yield return "program must be an HHSC program code (01-07, 10).";
        if (!TexasAgCodeLists.McoCodes.Contains(r.McoCode ?? "")) yield return "mcoCode must be an HHSC MCO/DMO name code.";
        if (!TwoAlnum.IsMatch(r.PlanCode ?? "")) yield return "planCode must be the 2-character HHSC plan code, leading zero kept.";
    }

    /// <summary>Kind of the case for the file: grievances are complaints.</summary>
    public static TexasAgFile FileFor(Appeal a) => IsGrievance(a) ? TexasAgFile.Complaints : TexasAgFile.MemberAppeals;

    /// <summary>
    /// Received by the end of the month and not resolved before it began.
    /// A case closed without a resolution date counts as resolved when it closed.
    /// </summary>
    public static bool BelongsInMonth(Appeal a, DateTime monthStartUtc, DateTime monthEndUtc) =>
        a.Status != AppealStatus.Draft
        && ReceivedAt(a) < monthEndUtc
        && !(Resolved(a) is { } r && r < monthStartUtc);

    private static DateTime? Resolved(Appeal a) => a.Status == AppealStatus.Closed ? DecidedAt(a) ?? a.UpdatedAt : null;

    /// <param name="cases">The tenant's cases that belong in the month (<see cref="BelongsInMonth"/>), any program.</param>
    /// <param name="codes">Each case's Texas codes, by appeal id.</param>
    public static TexasAgFileResult Build(TexasAgFile file, TexasAgFileRequest request, IEnumerable<Appeal> cases,
        IReadOnlyDictionary<string, TexasAgCodes> codes)
    {
        var (start, end) = MonthUtc(request.Year, request.Month);
        var sfy = StateFiscalYear(request.Year, request.Month);
        var lines = new List<string>();
        var items = new List<TexasAgFileRow>();
        var problems = new List<TexasAgFileProblem>();

        foreach (var a in cases
                     .Where(a => FileFor(a) == file && BelongsInMonth(a, start, end))
                     .OrderBy(ReceivedAt).ThenBy(a => a.Id, StringComparer.Ordinal))
        {
            codes.TryGetValue(a.Id, out var c);
            if (c?.Program is null)
            {
                // A Medicaid case with no program can't be placed in any program's file: say so.
                if (a.LineOfBusiness == LineOfBusiness.Medicaid)
                    problems.Add(new(a.Id, a.AppealNumber, "Program", "No HHSC program recorded for this Medicaid case; it is in no program's file."));
                continue;
            }
            if (c.Program != request.Program) continue;

            var issues = new List<(string Field, string Problem)>();
            var record = new FixedWidthRecord(file == TexasAgFile.MemberAppeals ? AppealsLayout : ComplaintsLayout);
            var resolvedInMonth = Resolved(a) is { } r && r >= start && r < end ? r : (DateTime?)null;
            var tracking = TrackingId(a, c);
            if (tracking is null) issues.Add(("TrackingId", "The appeal number does not fit 15 characters; record a tracking id."));

            var sequence = lines.Count + 1;
            void Put(string field, string? value)
            {
                try { record.Set(field, value); }
                catch (ArgumentException ex) { issues.Add((field, ex.Message)); }
            }

            Put("Sequence", sequence.ToString("000000", CultureInfo.InvariantCulture));
            Put("McoName", request.McoCode);
            Put("ReportingMonth", request.Month.ToString("00", CultureInfo.InvariantCulture));
            Put("StateFiscalYear", sfy.ToString(CultureInfo.InvariantCulture));
            Put("Program", request.Program);
            Put("PlanCode", request.PlanCode);
            Put("TrackingId", tracking);
            Put("DateReceived", Mmddyyyy(ReceivedAt(a)));
            Put("MethodReceived", Required(issues, "MethodReceived", c.MethodReceived, TexasAgCodeLists.MethodReceived));
            Put("ReceivedBy", Required(issues, "ReceivedBy", c.ReceivedBy, TexasAgCodeLists.ReceivedBy));
            if (c.ReceivedBy == "03") Put("ReceivedBySubcontractor", Text(issues, "ReceivedBySubcontractor", c.ReceivedBySubcontractor));

            if (file == TexasAgFile.MemberAppeals) Appeal(a, c, resolvedInMonth, Put, issues);
            else Complaint(a, c, resolvedInMonth, Put, issues);

            if (issues.Count > 0)
            {
                problems.AddRange(issues.Select(i => new TexasAgFileProblem(a.Id, a.AppealNumber, i.Field, i.Problem)));
                continue;
            }
            lines.Add(record.ToString());
            items.Add(new(sequence, tracking!, a.Id, a.MemberId, resolvedInMonth is null));
        }

        // No cases: HHSC wants a blank file.
        var text = lines.Count == 0 ? string.Empty : string.Join("\r\n", lines) + "\r\n";
        return new(file, request, sfy, text, items, problems);
    }

    private static void Appeal(Appeal a, TexasAgCodes c, DateTime? resolved, Action<string, string?> put, List<(string, string)> issues)
    {
        put("MemberId", StateMemberId(issues, c));
        put("BenefitType", Required(issues, "BenefitType", c.BenefitType, TexasAgCodeLists.AppealBenefitTypes));
        if (c.BenefitType == "OH") put("OtherBenefitType", Text(issues, "OtherBenefitType", c.OtherBenefitType));
        put("ServicesImpacted", Optional(issues, "ServicesImpacted", c.ServicesImpacted, TexasAgCodeLists.ServicesImpacted));
        if (c.ServicesImpacted == "OTH") put("OtherServicesImpacted", Text(issues, "OtherServicesImpacted", c.OtherServicesImpacted));
        put("ReasonForDenial", Required(issues, "ReasonForDenial", c.ReasonForDenial, TexasAgCodeLists.ReasonsForDenial));
        if (c.ReasonForDenial == "OTH") put("OtherReasonForDenial", Text(issues, "OtherReasonForDenial", c.OtherReasonForDenial));
        put("AppealTimeframe", Optional(issues, "AppealTimeframe", c.AppealTimeframe, TexasAgCodeLists.AppealTimeframes) ?? (a.IsUrgent ? "72" : "30"));

        var extended = a.DeadlineExtension is not null;
        put("ExtensionNeeded", extended ? "Y" : "N");
        put("ExtensionRequestedBy", Optional(issues, "ExtensionRequestedBy", c.ExtensionRequestedBy, TexasAgCodeLists.ExtensionRequestedBy)
            ?? (a.DeadlineExtension?.Reason switch
            {
                AppealExtensionReason.EnrolleeRequested => "03",
                AppealExtensionReason.PlanNeedsInfo => "01",
                _ => "00",
            }));

        put("AdverseBenefitDetermination", Required(issues, "AdverseBenefitDetermination", c.AdverseBenefitDetermination, TexasAgCodeLists.AdverseBenefitDeterminations));
        if (c.AdverseBenefitDetermination == "OD")
            put("OtherAdverseBenefitDetermination", Text(issues, "OtherAdverseBenefitDetermination", c.OtherAdverseBenefitDetermination));
        put("PriorAuthorizationNumber", c.PriorAuthorizationNumber);

        if (resolved is null)
        {
            put("AppealOutcome", "PN");
            return;
        }
        put("ResolutionDate", Mmddyyyy(resolved.Value));
        var outcome = Optional(issues, "AppealOutcome", c.AppealOutcome, TexasAgCodeLists.AppealOutcomes) ?? a.ClosureReasonCode switch
        {
            AppealClosureReasonCode.Approved => "OV",
            AppealClosureReasonCode.PartialApproval => "PU",
            AppealClosureReasonCode.Denied => "UP",
            AppealClosureReasonCode.Withdrawn => "WD",
            _ => null,
        };
        if (outcome is null) issues.Add(("AppealOutcome", $"Closed as {a.ClosureReasonCode?.ToString() ?? "no reason"}; record the HHSC outcome (UP, OV, PU, WD or DM)."));
        put("AppealOutcome", outcome);
        if (outcome == "OV")
            put("OverturnedOutcome", Required(issues, "OverturnedOutcome", c.OverturnedOutcome, TexasAgCodeLists.OverturnedOutcomes));
    }

    private static void Complaint(Appeal a, TexasAgCodes c, DateTime? resolved, Action<string, string?> put, List<(string, string)> issues)
    {
        var type = Optional(issues, "ComplaintType", c.ComplaintType, TexasAgCodeLists.ComplaintTypes) ?? "M";
        put("ComplaintType", type);
        put("Complainant", Required(issues, "Complainant", c.Complainant, TexasAgCodeLists.Complainants));
        if (c.Complainant == "O")
        {
            var other = Text(issues, "OtherComplainant", c.OtherComplainant);
            if (other is not null && !other.All(ch => char.IsAsciiLetter(ch) || ch == ' '))
                issues.Add(("OtherComplainant", "Letters and spaces only (HHSC field type A)."));
            else put("OtherComplainant", other);
        }
        if (type == "M")
        {
            put("MemberId", StateMemberId(issues, c));
            put("Mdcp", Required(issues, "Mdcp", c.Mdcp, TexasAgCodeLists.YesNo));
        }
        else
        {
            put("ProviderNpi", TenDigits.IsMatch(a.ProviderNPI ?? "") ? a.ProviderNPI : "9999999999");
        }

        put("BenefitType", Required(issues, "BenefitType", c.BenefitType, TexasAgCodeLists.ComplaintBenefitTypes));
        var category = Required(issues, "ComplaintCategory", c.ComplaintCategory, TexasAgCodeLists.ComplaintCategories);
        put("ComplaintCategory", category);
        var detail = Required(issues, "ComplaintCategoryDetail", c.ComplaintCategoryDetail, TexasAgCodeLists.ComplaintCategoryDetails);
        // Each detail code starts with its category's code.
        if (detail is not null && category is not null && !detail.StartsWith(category, StringComparison.Ordinal))
            issues.Add(("ComplaintCategoryDetail", $"{detail} is not a {category} detail."));
        put("ComplaintCategoryDetail", detail);
        if (detail is not null && TexasAgCodeLists.OtherCategoryDetails.Contains(detail))
            put("OtherComplaintCategoryDetail", Text(issues, "OtherComplaintCategoryDetail", c.OtherComplaintCategoryDetail));
        put("DisasterRelated", Optional(issues, "DisasterRelated", c.DisasterRelated, TexasAgCodeLists.YesNo) ?? "N");

        if (resolved is null)
        {
            put("InitialContactComplaint", "N");
            put("ComplaintOutcome", "PND");
            return;
        }

        put("DateResolved", Mmddyyyy(resolved.Value));
        put("InitialContactComplaint", WithinOneBusinessDay(ReceivedAt(a), resolved.Value) ? "Y" : "N");
        var disposition = Required(issues, "Disposition", c.Disposition, TexasAgCodeLists.Dispositions);
        put("Disposition", disposition);
        if (disposition == TexasAgCodeLists.OtherDisposition)
            put("OtherDispositionDetail", Text(issues, "OtherDispositionDetail", c.OtherDispositionDetail));
        put("ComplaintOutcome", a.ClosureReasonCode == AppealClosureReasonCode.Withdrawn
            ? "WTD"
            : Required(issues, "ComplaintOutcome", c.ComplaintOutcome, TexasAgCodeLists.ComplaintOutcomes));
    }

    /// <summary>
    /// Resolved by the end of the next Texas business day after receipt.
    /// Weekends are skipped; state holidays are not (they are not in CHO's calendar here).
    /// </summary>
    public static bool WithinOneBusinessDay(DateTime receivedUtc, DateTime resolvedUtc)
    {
        var next = TexasDate(receivedUtc).AddDays(1);
        while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) next = next.AddDays(1);
        return TexasDate(resolvedUtc) <= next;
    }

    /// <summary>The recorded tracking id, else one from the appeal number: APL-yyyyMMdd-XXXXXXXX becomes yyMMddXXXXXXXX.</summary>
    public static string? TrackingId(Appeal a, TexasAgCodes c)
    {
        if (!string.IsNullOrWhiteSpace(c.TrackingId)) return c.TrackingId.Trim();
        var number = (a.AppealNumber ?? string.Empty).Trim();
        if (AppealNumberPattern.Match(number) is { Success: true } m) return m.Groups[1].Value + m.Groups[2].Value;
        var compact = new string(number.Where(char.IsAsciiLetterOrDigit).ToArray());
        return compact.Length is > 0 and <= 15 ? compact : null;
    }

    private static string Mmddyyyy(DateTime utc) => TexasDate(utc).ToString("MMddyyyy", CultureInfo.InvariantCulture);

    private static string? StateMemberId(List<(string, string)> issues, TexasAgCodes c)
    {
        if (c.StateMemberId is { } id && NineDigits.IsMatch(id)) return id;
        issues.Add(("StateMemberId", "The member's 9-digit Medicaid/CHIP number is required."));
        return null;
    }

    private static string? Required(List<(string, string)> issues, string field, string? value, IReadOnlySet<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(value)) { issues.Add((field, "Required.")); return null; }
        return Optional(issues, field, value, allowed);
    }

    private static string? Optional(List<(string, string)> issues, string field, string? value, IReadOnlySet<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (allowed.Contains(v)) return v;
        issues.Add((field, $"'{v}' is not an HHSC code for this field."));
        return null;
    }

    private static string? Text(List<(string, string)> issues, string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) { issues.Add((field, "Required for the code chosen.")); return null; }
        return value.Trim();
    }
}
