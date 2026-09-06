using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace FhirService.Services.Identity;

/// <summary>
/// Fetches an issuer's OIDC discovery document and JWKS over HTTP, validating
/// each hop before any of it becomes trust material.
///
/// The validation is not ceremony. Discovery is a document fetched from the
/// network that then tells CHO where to get the keys it will verify tokens
/// against — so an unchecked discovery response is a redirection primitive
/// aimed at the one decision that matters. Three things are therefore checked
/// before a single key is read:
///
///   1. The document's own <c>issuer</c> equals the configured issuer exactly.
///      A document that names a different issuer is a document for a different
///      trust relationship, whatever host served it.
///   2. The <c>jwks_uri</c> is HTTPS outside development.
///   3. The jwks_uri host passes <see cref="JwksOriginPolicy"/> — the issuer's
///      own host, or one an administrator listed. This is what stops a
///      compromised or misconfigured discovery document from pointing key
///      retrieval at an arbitrary origin.
/// </summary>
public sealed class HttpIssuerMetadataFetcher : IIssuerMetadataFetcher
{
    public const string HttpClientName = "smart-issuer-metadata";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly bool _isDevelopmentHost;

    public HttpIssuerMetadataFetcher(IHttpClientFactory httpClientFactory, IHostEnvironment environment)
    {
        _httpClientFactory = httpClientFactory;
        _isDevelopmentHost = environment.IsDevelopment();
    }

    public async Task<IssuerMetadata> FetchAsync(
        TrustedIssuerOptions issuer, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);

        // An explicitly configured JWKS URI skips discovery: the administrator
        // has already named the endpoint, so there is nothing for a discovery
        // document to add and one less network-supplied value to validate.
        if (!string.IsNullOrWhiteSpace(issuer.JwksUri))
        {
            var directUri = RequireAllowedUri(issuer.JwksUri!, issuer, "JwksUri");
            return new IssuerMetadata
            {
                Issuer = issuer.Issuer,
                JwksUri = directUri.ToString(),
                SigningKeys = await FetchKeysAsync(client, directUri, issuer, ct),
            };
        }

        var discoveryUri = RequireAllowedUri(issuer.EffectiveDiscoveryUrl(), issuer, "DiscoveryUrl");

