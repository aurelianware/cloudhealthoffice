using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;

namespace ClaimsService.Extracts.Cpcds;

/// <summary>A member as the CPCDS export needs it, read from member-service.</summary>
public sealed record CpcdsExportMember(
    string MemberId,
    string? FirstName,
    string? LastName,
    string? MiddleName,
    DateTime? DateOfBirth,
    string? Gender,
    string? Address,
    string? City,
    string? State,
    string? ZipCode,
    string? SubscriberId,
    string? RelationshipCode,
    bool IsSubscriber,
    DateTime? DeceasedDate);

/// <summary>A coverage record as the CPCDS export needs it, read from coverage-service.</summary>
public sealed record CpcdsExportCoverage(
    string CoverageId,
    string MemberId,
    string? GroupNumber,
    string? GroupName,
    string? PlanId,
    string? PlanName,
    string? InsuranceLineCode,
    DateTime? EffectiveDate,
    DateTime? TerminationDate);

/// <summary>
/// The tenant's whole membership and coverage, a page at a time. The CPCDS
/// export writes them as a full snapshot on every run: member and coverage
/// files are small next to claims, and a consumer that replaces its copy
/// never has to reason about deletes.
/// </summary>
public interface ICpcdsEnrollmentSource
{
    IAsyncEnumerable<CpcdsExportMember> MembersAsync(string tenantId, CancellationToken ct);

    IAsyncEnumerable<CpcdsExportCoverage> CoveragesAsync(string tenantId, CancellationToken ct);
}

/// <summary>
/// Reads members (member-service <c>GET api/v1/members</c>) and coverage
/// (coverage-service <c>GET api/v1/coverage</c>) with claims-service's own
/// identity for the tenant, following each service's continuation token.
/// </summary>
public sealed class HttpCpcdsEnrollmentSource : ICpcdsEnrollmentSource
{
    public const string MemberClientName = "CpcdsExportMembers";
    public const string CoverageClientName = "CpcdsExportCoverage";
    public const int PageSize = 100;

    /// <summary>A guard against a service that keeps answering with a token: about 10 million records.</summary>
    internal const int MaxPages = 100_000;

    private readonly IHttpClientFactory _http;

    public HttpCpcdsEnrollmentSource(IHttpClientFactory http) => _http = http;

    public async IAsyncEnumerable<CpcdsExportMember> MembersAsync(
        string tenantId, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in PagesAsync(MemberClientName, "api/v1/members", "members", tenantId, ct))
        {
            if (Bool(item, "isDraft")) continue;
            var memberId = Text(item, "memberId");
            if (string.IsNullOrWhiteSpace(memberId)) continue;
            yield return new CpcdsExportMember(
                memberId,
                Text(item, "firstName"), Text(item, "lastName"), Text(item, "middleName"),
                DateValue(item, "dateOfBirth"), Text(item, "gender"),
                Text(item, "address"), Text(item, "city"), Text(item, "state"), Text(item, "zipCode"),
                Text(item, "subscriberMemberId"), Text(item, "relationshipCode"), Bool(item, "isSubscriber"),
                DateValue(item, "deceasedDate"));
        }
    }

    public async IAsyncEnumerable<CpcdsExportCoverage> CoveragesAsync(
        string tenantId, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in PagesAsync(CoverageClientName, "api/v1/coverage", "coverage", tenantId, ct))
        {
            var id = Text(item, "id");
            var memberId = Text(item, "memberId");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(memberId)) continue;
            yield return new CpcdsExportCoverage(
                id, memberId, Text(item, "groupNumber"), Text(item, "groupName"),
                Text(item, "planId"), Text(item, "planName"), Text(item, "insuranceLineCode"),
                DateValue(item, "effectiveDate"), DateValue(item, "terminationDate"));
        }
    }

    private async IAsyncEnumerable<JsonElement> PagesAsync(
        string client, string path, string listProperty, string tenantId, [EnumeratorCancellation] CancellationToken ct)
    {
        string? token = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var url = $"{path}?pageSize={PageSize}"
                      + (token is null ? string.Empty : "&continuationToken=" + Uri.EscapeDataString(token));
            using var request = new HttpRequestMessage(HttpMethod.Get, url).UseChoServiceIdentity();
            request.Headers.Add("X-Tenant-ID", tenantId);
            using var response = await _http.CreateClient(client).SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{path} answered {(int)response.StatusCode}.");

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            var root = doc.RootElement;
            if (Property(root, listProperty) is { ValueKind: JsonValueKind.Array } list)
            {
                foreach (var item in list.EnumerateArray())
                    yield return item.Clone();
            }

            token = Property(root, "continuationToken") is { ValueKind: JsonValueKind.String } next
                    && next.GetString() is { Length: > 0 } t ? t : null;
            if (token is null) yield break;
        }
        throw new InvalidOperationException($"{path} kept returning a continuation token after {MaxPages} pages.");
    }

    private static JsonElement? Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in element.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value;
        }
        return null;
    }

    private static string? Text(JsonElement e, string name)
        => Property(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name)
        => Property(e, name) is { ValueKind: JsonValueKind.True };

    private static DateTime? DateValue(JsonElement e, string name)
        => Text(e, name) is { } s && DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) && d.Year > 1
            ? d
            : null;
}

