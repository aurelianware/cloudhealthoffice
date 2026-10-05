using System.Text.Json;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.TokenService;
using CloudHealthOffice.TokenService.Directory;
using CloudHealthOffice.TokenService.Entra;
using CloudHealthOffice.TokenService.Exchange;
using CloudHealthOffice.TokenService.ServiceTokens;
using CloudHealthOffice.TokenService.Signing;
using CloudHealthOffice.TokenService.Workload;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);

// token-service does not use AddChoAuthentication; apply its Testing guard here.
ChoTestingEnvironment.EnsureNotDeployed(builder.Environment);

var serviceOptions = builder.Configuration.GetSection(TokenServiceOptions.SectionName).Get<TokenServiceOptions>()
                     ?? new TokenServiceOptions();
serviceOptions.Validate();

var signingOptions = builder.Configuration.GetSection(TokenSigningOptions.SectionName).Get<TokenSigningOptions>()
                     ?? new TokenSigningOptions();

builder.Services.AddSingleton(serviceOptions);
builder.Services.AddSingleton(signingOptions);
builder.Services.AddSingleton<ISigningMaterialSource>(sp =>
    TokenSigningSetup.Create(signingOptions, sp.GetRequiredService<IHostEnvironment>()));
builder.Services.AddSingleton<TokenAudit>();
builder.Services.AddScoped<TokenExchangeService>();

// Kubernetes workloads (Argo workflows): POST /v1/token/workload.
var workloadOptions = builder.Configuration.GetSection(WorkloadTokenOptions.SectionName).Get<WorkloadTokenOptions>()
                      ?? new WorkloadTokenOptions();
workloadOptions.Validate(signingOptions.Issuer, builder.Environment.IsDevelopment());
builder.Services.AddWorkloadTokens(workloadOptions, builder.Environment);

// CHO services, by Azure workload identity: POST /v1/token/service.
var serviceTokenOptions = builder.Configuration.GetSection(ServiceTokenOptions.SectionName).Get<ServiceTokenOptions>()
                          ?? new ServiceTokenOptions();
serviceTokenOptions.Validate(signingOptions.Issuer, workloadOptions.Issuer, builder.Environment.IsDevelopment());
builder.Services.AddServiceTokens(serviceTokenOptions, builder.Environment);

// Entra ID access tokens from the portal (on behalf of a signed-in user).
builder.Services.AddEntraUserTokenValidation(builder.Configuration, serviceOptions);
builder.Services.AddAuthorization(authz =>
{
    authz.FallbackPolicy = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .Build();
});

// tenant-service, authenticated as the token-service service identity.
// Deployed: signed by token-service itself under the service-token issuer
// (ServiceTokens:Issuer), with its Key Vault key, like every other service's
// token. Development/Testing may use a local ChoAuth:ServiceToken key instead;
// a local key is refused anywhere else.
builder.Services.AddSingleton(sp =>
{
    var st = builder.Configuration.GetSection("ChoAuth:ServiceToken").Get<ChoServiceTokenOptions>();
    var env = sp.GetRequiredService<IHostEnvironment>();
    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("CloudHealthOffice.TokenService");
    var clientId = string.IsNullOrWhiteSpace(st?.ClientId) ? "token-service" : st!.ClientId;
    if (st != null && (!string.IsNullOrWhiteSpace(st.PrivateKeyPem) || !string.IsNullOrWhiteSpace(st.SymmetricKey)))
    {
        if (!ChoTestingEnvironment.AllowsDevelopmentSecrets(env))
            throw new InvalidOperationException(
                "ChoAuth:ServiceToken holds a local signing key, which is permitted only on a Development or Testing host. " +
                "Remove it and enable ServiceTokens: token-service then signs its own service token with its Key Vault key.");
        return new ServiceTokenSource(
            ChoTokenIssuer.FromKeys(st.Issuer, ChoDevelopmentAuth.Audience, st.PrivateKeyPem, st.SymmetricKey, st.Lifetime),
            clientId);
    }
    if (serviceTokenOptions.Enabled)
    {
        var issuer = sp.GetRequiredService<ServiceTokenService>();
        return new ServiceTokenSource((tenant, ct) => new ValueTask<string>(issuer.IssueAsync(clientId, tenant, ct)), clientId);
    }
    // tenant-service refuses these calls; token-service then answers 503.
    log.LogWarning("Neither ServiceTokens nor a development ChoAuth:ServiceToken key is configured; " +
                   "calls to tenant-service carry no service token.");
    return new ServiceTokenSource((Func<string, CancellationToken, ValueTask<string>>?)null, clientId);
});
builder.Services.AddTransient<TenantServiceTokenHandler>();
builder.Services.AddHttpClient(HttpTenantDirectory.ClientName, client =>
    {
        var baseUrl = builder.Configuration["Services:TenantService"];
        if (!string.IsNullOrWhiteSpace(baseUrl))
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(10);
    })
    .AddHttpMessageHandler<TenantServiceTokenHandler>();
