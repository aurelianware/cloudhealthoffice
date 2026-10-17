using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace CloudHealthOffice.Portal.Services;

[BsonIgnoreExtraElements]
public class TenantSubscription
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string AzureTenantId { get; set; } = string.Empty;
    public string OrganizationName { get; set; } = string.Empty;
    public string SubscriptionStatus { get; set; } = string.Empty; // Active, Trial, Expired, Cancelled
    public string Tier { get; set; } = string.Empty; // starter, professional, enterprise
    public bool IsDemo { get; set; }
    public string? StripeCustomerId { get; set; }
    public string? StripeSubscriptionId { get; set; }
    public DateTime? TrialEndsAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<string> AdminEmails { get; set; } = new();
    public string? Notes { get; set; }
}
