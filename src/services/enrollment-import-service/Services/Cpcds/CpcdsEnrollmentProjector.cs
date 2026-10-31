using System.Globalization;
using CloudHealthOffice.Infrastructure.Import.Cpcds;
using EnrollmentImportService.Models;

namespace EnrollmentImportService.Services.Cpcds;

/// <summary>
/// Turns a core's CPCDS member file (and, optionally, its coverage file) into
/// the enrollment shape the 834 import already processes, so members and
/// coverage loaded from a CPCDS extract go through exactly the same member,
/// sponsor, plan-code and coverage reconciliation as an 834.
///
/// <para>
/// Every CPCDS member becomes its own enrollment, filed under the core's own
/// member id (<see cref="MemberEnrollment.ExplicitMemberId"/>), dependents
/// included. That id is the one the core's CPCDS claims carry, so the claims
/// importer's pass-through member crosswalk finds the member without a
/// separate mapping table. Each enrollment is a "001" change: create the
/// member when new, otherwise update, so loading the same file every night
/// is safe.
/// </para>
///
/// <para>
/// Coverage still needs the tenant's plan-code mapping (group, insurance line
/// and the core's plan identifier → CHO benefit plan), exactly as an 834 does.
/// The gap report lists the codes that are not mapped yet.
/// </para>
/// </summary>
public static class CpcdsEnrollmentProjector
{
    /// <summary>The member-file columns read, CPCDS name first, then alternatives.</summary>
    internal static readonly IReadOnlyDictionary<string, string[]> MemberColumns = new Dictionary<string, string[]>
    {
        ["MemberId"] = ["Member id", "Member identifier"],
        ["Name"] = ["Name", "Member name"],
        ["FirstName"] = ["First name", "Given name"],
        ["LastName"] = ["Last name", "Family name"],
        ["MiddleName"] = ["Middle name"],
        ["DateOfBirth"] = ["Date of birth", "Birth date"],
        ["Gender"] = ["Gender code", "Administrative gender", "Gender"],
        ["Address"] = ["Home_Address line", "Home address line", "Address line", "Address"],
        ["City"] = ["Home_City", "Home city", "City"],
        ["State"] = ["Home_State", "Home state", "State"],
        ["Zip"] = ["Home_Zip code", "Home zip code", "Zip code", "Postal code"],
        ["SubscriberId"] = ["Subscriber id"],
        ["Relationship"] = ["Relationship to subscriber"],
    };

    /// <summary>The coverage-file columns read.</summary>
    internal static readonly IReadOnlyDictionary<string, string[]> CoverageColumns = new Dictionary<string, string[]>
    {
        ["CoverageId"] = ["Coverage id"],
        ["MemberId"] = ["Member id", "Member identifier"],
        ["SubscriberId"] = ["Subscriber id"],
        ["CoverageType"] = ["Coverage type"],
        ["Status"] = ["Coverage status"],
        ["StartDate"] = ["Start date", "Coverage start date"],
        ["EndDate"] = ["End date", "Coverage end date"],
        ["GroupId"] = ["Group id"],
        ["GroupName"] = ["Group name"],
        ["PlanId"] = ["Plan identifier", "Plan id"],
        ["PlanName"] = ["Plan name"],
        ["Relationship"] = ["Relationship to subscriber"],
    };

    public sealed class Projection
    {
        public Enrollment834 Enrollment { get; init; } = new();
        public List<string> FileErrors { get; } = [];

        /// <summary>Per-member problems that kept a member (or one of its coverage rows) out of the enrollment.</summary>
        public List<string> RowErrors { get; } = [];
        public List<string> Warnings { get; } = [];
        public int MemberRows { get; set; }
        public int CoverageRows { get; set; }
    }