builder.Services.AddSingleton<ITenantDirectory, HttpTenantDirectory>();

builder.Services.AddChoHealthChecks();

// Invitation redemption attempts are limited per Entra identity.
builder.Services.AddInvitationRedemptionRateLimit(serviceOptions);
// Self-service signup attempts likewise (same per-minute limit).
builder.Services.AddSignupRateLimit(serviceOptions);

var app = builder.Build();

if (string.IsNullOrWhiteSpace(app.Configuration["Services:TenantService"]))
    throw new InvalidOperationException("Services:TenantService is required.");

// Probes answer before authentication, so the platform can reach them.
app.MapChoHealthChecks();

app.UseAuthentication();
app.UseAuthorization();
// After authorization: only validated Entra users reach the limiter, partitioned by tid+oid.
app.UseRateLimiter();

app.MapInvitationRedemption();
app.MapSignup();
app.MapWorkloadTokens();
app.MapServiceTokens();

app.MapPost("/v1/token/exchange", async (HttpContext http, TokenExchangeService exchange, TokenAudit audit) =>
{
    var user = EntraUser.From(http.User);

    string? tenantId = null;
    if (http.Request.ContentLength is > 0 || http.Request.Headers.TransferEncoding.Count > 0)
    {
        try
        {
            var body = await http.Request.ReadFromJsonAsync<ExchangeRequest>(http.RequestAborted);
            tenantId = body?.TenantId;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return Results.Json(new { error = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    try
    {
        var outcome = await exchange.ExchangeAsync(user, tenantId, http.RequestAborted);
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
        audit.Unavailable(user, tenantId, "tenant_service_unavailable");
        return Results.Json(new { error = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (SigningUnavailableException ex)
    {
        audit.Unavailable(user, tenantId, "signing_unavailable:" + ex.InnerException?.GetType().Name);
        return Results.Json(new { error = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/v1/token/tenants", async (HttpContext http, TokenExchangeService exchange, TokenAudit audit) =>
{
    var user = EntraUser.From(http.User);
    try
    {
        return Results.Json(await exchange.GetAccessibleTenantsAsync(user, http.RequestAborted));
    }
    catch (TenantDirectoryUnavailableException)
    {
        audit.Unavailable(user, null, "tenant_service_unavailable");
        return Results.Json(new { error = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

// The public verification key, so services and operators can pin it
// (ChoAuth:Issuers:n:PublicKeyPem). Public material only; empty for a
// development symmetric key.
app.MapGet("/.well-known/jwks.json", async (ISigningMaterialSource signing, CancellationToken ct) =>
{
    var material = await signing.GetAsync(ct);
    var keys = material.PublicJwk == null
        ? Array.Empty<object>()
        : new object[]
        {
            new
            {
                kty = material.PublicJwk.Kty,
                use = material.PublicJwk.Use,
                alg = material.PublicJwk.Alg,
                kid = material.PublicJwk.KeyId,
                n = material.PublicJwk.N,
                e = material.PublicJwk.E,
                crv = material.PublicJwk.Crv,
                x = material.PublicJwk.X,
                y = material.PublicJwk.Y,
            },
        };
    return Results.Json(new { keys }, new JsonSerializerOptions
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    });
}).AllowAnonymous();

app.Run();

internal sealed record ExchangeRequest(string? TenantId);

public partial class Program;
