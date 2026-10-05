using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace TenantService.Models;

/// <summary>
/// Represents a user within a tenant organization.
/// Maps to Azure AD identities for SSO authentication.
/// </summary>
public class TenantUser
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("tenantId")]
    [Required]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty; // Azure AD UPN

    [JsonPropertyName("emailNormalized")]
    public string EmailNormalized { get; set; } = string.Empty; // Lowercase for case-insensitive lookups

    [JsonPropertyName("azureAdObjectId")]
    public string AzureAdObjectId { get; set; } = string.Empty; // Azure AD OID from JWT

    /// <summary>
    /// The Entra directory (<c>tid</c>) that <see cref="AzureAdObjectId"/> belongs to.
    /// An object id only means something inside its own directory, so token-service
    /// requires both to match once this is recorded. It is empty on links made
    /// before it existed. token-service honours those only from the tenant's own
    /// registered directory, and records the tid when it does.
    /// </summary>
    [JsonPropertyName("azureAdTenantId")]
    public string AzureAdTenantId { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    [Required]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("firstName")]
    public string FirstName { get; set; } = string.Empty;

    [JsonPropertyName("lastName")]
    public string LastName { get; set; } = string.Empty;

    [JsonPropertyName("roles")]
    public List<string> Roles { get; set; } = new(); // Role names

    [JsonPropertyName("department")]
    public string Department { get; set; } = string.Empty; // Claims, UM, Enrollment, etc.

    [JsonPropertyName("supervisorId")]
    public string? SupervisorId { get; set; } // For work queue escalation

    [JsonPropertyName("status")]
    public string Status { get; set; } = TenantUserStatus.Active; // Active, Disabled, Locked, Invited

    /// <summary>
    /// The invitation this user was created or re-invited by. Kept after
    /// redemption, for traceability. Never serialized.
    /// </summary>
    [JsonIgnore]
    public string? InvitationId { get; set; }

    /// <summary>
    /// SHA-256 of the invitation code that may still activate this user. Present
    /// only while the user is <see cref="TenantUserStatus.Invited"/> with a live
    /// invitation. Redemption is one conditional update on this document that
    /// requires this field, so revoking or resending (which clear or replace it
    /// here first) either wins or loses against a redemption, atomically.
    /// Never serialized.
    /// </summary>
    [JsonIgnore]
    public string? InvitationCodeHash { get; set; }

    /// <summary>When the live invitation code stops working. Never serialized.</summary>
    [JsonIgnore]
    public DateTime? InvitationExpiresAt { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("lastLoginAt")]
    public DateTime? LastLoginAt { get; set; }

    /// <summary>Token subject of the caller that created the user. Never read from a request body.</summary>
    [JsonPropertyName("createdBy")]
    public string? CreatedBy { get; set; }

    /// <summary>Token subject of the caller that last changed the user. Never read from a request body.</summary>
    [JsonPropertyName("updatedBy")]
    public string? UpdatedBy { get; set; }
}

/// <summary>TenantUser.Status values.</summary>
public static class TenantUserStatus
{
    public const string Active = "Active";
    public const string Disabled = "Disabled";
    public const string Locked = "Locked";

    /// <summary>
    /// Created by an invitation and not yet redeemed. Not linked to any Entra
    /// identity. token-service issues no token for it and never links it by email.
    /// </summary>
    public const string Invited = "Invited";

    public static bool Is(string? status, string expected)
        => string.Equals(status?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// DTO for creating a new tenant user
/// </summary>
public class CreateTenantUserRequest
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    // No azureAdObjectId / azureAdTenantId: an administrator cannot assert an
    // Entra identity, and nothing could verify one. Only token-service links
    // identities (first sign-in from the tenant's own directory, or invitation
    // redemption). Unknown body properties are ignored.

    [Required]
    public string DisplayName { get; set; } = string.Empty;

    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;

    [Required]
    public List<string> Roles { get; set; } = new();

    public string Department { get; set; } = string.Empty;
    public string? SupervisorId { get; set; }
}

/// <summary>
/// DTO for updating an existing tenant user
/// </summary>
public class UpdateTenantUserRequest
{
    public string? DisplayName { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Email { get; set; }

    // No azureAdObjectId / azureAdTenantId (see CreateTenantUserRequest). An
    // administrator removes a link with POST .../users/{id}/unlink.
    public List<string>? Roles { get; set; }
    public string? Department { get; set; }
    public string? SupervisorId { get; set; }
    public string? Status { get; set; }
}
