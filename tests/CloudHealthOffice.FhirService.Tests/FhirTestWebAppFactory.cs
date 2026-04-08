using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using CloudHealthOffice.PriorAuthRuleEngine.Abstractions;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.ProviderEnrollmentService.Abstractions;
using CloudHealthOffice.ProviderEnrollmentService.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Moq;
using SecurityClaim = System.Security.Claims.Claim;

namespace CloudHealthOffice.FhirService.Tests;

/// <summary>
/// WebApplicationFactory for fhir-service integration tests that overrides JWT Bearer
/// validation to use a local test RSA key, bypassing OIDC discovery so tests run
/// without a live smart-auth-service.
/// </summary>
public class FhirTestWebAppFactory : WebApplicationFactory<Program>
{
    // Keep the raw RSA so we can dispose it alongside the factory.
    private readonly RSA _rsa;
    private readonly RsaSecurityKey _signingKey;
    private readonly string _issuer = "https://auth.test.local";

    public FhirTestWebAppFactory()
    {
        _rsa = RSA.Create(2048);
        _signingKey = new RsaSecurityKey(_rsa);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // ── Mock IEnrollmentDecisionGate and IPriorAuthRuleEngine ─────
            // PasAutoAdjudicator's constructor now requires these two params.
            // In test environments without Redis/DB, the engine registrations
            // are skipped so we provide no-op mocks.
            var gateMock = new Mock<IEnrollmentDecisionGate>();
            gateMock.Setup(g => g.EvaluateAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<DateOnly>(), It.IsAny<LineOfBusiness>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(GateResult.Pass());
            services.TryAddSingleton(gateMock.Object);

            var ruleEngineMock = new Mock<IPriorAuthRuleEngine>();
            ruleEngineMock.Setup(r => r.EvaluateAsync(It.IsAny<PaRuleContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PaRuleDecision
                {
                    Outcome = PaDecisionOutcome.Pend,
                    FiringRuleId = "NoRuleMatch",
                    FiringRuleName = "NoRuleMatch",
                    ResolvedRuleSetKey = "test/TX/Medicaid/any"
                });
            services.TryAddSingleton(ruleEngineMock.Object);

            // Override JWT Bearer validation parameters via PostConfigure
            // instead of removing and re-adding the auth scheme (which causes
            // "Scheme already exists: Bearer" when the host registers it first).
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                // Disable OIDC discovery entirely so the test host never makes network
                // calls to the real issuer (https://auth.cloudhealthoffice.com).
                // Authority must be cleared before nulling ConfigurationManager because
                // the lazy getter recreates it from Authority if Authority is still set.
                options.Authority = null;
                options.MetadataAddress = null!;
                options.ConfigurationManager = null;

                options.RequireHttpsMetadata = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = _issuer,
                    ValidateAudience = true,
                    ValidAudience = "fhir-api",
                    ValidateLifetime = true,
                    IssuerSigningKey = _signingKey
                };
            });
        });

        builder.UseEnvironment("Development");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _rsa.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>
    /// Issues a test JWT signed with the factory's test RSA key.
    /// The token includes tenant_id and the requested SMART scopes.
    /// </summary>
    public string IssueToken(string scopes, string? patientId = null)
    {
        var claims = new List<SecurityClaim>
        {
            new(JwtRegisteredClaimNames.Sub, "test-user"),
            new("scope", scopes),
            new(JwtRegisteredClaimNames.Aud, "fhir-api"),
            new("tenant_id", "test-tenant")
        };

        if (patientId != null)
            claims.Add(new SecurityClaim("patient", patientId));

        var credentials = new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256);
        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: "fhir-api",
            claims: claims,
            notBefore: DateTime.UtcNow.AddSeconds(-5),
            expires: DateTime.UtcNow.AddMinutes(60),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
