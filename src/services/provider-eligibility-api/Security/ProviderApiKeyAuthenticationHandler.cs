using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ProviderEligibilityApi.Security;

/// <summary>
/// The external-caller scheme: provider applications (CloudDentalOffice) that
/// hold no CHO token authenticate with the API key issued to them in
/// <c>X-Api-Key</c>. It is a scheme of its own, never the CHO "Bearer" scheme.
///
/// The principal it produces carries the credential's bound tenant as its
/// <c>tenant_id</c> claim, so the shared tenant middleware takes the tenant
/// from the credential exactly as it does from a CHO token (a disagreeing
/// <c>X-Tenant-ID</c> is refused with 403). It carries no roles and no
/// permissions, so it can never satisfy a CHO permission; only the
/// <see cref="ProviderEligibilityAuth.CallerPolicy"/> admits it.
///
/// Fails closed: with no usable client configured every API-key request is
/// refused with 503, and a key shared by two clients authenticates neither.
/// </summary>
public sealed class ProviderApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ProviderApiKey";
    public const string ApiKeyHeader = "X-Api-Key";
    public const string ClientClaim = "provider_client";
    public const string SubjectPrefix = "provider-api:";

    private const string NotConfiguredItem = "ProviderApiKey.NotConfigured";

    private readonly IOptionsMonitor<ProviderApiOptions> _clients;

    public ProviderApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptionsMonitor<ProviderApiOptions> clients)
        : base(options, logger, encoder)
    {
        _clients = clients;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyHeader, out var values))
            return Task.FromResult(AuthenticateResult.NoResult());

        // One credential per request. A request that also carries a bearer
        // token states two identities; neither is chosen for it.
        if (!string.IsNullOrEmpty(Request.Headers.Authorization.ToString()))
        {
            Logger.LogWarning("Provider API request rejected: both an API key and an Authorization header were sent");
            return Task.FromResult(AuthenticateResult.Fail("Both an API key and a bearer token were presented."));
        }

        var clients = _clients.CurrentValue.Clients.Where(c => c.IsUsable).ToList();
        if (clients.Count == 0)
        {
            Logger.LogError("Provider API has no usable client credentials configured; refusing request");
            Context.Items[NotConfiguredItem] = true;
            return Task.FromResult(AuthenticateResult.Fail("No provider API clients are configured."));
        }

        var supplied = values.Count == 1 ? values[0] : null;
        var matches = clients.Where(c => FixedTimeEquals(supplied, c.ApiKey)).ToList();
        if (matches.Count != 1)
        {
            if (matches.Count > 1)
            {
                Logger.LogError(
                    "Provider API credential is configured for {Count} clients; it authenticates none of them",
                    matches.Count);
            }
            else
            {
                Logger.LogWarning("Provider API request rejected: missing or invalid credential");
            }
            return Task.FromResult(AuthenticateResult.Fail("Invalid provider API credential."));
        }

        var client = matches[0];
        var identity = new ClaimsIdentity(
            [
                new Claim(ChoClaimTypes.Subject, SubjectPrefix + client.Name.Trim()),
                new Claim(ChoClaimTypes.TenantId, client.TenantId.Trim()),
                new Claim(ClientClaim, client.Name.Trim()),
            ],
            SchemeName,
            ChoClaimTypes.Name,
            ChoClaimTypes.Role);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = Context.Items.ContainsKey(NotConfiguredItem)
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    private static bool FixedTimeEquals(string? supplied, string expected)
    {
        if (supplied is null) return false;
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return suppliedBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }
}
