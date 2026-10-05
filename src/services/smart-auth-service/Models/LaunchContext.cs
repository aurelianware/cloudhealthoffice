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

    /// <summary>
    /// The provider id (the provider binding's <c>ProviderId</c>) the launch is
    /// for, when the registering caller named one. Such a launch is honoured
    /// only for a provider user bound to exactly that provider.
    /// </summary>
    public string? PractitionerId { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// POST /launch request body. There is deliberately no tenant here: the
/// tenant is the caller's token's. The practitioner in the issued token
/// (fhirUser) is always the signed-in provider user's mapping;
/// <see cref="PractitionerId"/> only restricts WHO may use the launch.
/// </summary>
public class RegisterLaunchRequest
{
    /// <summary>FHIR Patient resource ID (without "Patient/" prefix), e.g. "pat-001".</summary>
    public string? PatientId { get; init; }

    /// <summary>FHIR Encounter resource ID, e.g. "enc-001".</summary>
    public string? EncounterId { get; init; }

    /// <summary>OAuth2 client_id of the SMART application being launched (must belong to the caller's tenant).</summary>
    public required string ClientId { get; init; }

    /// <summary>
    /// The provider id (as bound by a provider enrolment) of the practitioner
    /// being launched. When set, only a provider user bound to that provider
    /// can use the launch; anyone else who obtains the launch token cannot use
    /// or burn it. EHR integrations should always send it.
    /// </summary>
    public string? PractitionerId { get; init; }
}

/// <summary>POST /launch response.</summary>
public record RegisterLaunchResponse(string Launch);