    public static Projection Project(string membersContent, string? coveragesContent, string fileName)
    {
        var projection = new Projection
        {
            Enrollment =
            {
                FileName = fileName,
                ParsedAt = DateTime.UtcNow,
            },
        };

        var members = ReadTable(membersContent, MemberColumns, ["MemberId"], "member", projection.FileErrors);
        var coverages = coveragesContent is null
            ? []
            : ReadTable(coveragesContent, CoverageColumns, ["MemberId", "PlanId", "GroupId"], "coverage", projection.FileErrors);
        if (projection.FileErrors.Count > 0)
            return projection;

        projection.MemberRows = members.Count;
        projection.CoverageRows = coverages.Count;

        var coverageByMember = coverages
            .Where(c => Get(c, "MemberId") is not null)
            .GroupBy(c => Get(c, "MemberId")!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in members)
        {
            var memberId = Get(row, "MemberId");
            if (memberId is null)
            {
                projection.RowErrors.Add($"File line {row.Line}: \"Member id\" is empty.");
                continue;
            }
            if (!seen.Add(memberId))
            {
                projection.Warnings.Add($"Member {memberId} appears more than once in the member file; the first row is used.");
                continue;
            }

            var (first, middle, last) = Names(row);
            if (first is null || last is null)
            {
                projection.RowErrors.Add(
                    $"Member {memberId} (file line {row.Line}): no first and last name (give \"First name\" and \"Last name\", or \"Name\").");
                continue;
            }

            coverageByMember.TryGetValue(memberId, out var memberCoverage);
            memberCoverage ??= [];
            var firstCoverage = memberCoverage.FirstOrDefault();

            var subscriberId = Get(row, "SubscriberId") ?? (firstCoverage is null ? null : Get(firstCoverage, "SubscriberId")) ?? memberId;
            var relationship = Relationship(
                Get(row, "Relationship") ?? (firstCoverage is null ? null : Get(firstCoverage, "Relationship")),
                memberId == subscriberId);

            var groupId = firstCoverage is null ? null : Get(firstCoverage, "GroupId");
            var enrollment = new MemberEnrollment
            {
                ExplicitMemberId = memberId,
                SubscriberId = subscriberId,
                Relationship = relationship,
                MaintenanceType = "001",
                BenefitStatus = "A",
                GroupNumber = groupId,
                Demographics = new Demographics
                {
                    FirstName = first,
                    MiddleName = middle,
                    LastName = last,
                    DateOfBirth = Date(Get(row, "DateOfBirth")),
                    Gender = Gender(Get(row, "Gender")),
                    Address1 = Get(row, "Address"),
                    City = Get(row, "City"),
                    State = Get(row, "State"),
                    Zip = Get(row, "Zip"),
                },
            };

            if (groupId is not null)
            {
                enrollment.Sponsor = new Sponsor
                {
                    Qualifier = "P5",
                    Name = Get(firstCoverage!, "GroupName") ?? groupId,
                    Id = groupId,
                };
            }

            foreach (var coverage in memberCoverage)
            {
                var status = Get(coverage, "Status")?.ToLowerInvariant();
                if (status is "cancelled" or "canceled" or "entered-in-error" or "draft")
                {
                    projection.Warnings.Add($"Member {memberId}: coverage {Get(coverage, "CoverageId") ?? "(no id)"} has status '{status}' and was skipped.");
                    continue;
                }
                if (Get(coverage, "GroupId") is { } otherGroup && groupId is not null && otherGroup != groupId)
                {
                    projection.RowErrors.Add(
                        $"Member {memberId}: coverage rows name more than one group ({groupId}, {otherGroup}). " +
                        "Load one group per member, or split the member across files.");
                    continue;
                }

                var start = Date(Get(coverage, "StartDate"));
                if (start is null)
                {
                    projection.RowErrors.Add($"Member {memberId} (file line {coverage.Line}): coverage \"Start date\" is empty or not a date.");
                    continue;
                }

                enrollment.Coverage.Add(new CoverageDetail
                {
                    MaintenanceType = "001",
                    InsuranceLineCode = InsuranceLine(Get(coverage, "CoverageType")),
                    PlanCoverageDescription = Get(coverage, "PlanId"),
                    BenefitBeginDate = start,
                    BenefitEndDate = Date(Get(coverage, "EndDate")),
                });
            }

            enrollment.EnrollmentDate = enrollment.Coverage.Select(c => c.BenefitBeginDate).Where(d => d is not null).Min();
            projection.Enrollment.Enrollments.Add(enrollment);
        }

        foreach (var orphan in coverageByMember.Keys.Where(k => !seen.Contains(k)))
            projection.RowErrors.Add($"Coverage for member {orphan} has no row in the member file and was not loaded.");

        projection.Enrollment.TransactionCount = projection.Enrollment.Enrollments.Count;
        return projection;
    }

