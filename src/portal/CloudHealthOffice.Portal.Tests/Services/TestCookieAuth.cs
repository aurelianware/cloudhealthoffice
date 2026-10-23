using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static CloudHealthOffice.Portal.Tests.Services.ChoTokenTestSupport;

namespace CloudHealthOffice.Portal.Tests.Services;

/// <summary>Stands in for the portal's cookie: authenticated when the test header names a user.</summary>
public sealed class TestCookieAuth : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "TestCookie";
    public const string UserHeader = "X-Test-User";

    public TestCookieAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var oid) || string.IsNullOrEmpty(oid))
            return Task.FromResult(AuthenticateResult.NoResult());

        var principal = EntraUser(tid: "entra-tid-1", oid: oid.ToString());
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
