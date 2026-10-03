namespace SmartAuthService.Models;

/// <summary>
/// EHR launch context registered by an authenticated CHO caller before the
/// provider is redirected to the SMART application. Stored temporarily until
/// the authorization code is issued (TTL = SmartAuth:LaunchContextTtlMinutes).
///
/// <see cref="TenantId"/> and <see cref="RegisteredBy"/> come from the
/// registering caller's validated CHO token, never from the request.
/// </summary>
public class LaunchContext
{
    public string LaunchToken { get; init; } = string.Empty;
    public string TenantId { get; init; } = string.Empty;
    public string RegisteredBy { get; init; } = string.Empty;
    public string? PatientId { get; init; }
    public string? EncounterId { get; init; }
    public string ClientId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// POST /launch request body. There is deliberately no tenant and no
/// practitioner here: the tenant is the caller's token's, and the
/// practitioner (fhirUser) is the signed-in provider user's mapping.
/// </summary>
public class RegisterLaunchRequest
{
    /// <summary>FHIR Patient resource ID (without "Patient/" prefix), e.g. "pat-001".</summary>
    public string? PatientId { get; init; }

    /// <summary>FHIR Encounter resource ID, e.g. "enc-001".</summary>
    public string? EncounterId { get; init; }

    /// <summary>OAuth2 client_id of the SMART application being launched (must belong to the caller's tenant).</summary>
    public required string ClientId { get; init; }
}

/// <summary>POST /launch response.</summary>
public record RegisterLaunchResponse(string Launch);