        using var response = await client.GetAsync(discoveryUri, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new IssuerMetadataException(
                $"Discovery for issuer '{issuer.Issuer}' returned HTTP {(int)response.StatusCode}.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch (JsonException ex)
        {
            throw new IssuerMetadataException(
                $"Discovery document for issuer '{issuer.Issuer}' is not valid JSON.", ex);
        }

        using (document)
        {
            var root = document.RootElement;

            var declaredIssuer = ReadString(root, "issuer")
                ?? throw new IssuerMetadataException(
                    $"Discovery document for issuer '{issuer.Issuer}' has no 'issuer' member.");

            // Exact, ordinal. This is the single check that binds a fetched
            // document to the trust relationship it claims to describe.
            if (!string.Equals(declaredIssuer, issuer.Issuer, StringComparison.Ordinal))
            {
                throw new IssuerMetadataException(
                    $"Discovery document issuer mismatch: configured '{issuer.Issuer}', "
                    + $"document declared '{Sanitize(declaredIssuer)}'.");
            }

            var jwksUriValue = ReadString(root, "jwks_uri")
                ?? throw new IssuerMetadataException(
                    $"Discovery document for issuer '{issuer.Issuer}' has no 'jwks_uri'.");

            var jwksUri = RequireAllowedUri(jwksUriValue, issuer, "discovered jwks_uri");

            return new IssuerMetadata
            {
                Issuer = declaredIssuer,
                JwksUri = jwksUri.ToString(),
                AuthorizationEndpoint = ReadString(root, "authorization_endpoint"),
                TokenEndpoint = ReadString(root, "token_endpoint"),
                SigningKeys = await FetchKeysAsync(client, jwksUri, issuer, ct),
            };
        }
    }

    private async Task<IReadOnlyList<SecurityKey>> FetchKeysAsync(
        HttpClient client, Uri jwksUri, TrustedIssuerOptions issuer, CancellationToken ct)
    {
        using var response = await client.GetAsync(jwksUri, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new IssuerMetadataException(
                $"JWKS for issuer '{issuer.Issuer}' returned HTTP {(int)response.StatusCode}.");
        }

        var body = await response.Content.ReadAsStringAsync(ct);

        JsonWebKeySet keySet;
        try
        {
            keySet = new JsonWebKeySet(body);
        }
        catch (Exception ex)
        {
            throw new IssuerMetadataException(
                $"JWKS for issuer '{issuer.Issuer}' could not be parsed.", ex);
        }

        var allowed = issuer.EffectiveAlgorithms().ToHashSet(StringComparer.Ordinal);

        // Filter at the point of ingestion, not at validation time. A key whose
        // algorithm CHO does not accept should never enter the key ring, so it
        // cannot later be selected by a token that nominates it.
        //
        // The `alg` has to be read from the RAW JWKS entries: GetSigningKeys()
        // converts each JWK into an RsaSecurityKey or ECDsaSecurityKey and the
        // declared algorithm does not survive the conversion, so inspecting only
        // the converted keys would silently accept every algorithm.
        var refusedKids = keySet.Keys
            .Where(k => !string.IsNullOrEmpty(k.Alg) && !allowed.Contains(k.Alg))
            .Select(k => k.Kid)
            .Where(kid => !string.IsNullOrEmpty(kid))
            .ToHashSet(StringComparer.Ordinal);

        var keys = keySet.GetSigningKeys()
            .Where(k => IsAcceptableKey(k, allowed))
            .Where(k => string.IsNullOrEmpty(k.KeyId) || !refusedKids.Contains(k.KeyId))
            .ToList();

        if (keys.Count == 0)
        {
            throw new IssuerMetadataException(
                $"JWKS for issuer '{issuer.Issuer}' contained no usable signing key for the "
                + $"accepted algorithms ({string.Join(", ", allowed.OrderBy(a => a, StringComparer.Ordinal))}).");
        }

        return keys;
    }

    /// <summary>
    /// Asymmetric keys only, and only where the key's own declared algorithm
    /// (when it declares one) is accepted. A symmetric key arriving in a JWKS
    /// from a third-party issuer has no legitimate role in resource-server
    /// validation and is the ingredient of alg-confusion, so it is refused at
    /// the door rather than relied on being unreachable later.
    /// </summary>
    private static bool IsAcceptableKey(SecurityKey key, IReadOnlySet<string> allowedAlgorithms)
    {
        if (key is SymmetricSecurityKey) return false;

        if (key is JsonWebKey jwk)
        {
            if (!string.IsNullOrEmpty(jwk.Alg) && !allowedAlgorithms.Contains(jwk.Alg)) return false;
            if (string.Equals(jwk.Kty, "oct", StringComparison.Ordinal)) return false;
        }

        return true;
    }

    private Uri RequireAllowedUri(string value, TrustedIssuerOptions issuer, string field)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new IssuerMetadataException(
                $"Issuer '{issuer.Issuer}' {field} is not an absolute URI.");
        }

        if (!_isDevelopmentHost &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            throw new IssuerMetadataException(
                $"Issuer '{issuer.Issuer}' {field} must use HTTPS outside development.");
        }

        if (!JwksOriginPolicy.IsAllowedHost(uri, issuer, _isDevelopmentHost))
        {
            throw new IssuerMetadataException(
                $"Issuer '{issuer.Issuer}' {field} host '{Sanitize(uri.Host)}' is not permitted. "
                + "It is neither the issuer's own host nor listed in AdditionalJwksHosts.");
        }

        return uri;
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static string Sanitize(string value)
        => value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
