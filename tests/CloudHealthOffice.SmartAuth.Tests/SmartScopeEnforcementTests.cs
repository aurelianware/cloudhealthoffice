using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;

namespace CloudHealthOffice.SmartAuth.Tests;

/// <summary>
/// Tests SMART scope enforcement in the FHIR service.
/// Uses a test RSA key to issue self-signed JWTs so the tests run without a live
/// smart-auth-service.  The FHIR service is configured in-process via
/// WebApplicationFactory with the test key substituted for OIDC discovery.
/// </summary>
public class SmartScopeEnforcementTests : IClassFixture<FhirServiceFactory>
{
    private static readonly JsonSerializerOptions FhirOptions =
        new JsonSerializerOptions().ForFhir(typeof(Patient).Assembly);

    private readonly FhirServiceFactory _factory;

    public SmartScopeEnforcementTests(FhirServiceFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateClient() => _factory.CreateClient();

    // ── metadata is always public ─────────────────────────────────────────────

    [Fact]
    public async Task Metadata_NoToken_Returns200()
    {
        var client = CreateClient();
        var resp = await client.GetAsync("/fhir/r4/metadata");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SmartConfiguration_NoToken_Returns200()
    {
        var client = CreateClient();
        var resp = await client.GetAsync("/fhir/r4/.well-known/smart-configuration");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("authorization_endpoint").GetString()
            .Should().Contain("/connect/authorize");
        doc.RootElement.GetProperty("capabilities").EnumerateArray()
            .Select(e => e.GetString())
            .Should().Contain("launch-ehr", "launch-standalone", "permission-patient");
    }

    // ── unauthenticated ───────────────────────────────────────────────────────

    [Fact]
    public async Task PatientRead_NoToken_Returns401WithOperationOutcome()
    {
        var client = CreateClient();
        var resp = await client.GetAsync("/fhir/r4/Patient/pat-001");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await resp.Content.ReadAsStringAsync();
        var outcome = JsonSerializer.Deserialize<OperationOutcome>(body, FhirOptions);
        outcome!.TypeName.Should().Be("OperationOutcome");
    }

    // ── wrong scope ───────────────────────────────────────────────────────────

    [Fact]
    public async Task PatientRead_WrongScope_Returns403()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer",
                _factory.IssueToken("test-user", "patient/Coverage.read", patientId: "pat-001"));

        // Token has Coverage scope but NOT Patient.read
        var resp = await client.GetAsync("/fhir/r4/Patient/pat-001");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var body = await resp.Content.ReadAsStringAsync();
        var outcome = JsonSerializer.Deserialize<OperationOutcome>(body, FhirOptions);
        outcome!.Issue.Should().ContainSingle(i => i.Code == OperationOutcome.IssueType.Forbidden);
    }

    // ── patient wildcard scope ────────────────────────────────────────────────

