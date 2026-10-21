using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.TokenService.Directory;
using CloudHealthOffice.TokenService.Exchange;
using CloudHealthOffice.TokenService.Signing;

namespace CloudHealthOffice.TokenService.CloudflareAccess;

/// <summary>
/// Bound from the <c>CloudflareAccess</c> section. Lets people whom Cloudflare
/// Access has signed in obtain CHO user tokens, for the tenants listed in
/// <see cref="Tenants"/> only. Off unless <see cref="Enabled"/>.
/// </summary>
/// <remarks>
/// Cloudflare Access vouches for the email address (through whichever identity
/// provider the Access policy allows: Google Workspace, Microsoft, one-time
/// PIN). That is a weaker statement than a tenant's own Entra directory, so
/// it is accepted only where a deployment says so, and a person still needs
/// an Active user record with that address in the tenant.
/// </remarks>
public sealed class CloudflareAccessExchangeOptions
{
    public const string SectionName = "CloudflareAccess";

    public bool Enabled { get; set; }

    /// <inheritdoc cref="CloudflareAccessOptions.TeamDomain"/>
    public string TeamDomain { get; set; } = string.Empty;

    /// <inheritdoc cref="CloudflareAccessOptions.Audiences"/>
    public List<string> Audiences { get; set; } = new();

    /// <inheritdoc cref="CloudflareAccessOptions.JwksJson"/>
    public string? JwksJson { get; set; }

    /// <summary>CHO tenants that accept Cloudflare Access sign-in. Required; no wildcard.</summary>
    public List<string> Tenants { get; set; } = new();

    public CloudflareAccessOptions ToValidatorOptions() => new()
    {
        TeamDomain = TeamDomain,
        Audiences = Audiences,
        JwksJson = JwksJson,
    };

    public void Validate()
    {
        if (!Enabled)
            return;
        ToValidatorOptions().Validate(SectionName);
        if (Tenants.Count == 0 || Tenants.Any(t => string.IsNullOrWhiteSpace(t) || t.Contains('*', StringComparison.Ordinal)))
            throw new InvalidOperationException(
                $"{SectionName}:Tenants must list the CHO tenants that accept Cloudflare Access sign-in (no wildcards).");
    }
}

/// <summary>
/// Issues CHO user tokens to people Cloudflare Access signed in. The same
/// contract as the Entra exchange (<c>/v1/token/exchange</c>), so the portal
/// handles both alike.
/// </summary>
public sealed class CloudflareAccessExchangeService
{
    private readonly ITenantDirectory _directory;
    private readonly ISigningMaterialSource _signing;
    private readonly TokenSigningOptions _signingOptions;
    private readonly TokenServiceOptions _options;
    private readonly CloudflareAccessExchangeOptions _access;
    private readonly TokenAudit _audit;

    public CloudflareAccessExchangeService(
        ITenantDirectory directory,
        ISigningMaterialSource signing,
        TokenSigningOptions signingOptions,
        TokenServiceOptions options,
        CloudflareAccessExchangeOptions access,
        TokenAudit audit)
    {
        _directory = directory;
        _signing = signing;
        _signingOptions = signingOptions;
        _options = options;
        _access = access;
        _audit = audit;
    }

    /// <summary>Enabled tenants where this person is an Active user.</summary>
    public async Task<IReadOnlyList<TenantChoice>> GetAccessibleTenantsAsync(CloudflareAccessIdentity identity, CancellationToken ct)
    {
        var result = new List<TenantChoice>();
        foreach (var tenantId in _access.Tenants.Distinct(StringComparer.Ordinal))
        {
            var tenant = await _directory.GetTenantAsync(tenantId, ct);
            if (tenant is not { IsActive: true })
                continue;
            if (await FindMemberAsync(tenant, identity, ct) is { IsActive: true })
                result.Add(TenantChoice.From(tenant));
        }
        return result;
    }

