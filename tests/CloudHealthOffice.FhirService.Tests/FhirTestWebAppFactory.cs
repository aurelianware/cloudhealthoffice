using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using CloudHealthOffice.PriorAuthRuleEngine.Abstractions;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.ProviderEnrollmentService.Abstractions;
using CloudHealthOffice.ProviderEnrollmentService.Gates;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
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
            // ── Enrollment gate + rule engine stubs ─────────────────────
            // PasAutoAdjudicator now requires IEnrollmentDecisionGate and
            // IPriorAuthRuleEngine. Tests exercise the FHIR/PAS layer, not
            // the rule engine — provide no-op implementations.
            services.AddSingleton<IEnrollmentDecisionGate, PassthroughEnrollmentGate>();
            services.AddSingleton<IPriorAuthRuleEngine, NoOpPriorAuthRuleEngine>();

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

    private sealed class NoOpPriorAuthRuleEngine : IPriorAuthRuleEngine
    {
        public Task<PaRuleDecision> EvaluateAsync(
            PaRuleContext context, CancellationToken ct = default)
            => Task.FromResult(new PaRuleDecision
            {
                Outcome            = PaDecisionOutcome.Pend,
                FiringRuleId       = "NoOp",
                FiringRuleName     = "NoOp",
                ResolvedRuleSetKey = "test"
            });

        public Task<IReadOnlyList<PaRuleDocument>> GetApplicableRulesAsync(
            RuleSetKey key, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PaRuleDocument>>([]);
    }
}
