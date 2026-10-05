namespace SmartAuthService.Services;

/// <summary>
/// The issuer smart-auth-service signs as, from <c>SmartAuth:Issuer</c>.
///
/// Without an explicit issuer OpenIddict derives one from each request's
/// scheme and Host (and X-Forwarded-* behind a forwarding proxy), so a caller
/// who controls those headers controls the <c>iss</c> of the tokens and the
/// endpoints and JWKS URI that discovery advertises. The issuer is therefore
/// configuration, and it must equal the value resource servers trust
/// (fhir-service <c>SmartAuth:Issuer</c> / <c>TrustedIssuers[].Issuer</c>).
/// </summary>
public static class SmartIssuer
{
    public const string ConfigKey = "SmartAuth:Issuer";

    /// <summary>
    /// The configured issuer. Required on every host; outside Development it
    /// must be HTTPS. Throws at startup rather than issuing tokens under a
    /// request-derived issuer.
    /// </summary>
    public static Uri Resolve(IConfiguration configuration, IHostEnvironment environment)
    {
        var raw = configuration[ConfigKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException(
                $"{ConfigKey} is required: tokens, discovery and JWKS metadata must name a fixed issuer, "
                + "never one derived from the request's Host or X-Forwarded headers.");
        }

        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var issuer)
            || (issuer.Scheme != Uri.UriSchemeHttps && issuer.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(issuer.Query) || !string.IsNullOrEmpty(issuer.Fragment)
            || !string.IsNullOrEmpty(issuer.UserInfo))
        {
            throw new InvalidOperationException(
                $"{ConfigKey} '{raw}' must be an absolute http(s) URI without query, fragment or user info.");
        }

        if (issuer.Scheme != Uri.UriSchemeHttps && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"{ConfigKey} '{raw}' must use HTTPS outside Development.");
        }

        return issuer;
    }

    /// <summary>
    /// Pins OpenIddict's base URI to the issuer. <c>SetIssuer</c> fixes
    /// <c>iss</c>, but OpenIddict still builds the absolute endpoint and
    /// <c>jwks_uri</c> URLs in discovery from the request's scheme and Host.
    /// This runs right after OpenIddict reads the request URI and rebases both
    /// the base and request URIs onto the issuer, so endpoint matching is
    /// unchanged (same relative path) and every URL it emits names the issuer.
    /// </summary>
    public static OpenIddict.Server.OpenIddictServerHandlerDescriptor BaseUriHandler(Uri issuer)
    {
        var fixedBase = new Uri(issuer.AbsoluteUri.EndsWith('/') ? issuer.AbsoluteUri : issuer.AbsoluteUri + "/");

        return OpenIddict.Server.OpenIddictServerHandlerDescriptor
            .CreateBuilder<OpenIddict.Server.OpenIddictServerEvents.ProcessRequestContext>()
            .UseInlineHandler(context =>
            {
                if (context.BaseUri is { } requestBase && context.RequestUri is { } requestUri
                    && requestUri.AbsoluteUri.StartsWith(requestBase.AbsoluteUri, StringComparison.Ordinal))
                {
                    context.RequestUri = new Uri(fixedBase, requestUri.AbsoluteUri[requestBase.AbsoluteUri.Length..]);
                }
                context.BaseUri = fixedBase;
                return default;
            })
            .SetOrder(OpenIddict.Server.AspNetCore.OpenIddictServerAspNetCoreHandlers.ResolveRequestUri.Descriptor.Order + 1)
            .SetType(OpenIddict.Server.OpenIddictServerHandlerType.Custom)
            .Build();
    }
}
