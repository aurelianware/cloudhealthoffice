namespace CloudHealthOffice.Infrastructure.Security;

/// <summary>
/// Well-known symmetric key and settings for local development and automated
/// tests. <see cref="ChoAuthOptions.Validate"/> refuses symmetric keys on any
/// host other than Development or Testing, so this key cannot authenticate
/// anything in a deployed environment.
/// </summary>
public static class ChoDevelopmentAuth
{
    public const string UserIssuer = "cho-portal-dev";
    public const string ServiceIssuer = "cho-internal-dev";
    public const string Audience = "cho-api";

    /// <summary>Public, intentionally. Never configure it outside Development/Testing.</summary>
    public const string SymmetricKey = "Q0hPLWRldmVsb3BtZW50LW9ubHktc2lnbmluZy1rZXktZG8tbm90LXVzZS0yMDI2";

    /// <summary>Configuration entries that make a service trust the development issuers.</summary>
    public static IReadOnlyDictionary<string, string?> Configuration(string? serviceClientId = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ChoAuth:Audience"] = Audience,
            ["ChoAuth:Issuers:0:Issuer"] = UserIssuer,
            ["ChoAuth:Issuers:0:SymmetricKey"] = SymmetricKey,
            ["ChoAuth:Issuers:1:Issuer"] = ServiceIssuer,
            ["ChoAuth:Issuers:1:SymmetricKey"] = SymmetricKey,
            ["ChoAuth:Issuers:1:AllowServiceRole"] = "true",
        };

        if (serviceClientId != null)
        {
            settings["ChoAuth:ServiceToken:Issuer"] = ServiceIssuer;
            settings["ChoAuth:ServiceToken:ClientId"] = serviceClientId;
            settings["ChoAuth:ServiceToken:SymmetricKey"] = SymmetricKey;
        }

        return settings;
    }

    public static ChoTokenIssuer UserTokenIssuer(TimeSpan? lifetime = null)
        => ChoTokenIssuer.FromKeys(UserIssuer, Audience, null, SymmetricKey, lifetime ?? TimeSpan.FromMinutes(5));

    public static ChoTokenIssuer ServiceTokenIssuer(TimeSpan? lifetime = null)
        => ChoTokenIssuer.FromKeys(ServiceIssuer, Audience, null, SymmetricKey, lifetime ?? TimeSpan.FromMinutes(5));

    /// <summary>A development user token with the given tenant and roles.</summary>
    public static string UserToken(string tenantId, params string[] roles)
        => UserTokenIssuer().IssueUserToken("dev-user-" + tenantId, tenantId, roles, name: "Development User");
}

/// <summary>
/// Test/development client handler: turns an <c>X-Tenant-ID</c> on an outgoing
/// request into a development-signed user token for that tenant. It exists so
/// integration tests can keep naming the tenant they act in; the server still
/// takes the tenant from the token this handler mints.
/// </summary>
public sealed class ChoDevelopmentTokenHandler : DelegatingHandler
{
    private readonly string[] _roles;
    private readonly string _subject;

    public ChoDevelopmentTokenHandler(params string[] roles)
        : this("dev-user", roles)
    {
    }

    public ChoDevelopmentTokenHandler(string subject, params string[] roles)
    {
        _subject = subject;
        _roles = roles.Length > 0 ? roles : [ChoRolePermissions.TenantAdmin];
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization == null
            && request.Headers.TryGetValues(Middleware.TenantMiddleware.TenantHeaderName, out var values)
            && values.FirstOrDefault() is { Length: > 0 } tenant)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken(_subject, tenant, _roles));
        }

        return base.SendAsync(request, cancellationToken);
    }
}
