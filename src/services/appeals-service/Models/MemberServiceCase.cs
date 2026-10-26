using CloudHealthOffice.Infrastructure.Contacts;
using AppealsService.Services.RulePacks;
using MongoDB.Bson.Serialization.Attributes;

namespace AppealsService.Models;

/// <summary>Member-self intake without fabricated claim/provider identifiers. Separate from legacy claim appeals.</summary>
[BsonIgnoreExtraElements]
public sealed class MemberServiceCase
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string ContactId { get; set; } = string.Empty;
    public string? AuthorizationId { get; set; }
    public string MemberId { get; set; } = string.Empty;
    public string Classification { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public string EncryptedSummary { get; set; } = string.Empty;
    public string Status { get; set; } = "Received";
    public long Revision { get; set; }
    public string? EncryptedResolution { get; set; }
    public DateTime? DecisionRecordedAt { get; set; }
    public string? DecisionRecordedBy { get; set; }
    // Persist JSON to keep the pinned evaluation independent of BSON immutable-record conventions.
    public string PinnedRuleEvaluationJson { get; set; } = string.Empty;
    public ContactCaseResult Receipt { get; set; } = new();
}
