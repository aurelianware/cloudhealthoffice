using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace IdCardService.Middleware;

/// <summary>
/// Development/test auth scheme: accepts any request and attaches a fixed
/// principal so <c>[Authorize(Policy = "ProviderJwt")]</c> passes when no
/// real IdP is configured. Production uses the real JwtBearer handler
/// wired via <c>ProviderJwt:Authority</c> — this handler is only registered
/// when that value is empty.
/// </summary>
public class DevProviderAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public DevProviderAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var providerId = Request.Headers["X-Provider-Id"].FirstOrDefault() ?? "dev-provider";
        var claims = new[]
        {
            new Claim("provider_id", providerId),
            new Claim("sub", providerId)
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
