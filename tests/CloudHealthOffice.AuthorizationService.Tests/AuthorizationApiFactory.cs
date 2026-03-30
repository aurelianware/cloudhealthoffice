using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using AuthorizationService.Repositories;
using SecurityClaim = System.Security.Claims.Claim;

namespace CloudHealthOffice.AuthorizationService.Tests;

/// <summary>
/// WebApplicationFactory for authorization-service smoke tests.
/// Overrides JWT Bearer auth with a test RSA key and replaces the
/// repository with an NSubstitute mock.
/// </summary>
public class AuthorizationApiFactory : WebApplicationFactory<Program>
{
    private readonly RSA _rsa;
    private readonly RsaSecurityKey _signingKey;
    private readonly string _issuer = "https://auth.test.local";

    public IAuthorizationRepository AuthorizationRepository { get; } = Substitute.For<IAuthorizationRepository>();

    public AuthorizationApiFactory()
    {
        _rsa = RSA.Create(2048);
        _signingKey = new RsaSecurityKey(_rsa);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            // Remove real repository registrations
            var descriptorsToRemove = services
                .Where(d => d.ServiceType == typeof(IAuthorizationRepository))
                .ToList();

            foreach (var descriptor in descriptorsToRemove)
                services.Remove(descriptor);

            // Remove Cosmos/Mongo registrations that would fail without config
            var infraDescriptors = services
                .Where(d => d.ServiceType.FullName?.Contains("Cosmos") == true
                         || d.ServiceType.FullName?.Contains("Mongo") == true)
                .ToList();

            foreach (var descriptor in infraDescriptors)
                services.Remove(descriptor);

            services.AddSingleton(AuthorizationRepository);

            // Override JWT Bearer to use test RSA key
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Authority = null;
                options.MetadataAddress = null!;
                options.ConfigurationManager = null;
                options.RequireHttpsMetadata = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = _issuer,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    IssuerSigningKey = _signingKey
                };
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _rsa.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>
    /// Issues a test JWT with the specified scopes and tenant.
    /// </summary>
    public string IssueToken(string tenantId = "test-tenant", string role = "AuthorizationManager")
    {
        var claims = new List<SecurityClaim>
        {
            new(JwtRegisteredClaimNames.Sub, "test-user"),
            new("tenant_id", tenantId),
            new(ClaimTypes.Role, role),
            new("scope", "authorizations.read authorizations.write")
        };

        var credentials = new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256);
        var token = new JwtSecurityToken(
            issuer: _issuer,
            claims: claims,
            notBefore: DateTime.UtcNow.AddSeconds(-5),
            expires: DateTime.UtcNow.AddMinutes(60),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
