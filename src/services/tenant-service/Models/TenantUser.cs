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
    public List<string>? Roles { get; set; }
    public string? Department { get; set; }
    public string? SupervisorId { get; set; }
    public string? Status { get; set; }
}
