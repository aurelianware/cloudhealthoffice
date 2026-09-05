using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirService.Services.PayerToPayer.Outbound;

/// <summary>
/// A remote payer's Payer-to-Payer endpoints, as resolved from the trusted
/// directory. Only the resolver constructs this type: every URI it carries has
/// already been validated, so nothing downstream re-derives a network location
/// from caller input.
/// </summary>
public sealed record PayerToPayerEndpoint
{
    /// <summary>The payer this endpoint belongs to (the id callers name).</summary>
    public required string PayerId { get; init; }

    /// <summary>
    /// Stable, opaque directory key for the endpoint. This — never the URL — is
    /// what audit entries and log lines identify the remote side by.
    /// </summary>
    public required string EndpointKey { get; init; }

    /// <summary>Absolute URI of the remote <c>Patient/$member-match</c> operation.</summary>
    public required Uri MemberMatchUri { get; init; }

    /// <summary>Absolute URI of the remote <c>PayerToPayer/$member-data-export</c> operation.</summary>
    public required Uri MemberDataExportUri { get; init; }

    /// <summary>
    /// Whether this payer requires <c>$member-match</c> before it will export.
    /// When false and CHO already holds the member's identifier with this payer,
    /// the match step is skipped.
    /// </summary>
    public bool RequiresMemberMatch { get; init; } = true;

    /// <summary>
    /// Names the credential the transport should present (looked up by an
    /// <see cref="IPayerToPayerCredentialProvider"/>). It is a key, never a
    /// secret value — no token or client secret is stored in the directory.
    /// </summary>
    public string? CredentialKey { get; init; }
}

/// <summary>
/// Resolves a target payer id to its Payer-to-Payer endpoints. This is the SSRF
/// boundary of the outbound workflow: a remote location can only ever come from
/// trusted configuration/directory data, never from a request body.
/// </summary>
public interface IPayerToPayerEndpointResolver
{
    /// <summary>
    /// The endpoints configured for <paramref name="targetPayerId"/> within
    /// <paramref name="tenantId"/>, or null when the payer is not in the tenant's
    /// directory or its entry fails validation (fail closed).
    /// </summary>
    PayerToPayerEndpoint? Resolve(string? tenantId, string? targetPayerId);
}

/// <summary>One payer's entry in the outbound Payer-to-Payer directory.</summary>
public sealed class PayerToPayerEndpointEntry
{
    /// <summary>Payer id callers name when initiating (e.g. a prior payer's plan id).</summary>
    public string PayerId { get; set; } = string.Empty;

    /// <summary>Optional opaque key used in audit/logs; defaults to the payer id.</summary>
    public string? EndpointKey { get; set; }

    /// <summary>
    /// Absolute base URL of the payer's FHIR R4 endpoint (e.g.
    /// <c>https://payer.example/fhir/r4</c>). HTTPS is required unless
    /// <see cref="PayerToPayerDirectoryOptions.AllowInsecureTransport"/> is
    /// explicitly enabled for a development environment.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    public bool RequiresMemberMatch { get; set; } = true;

    /// <summary>Key of the credential to present; resolved by the credential provider.</summary>
    public string? CredentialKey { get; set; }
}

/// <summary>
/// The outbound Payer-to-Payer directory, bound from configuration. Keyed by
/// tenant so one instance can serve several tenants without a caller being able
/// to reach another tenant's payers.
/// </summary>
public sealed class PayerToPayerDirectoryOptions
{
    public const string SectionName = "Cms0057:PayerToPayerOutbound";

    /// <summary>
    /// Payer id Cloud Health Office identifies itself as to the remote payer (the
    /// "receiving payer" of the exchange).
    /// </summary>
    public string LocalPayerId { get; set; } = "cloud-health-office";

    /// <summary>
    /// Allows plain-HTTP endpoints. Off by default: an <c>http://</c> entry is
    /// rejected rather than silently downgraded. Intended only for a local
    /// development peer.
    /// </summary>
    public bool AllowInsecureTransport { get; set; }

    /// <summary>Configured target payers, keyed by tenant id.</summary>
    public Dictionary<string, List<PayerToPayerEndpointEntry>> PayersByTenant { get; set; } = new();
}