    // ── table reading ────────────────────────────────────────────────────────

    internal sealed record Row(int Line, Dictionary<string, string> Values);

    private static string? Get(Row row, string field)
        => row.Values.TryGetValue(field, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    private static List<Row> ReadTable(
        string content,
        IReadOnlyDictionary<string, string[]> columns,
        IReadOnlyList<string> required,
        string kind,
        List<string> fileErrors)
    {
        var delimiter = CpcdsDelimitedReader.DetectDelimiter(content);
        using var records = CpcdsDelimitedReader.Read(content, delimiter).GetEnumerator();
        if (!records.MoveNext())
        {
            fileErrors.Add($"The {kind} file is empty: it starts with a header row.");
            return [];
        }

        var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (field, names) in columns)
            foreach (var name in names)
                lookup.TryAdd(CpcdsHeaders.Normalize(name), field);

        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        var headers = records.Current.Fields;
        for (var i = 0; i < headers.Count; i++)
        {
            if (lookup.TryGetValue(CpcdsHeaders.Normalize(headers[i]), out var field))
                positions.TryAdd(field, i);
        }

        foreach (var field in required)
        {
            if (!positions.ContainsKey(field))
                fileErrors.Add($"The {kind} file has no \"{columns[field][0]}\" column.");
        }
        if (fileErrors.Count > 0) return [];

        var rows = new List<Row>();
        while (records.MoveNext())
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (field, index) in positions)
            {
                if (index < records.Current.Fields.Count)
                    values[field] = records.Current.Fields[index];
            }
            rows.Add(new Row(records.Current.LineNumber, values));
        }
        return rows;
    }

    // ── values ───────────────────────────────────────────────────────────────

    /// <summary>
    /// First, middle and last name: the separate columns when the file has
    /// them, otherwise "Name" read as "Last, First Middle" when it has a comma
    /// and "First Middle Last" when it has none.
    /// </summary>
    internal static (string? First, string? Middle, string? Last) Names(Row row)
    {
        var first = Get(row, "FirstName");
        var last = Get(row, "LastName");
        if (first is not null && last is not null)
            return (first, Get(row, "MiddleName"), last);

        var name = Get(row, "Name");
        if (name is null) return (null, null, null);

        if (name.Contains(','))
        {
            var parts = name.Split(',', 2, StringSplitOptions.TrimEntries);
            var given = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return (given.FirstOrDefault(), given.Length > 1 ? string.Join(' ', given.Skip(1)) : null, parts[0]);
        }

        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length switch
        {
            0 => (null, null, null),
            1 => (null, null, words[0]),
            2 => (words[0], null, words[1]),
            _ => (words[0], string.Join(' ', words[1..^1]), words[^1]),
        };
    }

    /// <summary>The 834 relationship code (INS02) for a CPCDS / FHIR subscriber relationship.</summary>
    internal static string Relationship(string? value, bool isSubscriber)
        => value?.Trim().ToLowerInvariant() switch
        {
            "self" or "18" => "18",
            "spouse" or "01" => "01",
            "child" or "19" => "19",
            "parent" or "other" or "injured" or "common" or "g8" => "G8",
            null or "" => isSubscriber ? "18" : "G8",
            _ => "G8",
        };

    /// <summary>The 834 gender code (M, F, U).</summary>
    internal static string? Gender(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "m" or "male" => "M",
            "f" or "female" => "F",
            null or "" => null,
            _ => "U",
        };

    /// <summary>The 834 insurance line (HD03) for a CPCDS coverage type. Medical unless it says dental or vision.</summary>
    internal static string InsuranceLine(string? coverageType)
    {
        var type = coverageType?.ToLowerInvariant() ?? string.Empty;
        if (type.Contains("dental")) return "DEN";
        if (type.Contains("vision")) return "VIS";
        return "HLT";
    }

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "yyyyMMdd", "MM/dd/yyyy", "M/d/yyyy", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ"];

    /// <summary>A CPCDS date as the 834's D8 (CCYYMMDD), which the import already parses; null when absent or unreadable.</summary>
    internal static string? Date(string? value)
        => value is not null
           && DateTime.TryParseExact(value.Trim(), DateFormats, CultureInfo.InvariantCulture,
               DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
            ? date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            : null;
}
