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
    public string Status { get; set; } = "Active"; // Active, Disabled, Locked

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

/// <summary>
/// DTO for creating a new tenant user
/// </summary>
public class CreateTenantUserRequest
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    public string AzureAdObjectId { get; set; } = string.Empty;

    /// <summary>Entra directory (tid) of <see cref="AzureAdObjectId"/>. Set both together.</summary>
    public string AzureAdTenantId { get; set; } = string.Empty;

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
    public string? AzureAdObjectId { get; set; }

    /// <summary>
    /// Entra directory (tid) of the object id. Changing <see cref="AzureAdObjectId"/>
    /// without also sending this clears the recorded directory.
    /// </summary>
    public string? AzureAdTenantId { get; set; }
    public List<string>? Roles { get; set; }
    public string? Department { get; set; }
    public string? SupervisorId { get; set; }
    public string? Status { get; set; }
}