/// <summary>An <see cref="ICpcdsEnrollmentSource"/> over lists (tests, local runs).</summary>
public sealed class InMemoryCpcdsEnrollmentSource : ICpcdsEnrollmentSource
{
    public List<CpcdsExportMember> Members { get; } = [];
    public List<CpcdsExportCoverage> Coverages { get; } = [];

#pragma warning disable CS1998 // async iterators over in-memory lists
    public async IAsyncEnumerable<CpcdsExportMember> MembersAsync(string tenantId, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var m in Members) yield return m;
    }

    public async IAsyncEnumerable<CpcdsExportCoverage> CoveragesAsync(string tenantId, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var c in Coverages) yield return c;
    }
#pragma warning restore CS1998
}

/// <summary>
/// The CPCDS member and coverage files, with the headers CHO's CPCDS
/// enrollment import reads, so the export loads back without a column map.
/// </summary>
public static class CpcdsEnrollmentExportRows
{
    public const string MembersFile = "CPCDS_Members.csv";
    public const string CoveragesFile = "CPCDS_Coverages.csv";

    public static readonly IReadOnlyList<string> MemberHeaders =
    [
        "Member id", "First name", "Last name", "Middle name", "Date of birth", "Date of death", "Gender code",
        "Home_Address line", "Home_City", "Home_State", "Home_Zip code", "Subscriber id", "Relationship to subscriber",
    ];

    public static readonly IReadOnlyList<string> CoverageHeaders =
    [
        "Coverage id", "Member id", "Subscriber id", "Coverage type", "Coverage status", "Start date", "End date",
        "Group id", "Group name", "Plan identifier", "Plan name", "Relationship to subscriber",
    ];

    public static IReadOnlyList<string> Member(CpcdsExportMember m) =>
    [
        m.MemberId, m.FirstName ?? "", m.LastName ?? "", m.MiddleName ?? "", Date(m.DateOfBirth), Date(m.DeceasedDate),
        Gender(m.Gender), m.Address ?? "", m.City ?? "", m.State ?? "", m.ZipCode ?? "",
        SubscriberId(m), Relationship(m.RelationshipCode, m.IsSubscriber),
    ];

    /// <param name="member">The coverage's member, when the export has it: its subscriber and relationship.</param>
    public static IReadOnlyList<string> Coverage(CpcdsExportCoverage c, CpcdsExportMember? member) =>
    [
        c.CoverageId, c.MemberId, member is null ? "" : SubscriberId(member), CoverageType(c.InsuranceLineCode),
        "active", Date(c.EffectiveDate), Date(c.TerminationDate),
        c.GroupNumber ?? "", c.GroupName ?? "", c.PlanId ?? "", c.PlanName ?? "",
        member is null ? "" : Relationship(member.RelationshipCode, member.IsSubscriber),
    ];

    private static string SubscriberId(CpcdsExportMember m)
        => !string.IsNullOrWhiteSpace(m.SubscriberId) ? m.SubscriberId! : m.IsSubscriber ? m.MemberId : "";

    /// <summary>The FHIR subscriber relationship for an 834 INS02 code.</summary>
    internal static string Relationship(string? code, bool isSubscriber) => code?.Trim() switch
    {
        "18" => "self",
        "01" => "spouse",
        "19" => "child",
        null or "" => isSubscriber ? "self" : "other",
        _ => "other",
    };

    internal static string Gender(string? gender) => gender?.Trim().ToUpperInvariant() switch
    {
        "M" or "MALE" => "male",
        "F" or "FEMALE" => "female",
        null or "" => "",
        _ => "unknown",
    };

    /// <summary>The CPCDS coverage type for an 834 insurance line (HD03).</summary>
    internal static string CoverageType(string? insuranceLine) => insuranceLine?.Trim().ToUpperInvariant() switch
    {
        "DEN" => "dental",
        "VIS" => "vision",
        "PDG" => "pharmacy",
        _ => "medical",
    };

    private static string Date(DateTime? value)
        => value is { } v && v != default ? v.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
}