    [Fact]
    public async Task PatientRead_WildcardScope_BoundPatient_Returns200()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer",
                _factory.IssueToken("pat-001", "patient/*.read", patientId: "pat-001"));

        var resp = await client.GetAsync("/fhir/r4/Patient/pat-001");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadAsStringAsync();
        var patient = JsonSerializer.Deserialize<Patient>(body, FhirOptions);
        patient!.Id.Should().Be("pat-001");
    }

    // ── patient binding violation ─────────────────────────────────────────────

    [Fact]
    public async Task PatientRead_WildcardScope_DifferentPatient_Returns403()
    {
        // Token bound to pat-001 — try to read pat-002
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer",
                _factory.IssueToken("pat-001", "patient/*.read", patientId: "pat-001"));

        var resp = await client.GetAsync("/fhir/r4/Patient/pat-002");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var body = await resp.Content.ReadAsStringAsync();
        var outcome = JsonSerializer.Deserialize<OperationOutcome>(body, FhirOptions);
        outcome!.Issue.Should().ContainSingle(i => i.Diagnostics!.Contains("pat-001"));
    }

    // ── EOB patient-scoped search ─────────────────────────────────────────────

    [Fact]
    public async Task EobSearch_PatientToken_NoPatientParam_AutoInjects()
    {
        // Patient token for pat-001 — no patient param in query
        // Middleware injects it from the token → returns pat-001's EOBs
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer",
                _factory.IssueToken("pat-001", "patient/ExplanationOfBenefit.read",
                    patientId: "pat-001"));

        var resp = await client.GetAsync("/fhir/r4/ExplanationOfBenefit");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadAsStringAsync();
        var bundle = JsonSerializer.Deserialize<Bundle>(body, FhirOptions);
        bundle!.Total.Should().Be(2); // eob-001 and eob-002 for pat-001
    }

    [Fact]
    public async Task EobSearch_PatientToken_MismatchedPatientParam_Returns403()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer",
                _factory.IssueToken("pat-001", "patient/ExplanationOfBenefit.read",
                    patientId: "pat-001"));

        // Explicit patient param for different patient → violation
        var resp = await client.GetAsync("/fhir/r4/ExplanationOfBenefit?patient=pat-002");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── user-scoped token (provider access) ──────────────────────────────────

    [Fact]
    public async Task EobSearch_UserToken_CanSearchAnyPatient()
    {
        // user/*.read — provider can see any patient's EOBs (filtered by their access)
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer",
                _factory.IssueToken("provider-001", "user/*.read"));

        var resp = await client.GetAsync("/fhir/r4/ExplanationOfBenefit?patient=pat-001");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── system-scoped token (payer-to-payer) ─────────────────────────────────

    [Fact]
    public async Task PatientSearch_SystemToken_NoPatientBinding()
    {
        // system/*.read — no patient binding, returns all patients
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer",
                _factory.IssueToken("cho-payer-system", "system/*.read"));

        var resp = await client.GetAsync("/fhir/r4/Patient");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadAsStringAsync();
        var bundle = JsonSerializer.Deserialize<Bundle>(body, FhirOptions);
        bundle!.Total.Should().Be(3); // all patients
    }

    // ── specific resource scopes ──────────────────────────────────────────────

    [Fact]
    public async Task Coverage_PatientToken_EobScopeOnly_Returns403OnCoverage()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer",
                _factory.IssueToken("pat-001", "patient/ExplanationOfBenefit.read",
                    patientId: "pat-001"));

        var resp = await client.GetAsync("/fhir/r4/Coverage/cov-001");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Coverage_PatientToken_CoverageScopeIncluded_Returns200()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer",
                _factory.IssueToken("pat-001",
                    "patient/ExplanationOfBenefit.read patient/Coverage.read",
                    patientId: "pat-001"));

        var resp = await client.GetAsync("/fhir/r4/Coverage/cov-001");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

/// <summary>
/// WebApplicationFactory that overrides JWT validation to use a local test RSA key,
/// bypassing OIDC discovery so tests run without a live smart-auth-service.
/// </summary>
public class FhirServiceFactory : WebApplicationFactory<Program>
{
    private readonly RsaSecurityKey _signingKey;
    private readonly string _issuer = "https://auth.test.local";

    public FhirServiceFactory()
    {
        var rsa = RSA.Create(2048);
        _signingKey = new RsaSecurityKey(rsa);
    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Override JWT Bearer validation parameters via PostConfigure
            // instead of removing and re-adding the auth scheme (which causes
            // "Scheme already exists: Bearer" when the host registers it first).
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
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
            // claims-service. The legacy SMART tests covered routing /
            // scope-enforcement / patient-binding behavior through the old
            // mock-data path; preserve that coverage by stubbing the typed
            // ClaimsService HttpClient with a fake handler that returns the
            // canned Bundle the old MockFhirDataAdapter used to produce.
            services.AddHttpClient(global::FhirService.Controllers.ExplanationOfBenefitController.ClaimsServiceClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new FakeClaimsServiceHandler());
        });

        builder.UseEnvironment("Development");
    }

    /// <summary>Issues a test JWT signed with the factory's test key.</summary>
    public string IssueToken(
        string subject,
        string scopes,
        string? patientId = null,
        string? encounterId = null)
    {
        var claims = new List<SecurityClaim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new("scope", scopes),
            new(JwtRegisteredClaimNames.Aud, "fhir-api"),
            new("tenant_id", "test-tenant")
        };

        if (patientId != null)
            claims.Add(new SecurityClaim("patient", patientId));

        if (encounterId != null)
            claims.Add(new SecurityClaim("encounter", encounterId));

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
    /// <c>MockFhirDataAdapter.Eobs</c> seed: pat-001 owns eob-001 +
    /// eob-002, pat-002 owns eob-003. Read-by-id returns 200 for the
    /// known ids and 404 otherwise. Search returns the matching subset.
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
                    return Task.FromResult(JsonResponse(HttpStatusCode.OK,
                        FakeEob(id, patient)));
                }
                return Task.FromResult(JsonResponse(HttpStatusCode.NotFound,
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
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, body));
            }

            return Task.FromResult(JsonResponse(HttpStatusCode.NotFound,
                "{\"resourceType\":\"OperationOutcome\"}"));
        }

        private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body) =>
            new(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/fhir+json"),
            };
    }
}
