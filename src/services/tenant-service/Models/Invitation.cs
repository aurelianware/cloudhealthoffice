using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace TenantService.Models;

/// <summary>Stored invitation states. "Expired" is never stored: it is a Pending invitation past its expiry.</summary>
public static class InvitationStatus
{
    public const string Pending = "Pending";
    public const string Redeemed = "Redeemed";
    public const string Revoked = "Revoked";
    public const string Expired = "Expired";
}

/// <summary>
/// An invitation for a person, usually from another Entra directory, to join a
/// tenant. Creating one also creates (or reuses) the TenantUser it will link,
/// in status <see cref="TenantUserStatus.Invited"/>. The redemption code is
/// returned once and never stored: only its SHA-256 hash is.
/// </summary>
public class Invitation
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The TenantUser that redemption links and activates.</summary>
    public string UserId { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    /// <summary>Trimmed and lower-cased; redemption compares the signed-in username with this.</summary>
    public string EmailNormalized { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = new();

    /// <summary>Base64url SHA-256 of the current code. Never serialized to API callers.</summary>
    public string CodeHash { get; set; } = string.Empty;

    public string Status { get; set; } = InvitationStatus.Pending;

    /// <summary>Token subject of the administrator who created it.</summary>
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }

    public DateTime? ResentAt { get; set; }
    public string? ResentBy { get; set; }
    public int SendCount { get; set; } = 1;

    public DateTime? RedeemedAt { get; set; }
    public InvitationRedeemer? RedeemedBy { get; set; }

    public string? RevokedBy { get; set; }
    public DateTime? RevokedAt { get; set; }

    /// <summary>Pending past its expiry reads as Expired.</summary>
    public string EffectiveStatus(DateTime now)
        => Status == InvitationStatus.Pending && ExpiresAt <= now ? InvitationStatus.Expired : Status;
}

/// <summary>The Entra identity that redeemed an invitation.</summary>
public class InvitationRedeemer
{
    public string Tid { get; set; } = string.Empty;
    public string Oid { get; set; } = string.Empty;
}

/// <summary>What API callers see of an invitation: never the code or its hash.</summary>
public sealed class InvitationView
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("tenantId")] public string TenantId { get; init; } = string.Empty;
    [JsonPropertyName("userId")] public string UserId { get; init; } = string.Empty;
    [JsonPropertyName("email")] public string Email { get; init; } = string.Empty;
    [JsonPropertyName("displayName")] public string DisplayName { get; init; } = string.Empty;
    [JsonPropertyName("firstName")] public string FirstName { get; init; } = string.Empty;
    [JsonPropertyName("lastName")] public string LastName { get; init; } = string.Empty;
    [JsonPropertyName("department")] public string Department { get; init; } = string.Empty;
    [JsonPropertyName("roles")] public List<string> Roles { get; init; } = new();
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
    [JsonPropertyName("createdBy")] public string CreatedBy { get; init; } = string.Empty;
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; init; }
    [JsonPropertyName("expiresAt")] public DateTime ExpiresAt { get; init; }
    [JsonPropertyName("resentAt")] public DateTime? ResentAt { get; init; }
    [JsonPropertyName("sendCount")] public int SendCount { get; init; }
    [JsonPropertyName("redeemedAt")] public DateTime? RedeemedAt { get; init; }
    [JsonPropertyName("redeemedBy")] public InvitationRedeemer? RedeemedBy { get; init; }
    [JsonPropertyName("revokedBy")] public string? RevokedBy { get; init; }
    [JsonPropertyName("revokedAt")] public DateTime? RevokedAt { get; init; }

    public static InvitationView From(Invitation i, DateTime now) => new()
    {
        Id = i.Id,
        TenantId = i.TenantId,
        UserId = i.UserId,
        Email = i.Email,
        DisplayName = i.DisplayName,
        FirstName = i.FirstName,
        LastName = i.LastName,
        Department = i.Department,
        Roles = i.Roles ?? new List<string>(),
        Status = i.EffectiveStatus(now),
        CreatedBy = i.CreatedBy,
        CreatedAt = i.CreatedAt,
        ExpiresAt = i.ExpiresAt,
        ResentAt = i.ResentAt,
        SendCount = i.SendCount,
        RedeemedAt = i.RedeemedAt,
        RedeemedBy = i.RedeemedBy,
        RevokedBy = i.RevokedBy,
        RevokedAt = i.RevokedAt,
    };
}

/// <summary>
/// The answer to create and resend: the invitation plus the code and link,
/// which are shown once and cannot be retrieved again.
/// </summary>
public sealed class IssuedInvitationResponse
{
    [JsonPropertyName("invitation")] public InvitationView Invitation { get; init; } = new();
    [JsonPropertyName("code")] public string Code { get; init; } = string.Empty;
    [JsonPropertyName("redemptionUrl")] public string RedemptionUrl { get; init; } = string.Empty;
}

public class CreateInvitationRequest
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;

    [Required]
    public List<string> Roles { get; set; } = new();
}

/// <summary>Body of token-service's internal redemption call.</summary>
public sealed class RedeemInvitationRequest
{
    public string Code { get; set; } = string.Empty;
    public string Tid { get; set; } = string.Empty;
    public string Oid { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}
