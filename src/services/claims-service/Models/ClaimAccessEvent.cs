using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace ClaimsService.Models;

/// <summary>
/// One read of claim data: who looked at which claim (or which claims, for a
/// search), when, through which endpoint, and whether it was allowed.
/// Append-only — nothing updates or deletes an access event — so it answers
/// "who looked at this member's claims" for HIPAA accounting, privacy
/// investigations and audits (minimum-necessary, 45 CFR 164.308(a)(1)(ii)(D)
/// information system activity review).
///
/// Cosmos partition key: TenantId.
/// </summary>
[BsonIgnoreExtraElements]
public class ClaimAccessEvent
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string TenantId { get; set; } = string.Empty;

    /// <summary>The claim read, for a single-claim read.</summary>
    public string? ClaimId { get; set; }

    public string? ClaimNumber { get; set; }

    /// <summary>The member whose claim(s) were read, when known.</summary>
    public string? MemberId { get; set; }

    /// <summary>For a search or list: the claims returned (capped at <see cref="MaxClaimIds"/>).</summary>
    public List<string> ClaimIds { get; set; } = new();

    /// <summary>For a search or list: how many claims were returned in total.</summary>
    public int? ResultCount { get; set; }

    /// <summary>What was read: View, Search, Notes, Edits, AdjudicationDetail, AuditTimeline, Eob, AccessLog, ...</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Allowed, Denied or NotFound.</summary>
    public string Outcome { get; set; } = ClaimAccessOutcomes.Allowed;

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

    public const int MaxClaimIds = 200;
}

public static class ClaimAccessOutcomes
{
    public const string Allowed = "Allowed";
    public const string Denied = "Denied";
    public const string NotFound = "NotFound";
    public const string Error = "Error";
}

/// <summary>A query of the access log.</summary>
public sealed record ClaimAccessQuery(
    string TenantId,
    string? ClaimId = null,
    string? MemberId = null,
    string? ActorId = null,
    DateTime? From = null,
    DateTime? To = null,
    int Limit = 200);

/// <summary>The kinds of claim read recorded in the access log.</summary>
public static class ClaimAccessActions
{
    public const string View = "View";
    public const string Search = "Search";
    /// <summary>A standard report run (claims:reports): the claims it returned.</summary>
    public const string Report = "Report";
    public const string List = "List";
    public const string Notes = "Notes";
    public const string Edits = "Edits";
    public const string AdjudicationDetail = "AdjudicationDetail";
    public const string AuditTimeline = "AuditTimeline";
    public const string DuplicateMatches = "DuplicateMatches";
    public const string AiExamination = "AiExamination";
    public const string Acknowledgment = "Acknowledgment";
    public const string Eob = "Eob";
    public const string Adjustments = "Adjustments";
    public const string WorkQueue = "WorkQueue";
    public const string AccessLog = "AccessLog";
}
