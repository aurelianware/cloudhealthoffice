namespace SmartAuthService.Models;

/// <summary>
/// EHR launch context registered by the EHR system before redirecting the
/// provider to the SMART application.  Stored temporarily until the
/// authorization code is issued (TTL = SmartAuth:LaunchContextTtlMinutes).
/// </summary>
public class LaunchContext
{
    public string LaunchToken { get; init; } = string.Empty;
    public string? PatientId { get; init; }
    public string? EncounterId { get; init; }
    public string? PractitionerId { get; init; }
    public string ClientId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>POST /launch request body.</summary>
public class RegisterLaunchRequest
{
    /// <summary>FHIR Patient resource ID (without "Patient/" prefix), e.g. "pat-001".</summary>
    public string? PatientId { get; init; }

    /// <summary>FHIR Encounter resource ID, e.g. "enc-001".</summary>
    public string? EncounterId { get; init; }

    /// <summary>FHIR Practitioner resource ID for the launching provider.</summary>
    public string? PractitionerId { get; init; }

    /// <summary>OAuth2 client_id of the SMART application being launched.</summary>
    public required string ClientId { get; init; }
}

/// <summary>POST /launch response.</summary>
public record RegisterLaunchResponse(string Launch);
