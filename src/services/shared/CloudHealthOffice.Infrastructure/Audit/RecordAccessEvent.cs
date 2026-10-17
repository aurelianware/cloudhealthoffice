using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace CloudHealthOffice.Infrastructure.Audit;

/// <summary>
/// One read of a record (a member, a provider, an authorization, a payment):
/// who read it, when, through which endpoint, and whether it was allowed.
/// Append-only. With the claim access log it answers "who looked at this
/// member's information" for HIPAA accounting, privacy investigations and
/// audits (45 CFR 164.308(a)(1)(ii)(D) information system activity review;
/// 164.312(b) audit controls). Every query is scoped to the tenant.
/// </summary>
[BsonIgnoreExtraElements]
public class RecordAccessEvent
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string TenantId { get; set; } = string.Empty;

    /// <summary>Member, Provider, Authorization, Payment.</summary>
    public string RecordType { get; set; } = string.Empty;

    /// <summary>The record read, for a single-record read.</summary>
    public string? RecordId { get; set; }

    /// <summary>The member the read concerns, when known (for a member read, the member).</summary>
    public string? MemberId { get; set; }

    /// <summary>For a search or list: the records returned (capped at <see cref="MaxRecordIds"/>).</summary>
    public List<string> RecordIds { get; set; } = new();

    /// <summary>For a search or list: how many records were returned.</summary>
    public int? ResultCount { get; set; }

    /// <summary>View, Search, Notes, Documents, AccessLog, ...</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Allowed, Denied, NotFound or Error.</summary>
    public string Outcome { get; set; } = RecordAccessOutcomes.Allowed;

    public int StatusCode { get; set; }
    public string HttpMethod { get; set; } = "GET";

    /// <summary>The route template (no query string, so no search terms are stored).</summary>
    public string? Route { get; set; }

    public string ActorId { get; set; } = string.Empty;
    public string? ActorName { get; set; }
    public List<string> ActorRoles { get; set; } = new();
    public bool ActorIsService { get; set; }
    public string? CorrelationId { get; set; }
    public string? ClientIp { get; set; }
    public DateTime OccurredAt { get; set; }

    public const int MaxRecordIds = 200;
}

public static class RecordAccessOutcomes
{
    public const string Allowed = "Allowed";
    public const string Denied = "Denied";
    public const string NotFound = "NotFound";
    public const string Error = "Error";

    public static string For(int statusCode) => statusCode switch
    {
        401 or 403 => Denied,
        404 => NotFound,
        >= 500 => Error,
        _ => Allowed,
    };
}

public static class RecordAccessActions
{
    public const string View = "View";
    public const string Search = "Search";
    public const string List = "List";
    public const string Notes = "Notes";
    public const string Documents = "Documents";
    public const string AccessLog = "AccessLog";
}

/// <summary>A query of the access log; always within one tenant.</summary>
public sealed record RecordAccessQuery(
    string TenantId,
    string? RecordType = null,
    string? RecordId = null,
    string? MemberId = null,
    string? ActorId = null,
    DateTime? From = null,
    DateTime? To = null,
    int Limit = 200);
