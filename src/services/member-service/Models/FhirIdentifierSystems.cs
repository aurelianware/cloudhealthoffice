namespace MemberService.Models;

/// <summary>
/// Central registry of identifier system URIs used by the Member Service.
/// Keeps FHIR Identifier.system values DRY across projector, controllers, and tests.
/// </summary>
public static class FhirIdentifierSystems
{
    // Standard FHIR US-Core / sid URIs
    public const string SSN = "http://hl7.org/fhir/sid/us-ssn";
    public const string MedicareMbi = "http://hl7.org/fhir/sid/us-mbi";
    public const string Medicaid = "http://hl7.org/fhir/sid/us-medicaid";

    // CHO-internal URIs (tenant scoping is carried in the identifier value prefix)
    public const string MemberId = "urn:cho:member-id";
    public const string PortalId = "urn:cho:portal-id";
    public const string ExchangeId = "urn:cho:exchange-id";

    /// <summary>
    /// Legacy/external identifier URI prefix. Concatenate with a tenant-config-driven
    /// slug (e.g. "urn:cho:legacy:acme-enrollment-v1").
    /// </summary>
    public const string LegacyPrefix = "urn:cho:legacy";

    public static string LegacyForSystem(string systemSlug)
    {
        if (string.IsNullOrWhiteSpace(systemSlug))
            throw new ArgumentException("systemSlug is required", nameof(systemSlug));
        return $"{LegacyPrefix}:{systemSlug}";
    }

    /// <summary>
    /// Map a <see cref="MemberIdentifierType"/> to its canonical system URI.
    /// Legacy requires a slug and must be built via <see cref="LegacyForSystem"/>.
    /// </summary>
    public static string FromType(MemberIdentifierType type) => type switch
    {
        MemberIdentifierType.SSN => SSN,
        MemberIdentifierType.MedicareMbi => MedicareMbi,
        MemberIdentifierType.Medicaid => Medicaid,
        MemberIdentifierType.MemberId => MemberId,
        MemberIdentifierType.Portal => PortalId,
        MemberIdentifierType.Exchange => ExchangeId,
        MemberIdentifierType.Legacy => LegacyPrefix,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown identifier type")
    };
}
