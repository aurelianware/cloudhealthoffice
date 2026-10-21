using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.TokenService.Exchange;
using CloudHealthOffice.TokenService.Signing;
using CloudHealthOffice.TokenService.Workload;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CloudHealthOffice.TokenService.ServiceTokens;

/// <summary>
/// <c>POST /v1/token/service</c>: a CHO service proves its workload identity
/// and receives a short-lived CHO service token naming itself. The proof is an
/// Entra access token from its Azure workload identity
/// (<see cref="ServiceIdentityProvider.Entra"/>), or its projected Kubernetes
/// service-account token (<see cref="ServiceIdentityProvider.Kubernetes"/>,
/// any conformant cluster).
///
/// The caller names only the tenant. Which service it is comes from the
/// validated identity (managed identity <c>oid</c>, or namespace and service
/// account) and the configured registry, so a service can only ever obtain
/// tokens naming itself. The token
/// has exactly the claims services used to mint locally
/// (<see cref="ChoTokenIssuer.IssueServiceToken"/>: <c>sub</c> = <c>azp</c> =
/// client id, role <c>cho.service</c>, <c>tenant_id</c>), signed with
/// token-service's key under its own issuer (<see cref="ServiceTokenOptions.Issuer"/>),
/// which services trust as a <c>Service</c> issuer.
///
/// Tenant scope is unchanged from local minting: a registered service may
/// name any tenant (it acts for tenants taken from its own messages and
/// records). Every issuance is audited with client and tenant.
/// </summary>
public static class ServiceTokenEndpoint
{
    public const string Path = "/v1/token/service";

    public static IServiceCollection AddServiceTokens(
        this IServiceCollection services, ServiceTokenOptions options, IHostEnvironment environment)
    {
        services.AddSingleton(options);
        if (!options.Enabled)
            return services;

        if (options.IdentityProvider == ServiceIdentityProvider.Kubernetes)
        {
            // Its own validator instance: the cluster issuer here may differ from
            // WorkloadTokens', and the two endpoints must not share key caches.
            var k8s = options.Kubernetes.ToValidatorOptions();
            var staticJwks = k8s.StaticJwks();
            IKubernetesSigningKeys keys = staticJwks != null
                ? new StaticKubernetesSigningKeys(staticJwks)
                : new DiscoveryKubernetesSigningKeys(k8s, new HttpClient { Timeout = TimeSpan.FromSeconds(10) },
                    requireHttps: !environment.IsDevelopment());
            services.TryAddSingleton<IServiceIdentityValidator>(sp =>
                new KubernetesServiceIdentityValidator(new KubernetesTokenValidator(k8s, keys, sp.GetService<TimeProvider>())));
        }
        else
        {
            services.TryAddSingleton<IEntraSigningKeys>(_ =>
                !string.IsNullOrWhiteSpace(options.JwksJson)
                    ? new StaticEntraSigningKeys(options.JwksJson!)
                    : new DiscoveryEntraSigningKeys(options.EffectiveMetadataAddress,
                        new HttpClient { Timeout = TimeSpan.FromSeconds(10) }, requireHttps: !environment.IsDevelopment()));
            services.TryAddSingleton<IEntraWorkloadTokenValidator>(sp =>
                new EntraWorkloadTokenValidator(options, sp.GetRequiredService<IEntraSigningKeys>()));
            services.TryAddSingleton<IServiceIdentityValidator>(sp =>
                new EntraServiceIdentityValidator(sp.GetRequiredService<IEntraWorkloadTokenValidator>()));
        }
        services.AddSingleton(new ServiceClientRegistry(options.AllServiceClients()));
        services.AddSingleton<ServiceTokenService>();
        return services;
    }

    public static IEndpointRouteBuilder MapServiceTokens(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetRequiredService<ServiceTokenOptions>();
        if (!options.Enabled)
            return app;

        app.MapPost(Path, async (HttpContext http, ServiceTokenService service) =>
        {
            var header = http.Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;

            string? tenantId = null;
            var bodyMalformed = false;
            try
            {
                var body = await http.Request.ReadFromJsonAsync<ServiceTokenRequest>(http.RequestAborted);
                tenantId = body?.TenantId;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                bodyMalformed = true;
            }

            var outcome = await service.IssueAsync(token, tenantId, bodyMalformed, http.RequestAborted);
            if (outcome.Response != null)
            {
                http.Response.Headers.CacheControl = "no-store";
                http.Response.Headers.Pragma = "no-cache";
                return Results.Json(outcome.Response);
            }
            if (outcome.Status == StatusCodes.Status401Unauthorized)
                http.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
            return Results.Json(new { error = outcome.Error }, statusCode: outcome.Status);
        })
        // Authenticated here by the workload-identity proof, not by the user (delegated) Entra scheme.
        .AllowAnonymous();

        return app;
    }
}

/// <summary>
/// The registry: a workload identity (managed identity object id, or Kubernetes
/// namespace and service account) to CHO service client id. Exact matches only.
/// </summary>
public sealed class ServiceClientRegistry
{
    private readonly Dictionary<Guid, ServiceClientRegistration> _byObjectId = new();
    private readonly Dictionary<string, ServiceClientRegistration> _byServiceAccount = new(StringComparer.Ordinal);

