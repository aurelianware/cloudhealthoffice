using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.TokenService.Exchange;
using CloudHealthOffice.TokenService.Signing;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CloudHealthOffice.TokenService.Workload;

/// <summary>
/// <c>POST /v1/token/workload</c>: exchanges a pod's projected Kubernetes
/// service-account token for a short-lived CHO workload token.
///
/// The caller names only the tenant. Its identity (namespace and service
/// account) comes from the validated Kubernetes token, and its client id,
/// allowed tenants and permissions come from configuration. The issued token
/// is not a service token: role <c>cho.workload</c>, which grants nothing, plus
/// exactly the registered permissions.
/// </summary>
public static class WorkloadTokenEndpoint
{
    public const string Path = "/v1/token/workload";

    public static IServiceCollection AddWorkloadTokens(
        this IServiceCollection services, WorkloadTokenOptions options, IHostEnvironment environment)
    {
        services.AddSingleton(options);
        if (!options.Enabled)
            return services;

        services.TryAddSingleton<IKubernetesSigningKeys>(_ =>
        {
            var jwks = options.StaticJwks();
            return jwks != null
                ? new StaticKubernetesSigningKeys(jwks)
                : new DiscoveryKubernetesSigningKeys(options, new HttpClient { Timeout = TimeSpan.FromSeconds(10) },
                    requireHttps: !environment.IsDevelopment());
        });
        services.TryAddSingleton<IKubernetesTokenValidator>(sp =>
            new KubernetesTokenValidator(options, sp.GetRequiredService<IKubernetesSigningKeys>()));
        services.AddSingleton(new WorkloadRegistry(options.AllRegistrations()));
        services.AddSingleton<WorkloadTokenService>();
        return services;
    }

    public static IEndpointRouteBuilder MapWorkloadTokens(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetRequiredService<WorkloadTokenOptions>();
        if (!options.Enabled)
            return app;

        app.MapPost(Path, async (HttpContext http, WorkloadTokenService service) =>
        {
            var header = http.Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;

            string? tenantId = null;
            var bodyMalformed = false;
            try
            {
                var body = await http.Request.ReadFromJsonAsync<WorkloadTokenRequest>(http.RequestAborted);
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
        // Authenticated here by the Kubernetes token, not by Entra.
        .AllowAnonymous();

        return app;
    }
}

/// <summary>Registrations by Kubernetes identity.</summary>
public sealed class WorkloadRegistry
{
    private readonly Dictionary<string, WorkloadRegistration> _byAccount;

    public WorkloadRegistry(IEnumerable<WorkloadRegistration> registrations)
        => _byAccount = registrations.ToDictionary(r => Key(r.Namespace!, r.ServiceAccount!), StringComparer.Ordinal);

    public WorkloadRegistration? Find(KubernetesIdentity identity)
        => _byAccount.GetValueOrDefault(Key(identity.Namespace, identity.ServiceAccount));

    private static string Key(string ns, string sa) => ns + "/" + sa;
}

public sealed class WorkloadTokenService
{
    private readonly IKubernetesTokenValidator _validator;
    private readonly WorkloadRegistry _registry;
    private readonly WorkloadTokenOptions _options;
    private readonly ISigningMaterialSource _signing;
    private readonly TokenSigningOptions _signingOptions;
    private readonly TokenAudit _audit;

    public WorkloadTokenService(
        IKubernetesTokenValidator validator,
        WorkloadRegistry registry,
        WorkloadTokenOptions options,
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

    public async Task<WorkloadTokenOutcome> IssueAsync(string? token, string? tenantId, bool bodyMalformed, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token))
            return Refuse(null, null, tenantId, "missing_token", StatusCodes.Status401Unauthorized, "invalid_token");

        var validation = await _validator.ValidateAsync(token, ct);
        if (validation.Identity == null)
        {
            if (validation.Reason == "issuer_keys_unavailable")
            {
                _audit.WorkloadUnavailable(null, tenantId, validation.Reason);
                return WorkloadTokenOutcome.Fail(StatusCodes.Status503ServiceUnavailable, "unavailable");
            }
            return Refuse(null, null, tenantId, validation.Reason ?? "invalid_token", StatusCodes.Status401Unauthorized, "invalid_token");
        }

        var identity = validation.Identity;
        var registration = _registry.Find(identity);
        if (registration == null)
            return Refuse(identity, null, tenantId, "unknown_workload", StatusCodes.Status403Forbidden, "unknown_workload");

        if (bodyMalformed || string.IsNullOrWhiteSpace(tenantId))
            return Refuse(identity, registration, tenantId, "tenant_required", StatusCodes.Status400BadRequest, "invalid_request");

        tenantId = tenantId.Trim();
        if (!WorkloadTokenOptions.TenantIdPattern.IsMatch(tenantId)
            || WorkloadTokenOptions.ReservedTenants.Contains(tenantId)
            || !registration.AllowsTenant(tenantId))
            return Refuse(identity, registration, tenantId, "tenant_not_allowed", StatusCodes.Status403Forbidden, "tenant_not_allowed");

        var permissions = registration.Permissions.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();
        string accessToken;
        try
        {
            var material = await _signing.GetAsync(ct);
            accessToken = new ChoTokenIssuer(_options.Issuer, _signingOptions.Audience, material.Credentials, _options.TokenLifetime)
                .IssueWorkloadToken(registration.ClientId!, tenantId, permissions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _audit.WorkloadUnavailable(identity, tenantId, "signing_unavailable:" + ex.GetType().Name);
            return WorkloadTokenOutcome.Fail(StatusCodes.Status503ServiceUnavailable, "unavailable");
        }

        _audit.WorkloadIssued(identity, registration.ClientId!, tenantId, permissions);
        return new WorkloadTokenOutcome(StatusCodes.Status200OK, null, new WorkloadTokenResponse
        {
            AccessToken = accessToken,
            ExpiresIn = (int)_options.TokenLifetime.TotalSeconds,
            TenantId = tenantId,
            ClientId = registration.ClientId!,
            Permissions = permissions,
        });
    }

    private WorkloadTokenOutcome Refuse(
        KubernetesIdentity? identity, WorkloadRegistration? registration, string? tenantId, string reason, int status, string error)
    {
        _audit.WorkloadRefused(identity, registration?.ClientId, tenantId, reason);
        return WorkloadTokenOutcome.Fail(status, error);
    }
}

public sealed record WorkloadTokenOutcome(int Status, string? Error, WorkloadTokenResponse? Response)
{
    public static WorkloadTokenOutcome Fail(int status, string error) => new(status, error, null);
}

public sealed class WorkloadTokenResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; init; } = string.Empty;
    [JsonPropertyName("token_type")] public string TokenType { get; init; } = "Bearer";
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
    [JsonPropertyName("tenant_id")] public string TenantId { get; init; } = string.Empty;
    [JsonPropertyName("client_id")] public string ClientId { get; init; } = string.Empty;
    [JsonPropertyName("permissions")] public IReadOnlyList<string> Permissions { get; init; } = [];
}

internal sealed record WorkloadTokenRequest(string? TenantId);
