using System;
using System.ComponentModel.DataAnnotations;
using MongoDB.Bson.Serialization.Attributes;

namespace MemberService.Models;

/// <summary>
/// A free-text note attached to a member. Notes are append-only — once created
/// they cannot be edited or deleted. Corrections are written as new notes that
/// link back to the original via <see cref="LinkedResourceType"/> = <c>"MemberNote"</c>
/// and <see cref="LinkedResourceId"/> = original note id.
///
/// Projects to FHIR R4 Communication.
/// </summary>
[BsonIgnoreExtraElements]
public class MemberNote
{
    [Required]
    public string TenantId { get; set; } = string.Empty;

    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [StringLength(50)]
    public string MemberId { get; set; } = string.Empty;

    [Required]
    public MemberNoteCategory Category { get; set; }

    [Required]
    [StringLength(200)]
    public string Subject { get; set; } = string.Empty;

    [Required]
    [StringLength(8000)]
    public string Body { get; set; } = string.Empty;

    [Required]
    [StringLength(200)]
    public string Author { get; set; } = string.Empty;

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// FHIR-style link to a related resource. For corrections, set this to
    /// <c>"MemberNote"</c> and the prior note id. Other valid types include
    /// <c>"Claim"</c>, <c>"Authorization"</c>, <c>"Appeal"</c>, <c>"Communication"</c>.
    /// </summary>
    [StringLength(50)]
    public string? LinkedResourceType { get; set; }

    [StringLength(100)]
    public string? LinkedResourceId { get; set; }
}

/// <summary>
/// Logical inbox the note belongs to. Drives portal filtering and downstream
/// routing (e.g. Appeals notes feed the appeals queue).
/// </summary>
public enum MemberNoteCategory
{
    CustomerService = 1,
    CareManagement = 2,
    Appeals = 3,
    Billing = 4,
    Clinical = 5
}