    public async Task<ExchangeOutcome> ExchangeAsync(CloudflareAccessIdentity identity, string? requestedTenantId, CancellationToken ct)
    {
        string tenantId;
        if (string.IsNullOrWhiteSpace(requestedTenantId))
        {
            var accessible = await GetAccessibleTenantsAsync(identity, ct);
            if (accessible.Count == 0)
                return Refuse(identity, null, null, "no_accessible_tenant");
            if (accessible.Count > 1)
            {
                _audit.AccessRefused(identity, null, null, "tenant_required");
                return ExchangeOutcome.TenantRequired(accessible);
            }
            tenantId = accessible[0].TenantId;
        }
        else
        {
            tenantId = requestedTenantId.Trim();
        }

        if (!_access.Tenants.Contains(tenantId, StringComparer.Ordinal))
            return Refuse(identity, tenantId, null, "tenant_not_enabled");

        var tenant = await _directory.GetTenantAsync(tenantId, ct);
        if (tenant == null)
            return Refuse(identity, tenantId, null, "unknown_tenant");
        if (!tenant.IsActive)
            return Refuse(identity, tenantId, null, "tenant_inactive");

        var member = await FindMemberAsync(tenant, identity, ct);
        if (member == null)
            return Refuse(identity, tenantId, null, "no_membership");
        if (!member.IsActive)
            return Refuse(identity, tenantId, member.Id, "user_inactive");

        var roles = TokenExchangeService.TenantRoles(member.Roles);
        var permissions = ChoRolePermissions.Expand(roles).OrderBy(p => p, StringComparer.Ordinal).ToList();
        var display = member.DisplayName is { Length: > 0 } dn ? dn : member.Email;

        string token;
        try
        {
            var material = await _signing.GetAsync(ct);
            var issuer = new ChoTokenIssuer(_signingOptions.Issuer, _signingOptions.Audience, material.Credentials, _options.TokenLifetime);
            token = issuer.IssueUserToken(member.Id, tenant.TenantId, roles, permissions,
                name: string.IsNullOrEmpty(display) ? null : display,
                email: member.Email);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SigningUnavailableException(ex);
        }

        _audit.AccessIssued(identity, tenant.TenantId, member.Id, roles);

        return ExchangeOutcome.Issued(new ExchangeResponse
        {
            AccessToken = token,
            ExpiresIn = (int)_options.TokenLifetime.TotalSeconds,
            TenantId = tenant.TenantId,
            TenantName = tenant.TenantName,
            Roles = roles,
            Permissions = permissions,
            User = new ExchangeUser
            {
                Id = member.Id,
                Email = member.Email,
                DisplayName = display,
                FirstName = member.FirstName,
                LastName = member.LastName,
                Department = member.Department,
            },
        });
    }

    /// <summary>
    /// The tenant's user with exactly this address. A user linked to an Entra
    /// identity is still matched: the address is the identity here, and the
    /// tenant opted in to Cloudflare Access sign-in.
    /// </summary>
    private async Task<DirectoryUser?> FindMemberAsync(DirectoryTenant tenant, CloudflareAccessIdentity identity, CancellationToken ct)
    {
        var user = await _directory.FindUserByEmailAsync(tenant.TenantId, identity.Email, ct);
        return user != null
               && string.Equals(user.TenantId is { Length: > 0 } t ? t : tenant.TenantId, tenant.TenantId, StringComparison.Ordinal)
               && string.Equals(user.Email?.Trim(), identity.Email, StringComparison.OrdinalIgnoreCase)
            ? user
            : null;
    }

    private ExchangeOutcome Refuse(CloudflareAccessIdentity identity, string? tenantId, string? subject, string reason)
    {
        _audit.AccessRefused(identity, tenantId, subject, reason);
        return ExchangeOutcome.NoAccess();
    }
}

