using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// One read of a record, from any service's access log (HIPAA access
/// accounting). <see cref="RecordType"/> says which kind of record was read:
/// on a member's log, reads of the member record and of their claims appear
/// together.
/// </summary>
public sealed class RecordAccessEntry
{
    public string Id { get; set; } = string.Empty;
    public string RecordType { get; set; } = string.Empty;
    public string? RecordId { get; set; }
    public string? MemberId { get; set; }
    public List<string> RecordIds { get; set; } = new();
    public int? ResultCount { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string? Route { get; set; }
    public string ActorId { get; set; } = string.Empty;
    public string? ActorName { get; set; }
    public List<string> ActorRoles { get; set; } = new();
    public bool ActorIsService { get; set; }
    public string? ClientIp { get; set; }
    public DateTime OccurredAt { get; set; }

    public string Actor => string.IsNullOrWhiteSpace(ActorName) ? ActorId : $"{ActorName} ({ActorId})";

    /// <summary>Where a single-record read leads, for the log's Record column.</summary>
    public string? Href => RecordId is null ? null : RecordType switch
    {
        "Member" => $"/members/{Uri.EscapeDataString(RecordId)}",
        "Claim" => $"/claims/{Uri.EscapeDataString(RecordId)}",
        "Provider" => $"/providers/{Uri.EscapeDataString(RecordId)}",
        "Authorization" => $"/authorizations/{Uri.EscapeDataString(RecordId)}",
        "Payment" => $"/payments/{Uri.EscapeDataString(RecordId)}",
        _ => null,
    };

    public string What => Action switch
    {
        "View" => "Viewed",
        "Search" or "List" => ResultCount is { } n ? $"{Action} returning it ({n} results)" : $"{Action} returning it",
        "AccessLog" => "Read the access log",
        "Pcp" => "Read PCP",
        "Fhir" => "Read FHIR record",
        "AuditTimeline" => "Read history",
        "AdjudicationDetail" => "Read adjudication detail",
        "Acknowledgment" => "Read 277CA",
        "Eob" => "Read EOB",
        "Notes" or "Edits" or "Eligibility" or "Events" or "Coverage" or "Enrollment" or "Accumulators"
            or "Identifiers" or "Alerts" or "Family" or "Documents" or "Adjustments" => $"Read {Action.ToLowerInvariant()}",
        _ => Action,
    };

    internal static RecordAccessEntry FromClaim(ClaimAccessEntry c) => new()
    {
        Id = c.Id, RecordType = "Claim", RecordId = c.ClaimId, RecordIds = c.ClaimIds, ResultCount = c.ResultCount,
        Action = c.Action, Outcome = c.Outcome, StatusCode = c.StatusCode, Route = c.Route,
        ActorId = c.ActorId, ActorName = c.ActorName, ActorRoles = c.ActorRoles, ActorIsService = c.ActorIsService,
        ClientIp = c.ClientIp, OccurredAt = c.OccurredAt,
    };
}

/// <summary>A record's access log, and the services whose part of it couldn't be read.</summary>
public sealed record RecordAccessLog(IReadOnlyList<RecordAccessEntry> Entries, IReadOnlyList<string> Unavailable);

public interface IRecordAccessLogService
{
    /// <summary>
    /// Who read this member: the member record (member-service) and their
    /// claims (claims-service), newest first. Needs <c>audit:read</c>.
    /// </summary>
    Task<RecordAccessLog> GetMemberAccessLogAsync(string memberId, int limit = 200);
}

public sealed class RecordAccessLogService : IRecordAccessLogService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RecordAccessLogService> _logger;

    public RecordAccessLogService(HttpClient http, IConfiguration configuration, ILogger<RecordAccessLogService> logger)
    {
        _http = http;
        _configuration = configuration;
        _logger = logger;
    }

    private string Base(string key, string fallback) => (_configuration[$"Services:{key}"] ?? fallback).TrimEnd('/');

    private static string E(string value) => Uri.EscapeDataString(value);

    public async Task<RecordAccessLog> GetMemberAccessLogAsync(string memberId, int limit = 200)
    {
        var member = Fetch<RecordAccessEntry>("Member Service",
            $"{Base("MemberService", "http://member-service.cloudhealthoffice/api/v1")}/members/{E(memberId)}/access-log?limit={limit}");
        var claims = Fetch<ClaimAccessEntry>("Claims Service",
            $"{Base("ClaimsService", "http://claims-service.cloudhealthoffice/api")}/claims/access-log?memberId={E(memberId)}&limit={limit}");
        await Task.WhenAll(member, claims);

        var entries = (member.Result.Entries ?? [])
            .Concat((claims.Result.Entries ?? []).Select(RecordAccessEntry.FromClaim))
            .OrderByDescending(e => e.OccurredAt)
            .Take(limit)
            .ToList();
        var unavailable = new[] { member.Result.Unavailable, claims.Result.Unavailable }.OfType<string>().ToList();
        return new RecordAccessLog(entries, unavailable);
    }

    /// <summary>One service's part of a log; an outage or refusal names the service instead of failing the whole log.</summary>
    private async Task<(List<T>? Entries, string? Unavailable)> Fetch<T>(string service, string url)
    {
        try
        {
            using var response = await _http.GetAsync(url);
            if (response.StatusCode == HttpStatusCode.NotFound) return (new(), null);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("{ServiceName} access log returned {StatusCode}", service, (int)response.StatusCode);
                return (null, service);
            }
            return (await response.Content.ReadFromJsonAsync<List<T>>(Json) ?? new(), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", service);
            return (null, service);
        }
    }
}
