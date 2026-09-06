using System.Security.Claims;

namespace FhirService.Services.Identity;

/// <summary>What kind of principal a token represents, in SMART's terms.</summary>
public enum SmartCallerType
{
    /// <summary>Patient-context token: a member acting on their own record.</summary>
    Patient,

    /// <summary>User-context token: a human, typically a provider, acting through an app.</summary>
    User,

    /// <summary>Client-credentials token: a backend service with no human present.</summary>
    System,

    /// <summary>Authenticated, but carrying no scope that names a SMART context.</summary>
    Unknown,
}

/// <summary>
/// Who the caller is, resolved once per request from the token and the trusted
/// issuer's claim mapping.
///
/// This type exists because identity was previously re-derived wherever it was
/// needed — a <c>patient</c> claim read in the middleware, a tenant read in
/// another, scopes parsed into <c>HttpContext.Items</c>, and provider identity
/// nowhere at all. Each reader was individually reasonable and collectively
/// they could disagree, which is how a check ends up guarding one path and not
/// its neighbour. One resolution, one shape, read everywhere.
///
/// Every field is derived from a trusted issuer's token. Nothing here comes
/// from a query string, a body, or an unauthenticated header.
/// </summary>
public sealed record AuthenticatedCaller
{
    /// <summary>The issuer that vouched for this caller. Always a configured, trusted one.</summary>
    public required string Issuer { get; init; }

    /// <summary>Token <c>sub</c>. Opaque; safe to audit.</summary>
    public string? Subject { get; init; }

    /// <summary>OAuth client id, from the mapped claim then <c>azp</c> then <c>client_id</c>.</summary>
    public string? ClientId { get; init; }

    /// <summary><c>azp</c> where the issuer emits it, kept distinct from ClientId.</summary>
    public string? AuthorizedParty { get; init; }

    public required SmartCallerType CallerType { get; init; }

    /// <summary>Granted SMART scopes, already parsed.</summary>
    public required IReadOnlySet<string> Scopes { get; init; }

    /// <summary>
    /// The caller's provider NPI — present ONLY when the trusted issuer was
    /// configured with a <see cref="IssuerClaimMappingOptions.ProviderNpiClaim"/>
    /// and the token carried it. Absent otherwise, which is not the same as
    /// "the caller has no NPI": it means no issuer CHO trusts has asserted one,
    /// so nothing may be authorized on it.
    /// </summary>
    public string? ProviderNpi { get; init; }

    /// <summary>FHIR Practitioner id from the mapped claim, when configured.</summary>
    public string? PractitionerId { get; init; }

    /// <summary>SMART <c>fhirUser</c>, when the issuer emits it.</summary>
    public string? FhirUser { get; init; }

    /// <summary>The patient a patient-context token is bound to, unprefixed.</summary>
    public string? PatientId { get; init; }

    /// <summary>Tenant asserted by the token, when the issuer maps one.</summary>
    public string? TenantClaim { get; init; }

    /// <summary>True when a trusted issuer asserted a provider NPI for this caller.</summary>
    public bool HasVerifiedProviderIdentity => !string.IsNullOrEmpty(ProviderNpi);

    /// <summary>
    /// PHI-free audit projection. Subject and client id are opaque issuer
    /// identifiers, not names; no scope values, no token, no claim payload.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToAuditFields() => new Dictionary<string, string>
    {
        ["issuer"] = Sanitize(Issuer) ?? "(none)",
        ["subject"] = Sanitize(Subject) ?? "(none)",
        ["clientId"] = Sanitize(ClientId) ?? "(none)",
        ["callerType"] = CallerType.ToString(),
        ["providerIdentity"] = HasVerifiedProviderIdentity ? "asserted" : "none",
    };

    private static string? Sanitize(string? value)
        => value?.Replace("\r", string.Empty).Replace("\n", string.Empty);

    /// <summary>The key under which the resolved caller travels on HttpContext.</summary>
    public const string HttpContextItemKey = "SmartCaller";
}
