using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace ContactService.Models;

/// <summary>
/// One reason a person contacts the plan, in the tenant's own words.
///
/// Configuration, not an enum. Every plan counts its calls differently, and
/// the reason list is the spine of service reporting — call volume by reason
/// is the first number an operations manager looks at — so a plan has to be
/// able to add, retire and reorder reasons without a release. Retiring a
/// reason hides it from the rep but leaves it on the contacts that already
/// carry it, so last quarter's report still adds up.
///
/// See docs/architecture/cho-service.md §3.
/// </summary>
[BsonIgnoreExtraElements]
public class ContactReason
{
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Stable code, unique within the tenant, stored on the contact. Upper
    /// case by convention; compared case-insensitively.
    /// </summary>
    [Required]
    [StringLength(50)]
    public string Code { get; set; } = string.Empty;

    /// <summary>What the rep sees in the list.</summary>
    [Required]
    [StringLength(200)]
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Groups reasons on the screen and in reporting ("Claims", "Benefits",
    /// "Enrollment"). Free text, so a plan's own grouping survives.
    /// </summary>
    [StringLength(100)]
    public string? Category { get; set; }

    /// <summary>Order within the category; lower comes first.</summary>
    public int Order { get; set; }

    /// <summary>
    /// Retired reasons stay readable but are not offered to reps. A reason
    /// is never deleted, because contacts refer to it.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Marks the reasons that mean the caller is unhappy. Step 5 of
    /// cho-service.md §8 uses it alongside the AI complaint-language flag to
    /// find contacts closed as an inquiry that should have been grievances.
    /// It changes nothing on its own.
    /// </summary>
    public bool IsComplaintIndicator { get; set; }

    /// <summary>Set on the seeded starter reasons, so a re-seed recognises them.</summary>
    [StringLength(50)]
    public string? SystemKey { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    [StringLength(200)]
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// The starter taxonomy a tenant gets before anyone configures one, from the
/// examples in cho-service.md §3. A plan is expected to replace it; it exists
/// so the contact screen is usable on the first day.
/// </summary>
public static class StarterContactReasons
{
    public sealed record Seed(string Code, string Label, string Category, bool IsComplaintIndicator = false);

    public static readonly IReadOnlyList<Seed> All =
    [
        new("ELIGIBILITY",   "Eligibility",             "Coverage"),
        new("BENEFITS",      "Benefits",                "Coverage"),
        new("CLAIM_STATUS",  "Claim status",            "Claims"),
        new("CLAIM_DENIAL",  "Claim denial",            "Claims"),
        new("AUTH_STATUS",   "Authorization status",    "Authorizations"),
        new("ID_CARD",       "ID card",                 "Enrollment"),
        new("PCP_CHANGE",    "PCP change",              "Enrollment"),
        new("ADDRESS_CHANGE","Address change",          "Enrollment"),
        new("BILLING",       "Billing and premiums",    "Finance"),
        new("PROVIDER_PAY",  "Provider payment",        "Finance"),
        new("FIND_PROVIDER", "Find a provider",         "Coverage"),
        new("COMPLAINT",     "Complaint",               "Service",  IsComplaintIndicator: true),
        new("ACCESS_TO_CARE","Access to care",          "Service",  IsComplaintIndicator: true),
        new("OTHER",         "Other",                   "Service"),
    ];
}
