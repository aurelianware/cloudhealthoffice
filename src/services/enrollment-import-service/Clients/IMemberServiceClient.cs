namespace EnrollmentImportService.Clients;

/// <summary>
/// Client for member-service's own Member API. enrollment-import-service used
/// to write Member documents directly into a Mongo "Members" collection —
/// leftover from before member-service was split out as its own service (it
/// originally owned members/coverage/sponsors together). That collection name
/// collides with member-service's actual "Members" collection, and the two
/// Member shapes are incompatible (e.g. Status is a string here, a C# enum
/// there) — member-service would throw deserializing a document this service
/// wrote. Delegating to member-service's real API instead of writing to Mongo
/// directly fixes that at the source.
/// </summary>
public interface IMemberServiceClient
{
    /// <summary>True if a member with this id already exists for the tenant.</summary>
    Task<bool> ExistsAsync(string tenantId, string memberId, CancellationToken ct = default);

    Task CreateAsync(string tenantId, CreateMemberRequestDto request, CancellationToken ct = default);

    Task UpdateAsync(string tenantId, string memberId, UpdateMemberRequestDto request, CancellationToken ct = default);

    Task TerminateAsync(string tenantId, string memberId, TerminateMemberRequestDto request, CancellationToken ct = default);
}

/// <summary>Mirrors member-service's CreateMemberRequest (MembersController.cs).</summary>
public class CreateMemberRequestDto
{
    public string MemberId { get; set; } = string.Empty;
    public string? SSN { get; set; }
    public string GroupNumber { get; set; } = string.Empty;
    public bool IsSubscriber { get; set; }
    public string? SubscriberMemberId { get; set; }
    public string? RelationshipCode { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? MiddleName { get; set; }
    public DateTime DateOfBirth { get; set; }
    public string? Gender { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? ZipCode { get; set; }
}

/// <summary>Mirrors member-service's UpdateMemberRequest (MembersController.cs).</summary>
public class UpdateMemberRequestDto
{
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? ZipCode { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }

    /// <summary>member-service's EnrollmentStatus enum, sent as its string name — matches
    /// System.Text.Json's default enum-as-string behavior on both sides of the wire.</summary>
    public string? Status { get; set; }

    public string? EventId { get; set; }
}

/// <summary>Mirrors member-service's TerminateMemberRequest (MembersController.cs).</summary>
public class TerminateMemberRequestDto
{
    public string MemberId { get; set; } = string.Empty;
    public string CoverageId { get; set; } = string.Empty;
    public DateTime TerminationDate { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public string? EventId { get; set; }
}
