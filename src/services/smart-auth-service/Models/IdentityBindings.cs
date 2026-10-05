using MongoDB.Bson.Serialization.Attributes;

namespace SmartAuthService.Models;

/// <summary>
/// Who authenticated a SMART user: the issuer of whatever signed them in and
/// the subject it asserted. The pair, never the subject alone, is the identity:
/// two identity providers may both have a user called "alice".
/// </summary>
public readonly record struct SmartIdentity(string Issuer, string Subject)
{
    public override string ToString() => $"{Issuer}|{Subject}";
}

public static class BindingStatus
{
    public const string Active = "Active";
    public const string Revoked = "Revoked";
}

/// <summary>
/// A member's SMART identity bound to exactly one (tenant, member id).
/// Created only by the member redeeming an enrolment code a tenant user with
/// <c>members:write</c> issued; never from anything the SMART client sends.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class MemberLink
{
    [BsonId] public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;

    /// <summary>The member's FHIR Patient id in that tenant; becomes the token's <c>patient</c>.</summary>
    public string MemberId { get; set; } = string.Empty;

    public string Status { get; set; } = BindingStatus.Active;
    public string EnrolmentId { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string? RevokedBy { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>A provider user's SMART identity bound to one (tenant, provider id, NPI).</summary>
[BsonIgnoreExtraElements]
public sealed class ProviderUserLink
{
    [BsonId] public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// The provider id fhir-service attributes members to (its Provider Access
    /// panels key on the token's <c>sub</c>), and the Practitioner id in <c>fhirUser</c>.
    /// </summary>
    public string ProviderId { get; set; } = string.Empty;

    public string Npi { get; set; } = string.Empty;
    public string Status { get; set; } = BindingStatus.Active;
    public string EnrolmentId { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string? RevokedBy { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public static class EnrolmentKind
{
    public const string Member = "Member";
    public const string Provider = "Provider";
}

public static class EnrolmentStatus
{
    public const string Pending = "Pending";
    public const string Redeemed = "Redeemed";
    public const string Cancelled = "Cancelled";
}

/// <summary>
/// A single-use enrolment code issued by an authenticated tenant user for one
/// member or provider of their tenant. The code itself is never stored, only
/// its SHA-256; it is shown once to the issuer, who delivers it to the person
/// over a channel the tenant has verified (mailed letter, member portal,
/// provider onboarding packet).
/// </summary>
[BsonIgnoreExtraElements]
public sealed class Enrolment
{
    [BsonId] public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = EnrolmentKind.Member;
    public string TenantId { get; set; } = string.Empty;
    public string? MemberId { get; set; }
    public string? ProviderId { get; set; }
    public string? Npi { get; set; }
    public string CodeHash { get; set; } = string.Empty;
    public string Status { get; set; } = EnrolmentStatus.Pending;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string? RedeemedBy { get; set; }
    public DateTimeOffset? RedeemedAt { get; set; }
    public string? CancelledBy { get; set; }
}

public static class SmartClientKind
{
    /// <summary>System/backend services and payer-to-payer: client credentials, system/ scopes.</summary>
    public const string Backend = "backend";

    /// <summary>Member-facing apps (Patient Access): authorization code + PKCE, patient/ scopes.</summary>
    public const string PatientApp = "patient-app";

    /// <summary>Provider-facing apps (Provider Access, EHR launch): authorization code, user/ scopes.</summary>
    public const string ProviderApp = "provider-app";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Backend, PatientApp, ProviderApp,
    };
}

/// <summary>
/// The tenant a SMART client registration belongs to. One registration, one
/// tenant: the tenant of the administrator who registered it. A vendor app
/// serving two payers is registered once by each, under two client ids.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class ClientTenantRegistration
{
    /// <summary>The OAuth client_id.</summary>
    [BsonId] public string ClientId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Kind { get; set; } = SmartClientKind.Backend;
    public string DisplayName { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