public static class CloudflareAccessEndpoints
{
    public const string ExchangePath = "/v1/token/exchange/cloudflare-access";
    public const string TenantsPath = "/v1/token/tenants/cloudflare-access";

    public static bool IsPath(PathString path)
        => path.Equals(ExchangePath, StringComparison.OrdinalIgnoreCase)
           || path.Equals(TenantsPath, StringComparison.OrdinalIgnoreCase);

    public static IServiceCollection AddCloudflareAccessExchange(this IServiceCollection services, CloudflareAccessExchangeOptions options)
    {
        services.AddSingleton(options);
        if (!options.Enabled)
            return services;

        services.AddSingleton<ICloudflareAccessTokenValidator>(_ =>
            CloudflareAccessTokenValidator.Create(options.ToValidatorOptions(), new HttpClient { Timeout = TimeSpan.FromSeconds(10) }));
        services.AddScoped<CloudflareAccessExchangeService>();
        return services;
    }

    public static IEndpointRouteBuilder MapCloudflareAccessExchange(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetRequiredService<CloudflareAccessExchangeOptions>();
        if (!options.Enabled)
            return app;

        app.MapPost(ExchangePath, async (HttpContext http, ICloudflareAccessTokenValidator validator,
            CloudflareAccessExchangeService exchange, TokenAudit audit) =>
        {
            var validated = await validator.ValidateAsync(BearerToken(http), http.RequestAborted);
            if (validated.Identity is not { } identity)
                return Unauthorized(http, audit, validated.Reason);

            string? tenantId = null;
            if (http.Request.ContentLength is > 0 || http.Request.Headers.TransferEncoding.Count > 0)
            {
                try
                {
                    var body = await http.Request.ReadFromJsonAsync<AccessExchangeRequest>(http.RequestAborted);
                    tenantId = body?.TenantId;
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                {
                    return Results.Json(new { error = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest);
                }
            }

            try
            {
                var outcome = await exchange.ExchangeAsync(identity, tenantId, http.RequestAborted);
                if (outcome.Response != null)
                {
                    http.Response.Headers.CacheControl = "no-store";
                    http.Response.Headers.Pragma = "no-cache";
                    return Results.Json(outcome.Response);
                }
                if (outcome.Choices != null)
                    return Results.Json(new { error = "tenant_required", tenants = outcome.Choices }, statusCode: StatusCodes.Status409Conflict);
                return Results.Json(new { error = "no_access" }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (TenantDirectoryUnavailableException)
            {
                audit.AccessUnavailable(identity, tenantId, "tenant_service_unavailable");
                return Results.Json(new { error = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (SigningUnavailableException ex)
            {
                audit.AccessUnavailable(identity, tenantId, "signing_unavailable:" + ex.InnerException?.GetType().Name);
                return Results.Json(new { error = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
        // Authenticated here by the Cloudflare Access token, not the Entra scheme.
        .AllowAnonymous();

        app.MapGet(TenantsPath, async (HttpContext http, ICloudflareAccessTokenValidator validator,
            CloudflareAccessExchangeService exchange, TokenAudit audit) =>
        {
            var validated = await validator.ValidateAsync(BearerToken(http), http.RequestAborted);
            if (validated.Identity is not { } identity)
                return Unauthorized(http, audit, validated.Reason);
            try
            {
                return Results.Json(await exchange.GetAccessibleTenantsAsync(identity, http.RequestAborted));
            }
            catch (TenantDirectoryUnavailableException)
            {
                audit.AccessUnavailable(identity, null, "tenant_service_unavailable");
                return Results.Json(new { error = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
        .AllowAnonymous();

        return app;
    }

    private static string? BearerToken(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
    }

    private static IResult Unauthorized(HttpContext http, TokenAudit audit, string? reason)
    {
        audit.AccessRefused(null, null, null, reason ?? "invalid_token");
        http.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
        return Results.Json(new { error = "invalid_token" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    private sealed record AccessExchangeRequest(string? TenantId);
}
