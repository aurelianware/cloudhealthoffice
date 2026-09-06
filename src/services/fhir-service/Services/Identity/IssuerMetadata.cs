using Microsoft.IdentityModel.Tokens;

namespace FhirService.Services.Identity;

/// <summary>Trust material successfully retrieved from one issuer.</summary>
public sealed record IssuerMetadata
{
    /// <summary>The <c>issuer</c> the discovery document itself declared.</summary>
    public required string Issuer { get; init; }

    public required string JwksUri { get; init; }

    public required IReadOnlyList<SecurityKey> SigningKeys { get; init; }

    /// <summary>Authorization endpoint, for SMART discovery passthrough.</summary>
    public string? AuthorizationEndpoint { get; init; }

    /// <summary>Token endpoint, for SMART discovery passthrough.</summary>
    public string? TokenEndpoint { get; init; }

    public DateTimeOffset RetrievedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Key ids present, for unknown-kid decisions and diagnostics.</summary>
    public IReadOnlySet<string> KeyIds =>
        SigningKeys.Select(k => k.KeyId).Where(id => !string.IsNullOrEmpty(id))
                   .ToHashSet(StringComparer.Ordinal)!;
}

/// <summary>
/// Retrieves an issuer's trust material. Abstracted so the key ring's caching,
/// rotation and outage behaviour can be driven deterministically in tests
/// without standing up an HTTPS issuer.
/// </summary>
public interface IIssuerMetadataFetcher
{
    Task<IssuerMetadata> FetchAsync(TrustedIssuerOptions issuer, CancellationToken ct = default);
}

/// <summary>Raised when an issuer's trust material cannot be established.</summary>
public sealed class IssuerMetadataException : Exception
{
    public IssuerMetadataException(string message) : base(message) { }
    public IssuerMetadataException(string message, Exception inner) : base(message, inner) { }
}
