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

            // Capability 5.11 — the EOB controller is now a thin proxy over
            // claims-service. The legacy ContentNegotiation tests covered
            // controller routing / response shape through the old mock-data
            // path; preserve that coverage by stubbing the typed
            // ClaimsService HttpClient with a fake handler that returns the
            // canned Bundle the old MockFhirDataAdapter used to produce.
            services.AddHttpClient(global::FhirService.Controllers.ExplanationOfBenefitController.ClaimsServiceClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new FakeClaimsServiceHandler());
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

    /// <summary>
    /// Minimal canned-response stand-in for claims-service used by the
    /// fhir-service ExplanationOfBenefit proxy. Mirrors the legacy
    /// <c>MockFhirDataAdapter.Eobs</c> seed so existing fhir-service
    /// integration tests (ContentNegotiation, etc.) keep passing after
    /// capability 5.11 migrated the EOB controller to a proxy.
    /// </summary>
    private sealed class FakeClaimsServiceHandler : HttpMessageHandler
    {
        // Minimal EOB shape that satisfies all FHIR cardinality-1 fields
        // (status, type, use, patient, insurance). The Hl7.Fhir Bundle
        // deserializer rejects entries missing any of these, which the
        // tests rely on for round-trip parsing.
        private static string FakeEob(string id, string patientId) =>
            "{" +
              "\"resourceType\":\"ExplanationOfBenefit\"," +
              $"\"id\":\"{id}\"," +
              "\"status\":\"active\"," +
              "\"use\":\"claim\"," +
              "\"type\":{\"coding\":[{\"system\":\"http://terminology.hl7.org/CodeSystem/claim-type\",\"code\":\"professional\"}]}," +
              $"\"patient\":{{\"reference\":\"Patient/{patientId}\"}}," +
              "\"insurer\":{\"display\":\"CloudHealthOffice\"}," +
              "\"provider\":{\"display\":\"Test Provider\"}," +
              "\"created\":\"2026-01-15T00:00:00Z\"," +
              "\"outcome\":\"complete\"," +
              "\"insurance\":[{\"focal\":true,\"coverage\":{\"display\":\"Test Coverage\"}}]" +
            "}";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            const string searchPath = "/fhir/ExplanationOfBenefit";
            const string readPrefix = "/fhir/ExplanationOfBenefit/";

            if (path.StartsWith(readPrefix, StringComparison.Ordinal))
            {
                var id = path[readPrefix.Length..];
                var patient = id switch
                {
                    "eob-001" or "eob-002" => "pat-001",
                    "eob-003" => "pat-002",
                    _ => null,
                };
                if (patient is not null)
                {
                    return Task.FromResult(JsonResponse(System.Net.HttpStatusCode.OK,
                        FakeEob(id, patient)));
                }
                return Task.FromResult(JsonResponse(System.Net.HttpStatusCode.NotFound,
                    "{\"resourceType\":\"OperationOutcome\",\"issue\":[{\"severity\":\"error\",\"code\":\"not-found\"}]}"));
            }

            if (path == searchPath)
            {
                var query = request.RequestUri.Query;
                var (entries, total) =
                    query.Contains("patient=pat-001", StringComparison.Ordinal)
                        ? (
                            "{\"resource\":" + FakeEob("eob-001", "pat-001") + "}," +
                            "{\"resource\":" + FakeEob("eob-002", "pat-001") + "}",
                            2)
                    : query.Contains("patient=pat-002", StringComparison.Ordinal)
                        ? (
                            "{\"resource\":" + FakeEob("eob-003", "pat-002") + "}",
                            1)
                    : (string.Empty, 0);

                var body = $"{{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"total\":{total},\"entry\":[{entries}]}}";
                return Task.FromResult(JsonResponse(System.Net.HttpStatusCode.OK, body));
            }

            return Task.FromResult(JsonResponse(System.Net.HttpStatusCode.NotFound,
                "{\"resourceType\":\"OperationOutcome\"}"));
        }

        private static HttpResponseMessage JsonResponse(System.Net.HttpStatusCode status, string body) =>
            new(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/fhir+json"),
            };
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