/// <summary>
/// Configuration-driven, tenant-scoped, fail-closed endpoint resolver.
///
/// Validation is deliberately strict, because whatever this returns is a host
/// Cloud Health Office will call:
///   * the entry must belong to the requesting tenant;
///   * the base URL must be an absolute <c>https</c> URI (plain HTTP only when
///     <see cref="PayerToPayerDirectoryOptions.AllowInsecureTransport"/> is set);
///   * URLs carrying user info (<c>https://user:pw@host</c>), a query, or a
///     fragment are rejected — credentials belong in the credential provider,
///     not the directory;
///   * an unparseable or duplicate entry resolves to nothing rather than to a
///     best guess.
/// An empty directory resolves no payer at all.
/// </summary>
public sealed class ConfiguredPayerToPayerEndpointResolver : IPayerToPayerEndpointResolver
{
    /// <summary>Operation paths CHO calls on a peer — the same operations CHO itself serves (P2P-01 / P2P-04).</summary>
    internal const string MemberMatchPath = "Patient/$member-match";
    internal const string MemberDataExportPath = "PayerToPayer/$member-data-export";

    private readonly IOptions<PayerToPayerDirectoryOptions> _options;
    private readonly ILogger<ConfiguredPayerToPayerEndpointResolver> _logger;

    public ConfiguredPayerToPayerEndpointResolver(
        IOptions<PayerToPayerDirectoryOptions> options,
        ILogger<ConfiguredPayerToPayerEndpointResolver> logger)
    {
        _options = options;
        _logger = logger;
    }

    public PayerToPayerEndpoint? Resolve(string? tenantId, string? targetPayerId)
    {
        var tenant = tenantId?.Trim();
        var payerId = targetPayerId?.Trim();
        if (string.IsNullOrEmpty(tenant) || string.IsNullOrEmpty(payerId)) return null;

        if (!_options.Value.PayersByTenant.TryGetValue(tenant, out var entries) || entries is null)
            return null;

        var matches = entries
            .Where(e => string.Equals(e.PayerId?.Trim(), payerId, StringComparison.Ordinal))
            .ToList();

        // An ambiguous directory (the same payer configured twice) is a
        // configuration error, not something to pick a winner from.
        if (matches.Count != 1) return null;

        var entry = matches[0];
        if (!TryBuildBaseUri(entry.BaseUrl, out var baseUri))
        {
            _logger.LogWarning(
                "Payer-to-Payer directory entry for payer {PayerId} in tenant {Tenant} has an unusable base URL and was ignored.",
                Clean(payerId), Clean(tenant));
            return null;
        }

        return new PayerToPayerEndpoint
        {
            PayerId = payerId,
            EndpointKey = string.IsNullOrWhiteSpace(entry.EndpointKey) ? payerId : entry.EndpointKey.Trim(),
            MemberMatchUri = new Uri(baseUri, MemberMatchPath),
            MemberDataExportUri = new Uri(baseUri, MemberDataExportPath),
            RequiresMemberMatch = entry.RequiresMemberMatch,
            CredentialKey = string.IsNullOrWhiteSpace(entry.CredentialKey) ? null : entry.CredentialKey.Trim(),
        };
    }

    private bool TryBuildBaseUri(string? baseUrl, out Uri baseUri)
    {
        baseUri = null!;
        if (string.IsNullOrWhiteSpace(baseUrl)) return false;
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var parsed)) return false;

        var isHttps = string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal);
        var isHttp = string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal);
        if (!isHttps && !(isHttp && _options.Value.AllowInsecureTransport)) return false;

        // Credentials, query strings, and fragments have no place in a directory
        // entry; each is a sign the value was assembled from somewhere it
        // shouldn't have been.
        if (!string.IsNullOrEmpty(parsed.UserInfo)) return false;
        if (!string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment)) return false;

        if (isHttp)
        {
            _logger.LogWarning(
                "Payer-to-Payer directory uses plain HTTP for an endpoint (AllowInsecureTransport is enabled). "
                + "This is a development-only setting and must not be used with real member data.");
        }

        // A trailing slash makes Uri(base, relative) append rather than replace
        // the last path segment (e.g. .../fhir/r4 + Patient/$member-match).
        baseUri = parsed.AbsolutePath.EndsWith('/') ? parsed : new Uri(parsed.OriginalString.TrimEnd() + "/");
        return true;
    }

    /// <summary>Strips CR/LF so config/caller-derived values cannot forge log entries (CWE-117).</summary>
    private static string Clean(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("\r", string.Empty, StringComparison.Ordinal)
                   .Replace("\n", string.Empty, StringComparison.Ordinal);
}