    public ServiceClientRegistry(IEnumerable<ServiceClientRegistration> registrations)
    {
        foreach (var r in registrations)
        {
            if (!string.IsNullOrWhiteSpace(r.ObjectId))
                _byObjectId.Add(Guid.Parse(r.ObjectId), r);
            else if (!string.IsNullOrWhiteSpace(r.Namespace) && !string.IsNullOrWhiteSpace(r.ServiceAccount))
                _byServiceAccount.Add(r.Namespace + "/" + r.ServiceAccount, r);
        }
    }

    public ServiceClientRegistration? Find(ServiceIdentity identity)
    {
        if (identity.Kubernetes is { } k8s)
            return _byServiceAccount.GetValueOrDefault(k8s.Namespace + "/" + k8s.ServiceAccount);
        return identity.Entra is { } entra ? Find(entra) : null;
    }

    public ServiceClientRegistration? Find(EntraWorkloadIdentity identity)
    {
        if (!_byObjectId.TryGetValue(identity.ObjectId, out var registration))
            return null;
        // When the registry also pins the identity's client id, the token must name it.
        if (!string.IsNullOrWhiteSpace(registration.AppId)
            && !(Guid.TryParse(identity.AppId, out var app) && app == Guid.Parse(registration.AppId)))
            return null;
        return registration;
    }
}

public sealed class ServiceTokenService
{
    private readonly IServiceIdentityValidator _validator;
    private readonly ServiceClientRegistry _registry;
    private readonly ServiceTokenOptions _options;
    private readonly ISigningMaterialSource _signing;
    private readonly TokenSigningOptions _signingOptions;
    private readonly TokenAudit _audit;

    public ServiceTokenService(
        IServiceIdentityValidator validator,
        ServiceClientRegistry registry,
        ServiceTokenOptions options,
        ISigningMaterialSource signing,
        TokenSigningOptions signingOptions,
        TokenAudit audit)
    {
        _validator = validator;
        _registry = registry;
        _options = options;
        _signing = signing;
        _signingOptions = signingOptions;
        _audit = audit;
    }

    public async Task<ServiceTokenOutcome> IssueAsync(string? token, string? tenantId, bool bodyMalformed, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token))
            return Refuse(null, null, tenantId, "missing_token", StatusCodes.Status401Unauthorized, "invalid_token");

        var validation = await _validator.ValidateAsync(token, ct);
        if (validation.Identity == null)
        {
            if (validation.Reason == "issuer_keys_unavailable")
            {
                _audit.ServiceUnavailable(null, tenantId, validation.Reason);
                return ServiceTokenOutcome.Fail(StatusCodes.Status503ServiceUnavailable, "unavailable");
            }
            return Refuse(null, null, tenantId, validation.Reason ?? "invalid_token", StatusCodes.Status401Unauthorized, "invalid_token");
        }

        var identity = validation.Identity;
        var registration = _registry.Find(identity);
        if (registration == null)
            return Refuse(identity, null, tenantId, "unknown_service", StatusCodes.Status403Forbidden, "unknown_service");

        if (bodyMalformed || string.IsNullOrWhiteSpace(tenantId))
            return Refuse(identity, registration, tenantId, "tenant_required", StatusCodes.Status400BadRequest, "invalid_request");

        tenantId = tenantId.Trim();
        if (!WorkloadTokenOptions.TenantIdPattern.IsMatch(tenantId))
            return Refuse(identity, registration, tenantId, "invalid_tenant", StatusCodes.Status400BadRequest, "invalid_request");

        string accessToken;
        try
        {
            accessToken = await IssueAsync(registration.ClientId!, tenantId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _audit.ServiceUnavailable(identity, tenantId, "signing_unavailable:" + ex.GetType().Name);
            return ServiceTokenOutcome.Fail(StatusCodes.Status503ServiceUnavailable, "unavailable");
        }

        _audit.ServiceIssued(identity, registration.ClientId!, tenantId);
        return new ServiceTokenOutcome(StatusCodes.Status200OK, null, new ServiceTokenResponse
        {
            AccessToken = accessToken,
            ExpiresIn = (int)_options.TokenLifetime.TotalSeconds,
            TenantId = tenantId,
            ClientId = registration.ClientId!,
        });
    }

    /// <summary>
    /// A service token signed with token-service's key under the service issuer.
    /// Also how token-service obtains its own token for tenant-service.
    /// </summary>
    public async Task<string> IssueAsync(string clientId, string tenantId, CancellationToken ct)
    {
        var material = await _signing.GetAsync(ct);
        return new ChoTokenIssuer(_options.Issuer, _signingOptions.Audience, material.Credentials, _options.TokenLifetime)
            .IssueServiceToken(clientId, tenantId);
    }

    private ServiceTokenOutcome Refuse(
        ServiceIdentity? identity, ServiceClientRegistration? registration, string? tenantId, string reason, int status, string error)
    {
        _audit.ServiceRefused(identity, registration?.ClientId, tenantId, reason);
        return ServiceTokenOutcome.Fail(status, error);
    }
}

public sealed record ServiceTokenOutcome(int Status, string? Error, ServiceTokenResponse? Response)
{
    public static ServiceTokenOutcome Fail(int status, string error) => new(status, error, null);
}

public sealed class ServiceTokenResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; init; } = string.Empty;
    [JsonPropertyName("token_type")] public string TokenType { get; init; } = "Bearer";
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
    [JsonPropertyName("tenant_id")] public string TenantId { get; init; } = string.Empty;
    [JsonPropertyName("client_id")] public string ClientId { get; init; } = string.Empty;
}

internal sealed record ServiceTokenRequest(string? TenantId);
