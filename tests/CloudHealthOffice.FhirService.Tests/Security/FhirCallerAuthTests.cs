using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CloudHealthOffice.Appeals.Contracts;
using CloudHealthOffice.Infrastructure.Security;
using FhirService.Services;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using SecurityClaim = System.Security.Claims.Claim;

namespace CloudHealthOffice.FhirService.Tests.Security;

/// <summary>
/// fhir-service serves SMART callers (external FHIR clients) and CHO callers
/// (portal, CHO services) side by side. These tests pin the boundary between
/// them: which caller each endpoint admits, that neither token can satisfy the
/// other's checks, where the tenant comes from, and that a SMART token is never
/// sent on to a CHO service.
/// </summary>
public class FhirCallerAuthTests : IClassFixture<FhirTestWebAppFactory>
{
    private const string Tenant = "test-tenant";

    private static readonly JsonSerializerOptions FhirJson =
        new JsonSerializerOptions().ForFhir(typeof(Claim).Assembly);

    private readonly FhirTestWebAppFactory _factory;

    public FhirCallerAuthTests(FhirTestWebAppFactory factory) => _factory = factory;

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>A host whose claims-service client records every outbound call.</summary>
    private (WebApplicationFactory<Program> Host, RecordingClaimsService Claims) WithRecordingClaimsService()
    {
        var recorder = new RecordingClaimsService();
        var host = _factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
            services.AddHttpClient(UpstreamClientNames.ClaimsService)
                .ConfigurePrimaryHttpMessageHandler(() => recorder)));
        return (host, recorder);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        WebApplicationFactory<Program> host, HttpMethod method, string path, string? bearer,
        string? tenantHeader = null, HttpContent? content = null)
    {
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (bearer != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (tenantHeader != null) request.Headers.Add("X-Tenant-ID", tenantHeader);
        return await client.SendAsync(request);
    }

    private Task<HttpResponseMessage> GetAsync(string path, string? bearer, string? tenantHeader = null)
        => SendAsync(_factory, HttpMethod.Get, path, bearer, tenantHeader);

    private static string ChoUser(string tenant, params string[] roles) => ChoDevelopmentAuth.UserToken(tenant, roles);

    private string PatientToken(string patientId, string scopes = "patient/*.read")
        => _factory.IssueToken(scopes, patientId);

    // ── No token ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/fhir/r4/Patient/pat-001")]
    [InlineData("/fhir/r4/ExplanationOfBenefit/eob-001")]
    [InlineData("/api/v1/crd/code-classification")]
    public async Task NoToken_Is401(string path)
        => (await GetAsync(path, bearer: null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

    // ── CHO callers ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/fhir/r4/Patient/pat-001", ChoRolePermissions.ProviderRelations)]     // members:read
    [InlineData("/fhir/r4/Coverage/cov-001", ChoRolePermissions.Finance)]              // coverage:read
    [InlineData("/fhir/r4/ExplanationOfBenefit/eob-001", ChoRolePermissions.ProviderRelations)] // claims:read
    [InlineData("/fhir/r4/Practitioner/1234567890", ChoRolePermissions.Finance)]       // providers:read
    [InlineData("/api/v1/crd/code-classification", ChoRolePermissions.Finance)]        // authorizations:read
    public async Task ChoToken_WithoutThePermission_Is403(string path, string role)
        => (await GetAsync(path, ChoUser(Tenant, role))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task ChoToken_WithThePermission_Is200()
    {
        var response = await GetAsync("/fhir/r4/Patient/pat-001", ChoUser(Tenant, ChoRolePermissions.MemberServices));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ChoCaller_TenantComesFromTheToken_AndTheTokenIsForwarded()
    {
        var (host, claims) = WithRecordingClaimsService();
        var token = ChoUser("tenant-a", ChoRolePermissions.MemberServices);

        var response = await SendAsync(host, HttpMethod.Get, "/fhir/r4/ExplanationOfBenefit/eob-001", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var call = claims.Calls.Should().ContainSingle().Subject;
        call.TenantHeader.Should().Be("tenant-a", "the tenant comes from the CHO token");
        call.Authorization.Should().Be($"Bearer {token}", "a CHO caller's own token is forwarded");
    }

    [Fact]
    public async Task ChoCaller_HeaderNamingAnotherTenant_Is403()
    {
        var response = await GetAsync("/fhir/r4/Patient/pat-001",
            ChoUser("tenant-a", ChoRolePermissions.MemberServices), tenantHeader: "tenant-b");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ChoToken_IsNotPatientBound_ItsPermissionGovernsIt()
    {
        // cov-002 belongs to pat-002. A CHO caller with coverage:read reads it;
        // there is no SMART patient binding on a CHO token.
        var response = await GetAsync("/fhir/r4/Coverage/cov-002", ChoUser(Tenant, ChoRolePermissions.MemberServices));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ChoToken_CarryingSmartScopes_GainsNothingFromThem()
    {
        // A CHO-issued token that also carries SMART scope and patient claims is
        // still a CHO token: without members:read it cannot read a Patient.
        var token = CraftChoToken(Tenant, ChoRolePermissions.ProviderRelations,
            new SecurityClaim("scope", "patient/*.read user/*.read system/*.read"),
            new SecurityClaim("patient", "pat-001"));

        (await GetAsync("/fhir/r4/Patient/pat-001", token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SmartToken_CarryingChoRoles_CannotUseCHOEndpoints()
    {
        // A SMART token whose issuer wrote "roles: TenantAdmin" is still a SMART
        // token; CHO-only endpoints refuse it.
        var token = _factory.IssueToken("system/*.read",
            extraClaims: [new SecurityClaim("roles", ChoRolePermissions.TenantAdmin)]);

        (await GetAsync("/api/v1/crd/code-classification", token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── SMART callers: tenant ─────────────────────────────────────────────────

    [Fact]
    public async Task SmartToken_WithoutATenant_CannotChooseOneByHeader()
    {
        // Before: a SMART token with no tenant claim took the tenant from
        // X-Tenant-ID, so the caller could pick any tenant.
        var token = _factory.IssueToken("patient/*.read", "pat-001", tenantId: null);

        var response = await GetAsync("/fhir/r4/Patient/pat-001", token, tenantHeader: Tenant);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── SMART callers: patient binding ────────────────────────────────────────

    [Fact]
    public async Task SmartPatientToken_ReadsItsOwnPatient()
        => (await GetAsync("/fhir/r4/Patient/pat-001", PatientToken("pat-001"))).StatusCode
            .Should().Be(HttpStatusCode.OK);

    [Fact]
    public async Task SmartPatientToken_CannotReadAnotherPatient()
        => (await GetAsync("/fhir/r4/Patient/pat-002", PatientToken("pat-001"))).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

    [Theory]
    [InlineData("/fhir/r4/Coverage/cov-002")]
    [InlineData("/fhir/r4/Claim/clm-003")]
    [InlineData("/fhir/r4/Encounter/enc-003")]
    public async Task SmartPatientToken_CannotReadAnotherPatientsResourceById(string path)
        => (await GetAsync(path, PatientToken("pat-001"))).StatusCode.Should().Be(HttpStatusCode.NotFound);

    [Theory]
    [InlineData("/fhir/r4/Coverage", "Coverage")]
    [InlineData("/fhir/r4/Claim", "Claim")]
    [InlineData("/fhir/r4/Encounter", "Encounter")]
    [InlineData("/fhir/r4/Patient?gender=female", "Patient")]
    [InlineData("/fhir/r4/Patient", "Patient")]
    public async Task SmartPatientToken_SearchWithoutAPatientParameter_SeesOnlyItsOwnRecords(string path, string type)
    {
        var response = await GetAsync(path, PatientToken("pat-001"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var bundle = JsonSerializer.Deserialize<Bundle>(await response.Content.ReadAsStringAsync(), FhirJson)!;
        foreach (var entry in bundle.Entry)
        {
            entry.Resource.TypeName.Should().Be(type);
            MemberOf(entry.Resource).Should().Be("pat-001");
        }
    }

    [Fact]
    public async Task SmartPatientToken_CannotSearchCoverageByAnotherBeneficiary()
        => (await GetAsync("/fhir/r4/Coverage?beneficiary=Patient/pat-002", PatientToken("pat-001"))).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task SmartPatientScopedToken_WithoutAPatientClaim_Is403()
        => (await GetAsync("/fhir/r4/Coverage", _factory.IssueToken("patient/*.read"))).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task SmartPatientToken_CannotStartABulkExport()
    {
        var content = new StringContent(string.Empty);
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/fhir/r4/$export") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", PatientToken("pat-001"));
        request.Headers.Add("Prefer", "respond-async");

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── SMART callers through the claims-service proxy ────────────────────────

    [Fact]
    public async Task SmartPatientToken_ReadsItsOwnEobThroughTheProxy()
    {
        var (host, _) = WithRecordingClaimsService();
        var response = await SendAsync(host, HttpMethod.Get, "/fhir/r4/ExplanationOfBenefit/eob-001", PatientToken("pat-001"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SmartPatientToken_CannotReadAnotherPatientsEobThroughTheProxy()
    {
        // claims-service answers for the tenant; eob-003 is pat-002's.
        var (host, _) = WithRecordingClaimsService();
        var response = await SendAsync(host, HttpMethod.Get, "/fhir/r4/ExplanationOfBenefit/eob-003", PatientToken("pat-001"));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("pat-002");
    }

    [Fact]
    public async Task SmartPatientToken_EobSearchIsPinnedToItsPatient()
    {
        var (host, claims) = WithRecordingClaimsService();
        await SendAsync(host, HttpMethod.Get, "/fhir/r4/ExplanationOfBenefit?_id=eob-003", PatientToken("pat-001"));
        claims.Calls.Should().ContainSingle().Which.Query.Should().Contain("patient=pat-001");
    }

    [Fact]
    public async Task SmartToken_IsNeverForwarded_AServiceTokenForTheTenantIsUsed()
    {
        var (host, claims) = WithRecordingClaimsService();
        var smartToken = PatientToken("pat-001");

        var response = await SendAsync(host, HttpMethod.Get, "/fhir/r4/ExplanationOfBenefit/eob-001", smartToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var call = claims.Calls.Should().ContainSingle().Subject;
        call.Authorization.Should().NotBeNull().And.NotContain(smartToken);
        call.TenantHeader.Should().Be(Tenant);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(call.Authorization!["Bearer ".Length..]);
        jwt.Issuer.Should().Be(ChoDevelopmentAuth.ServiceIssuer);
        jwt.Subject.Should().Be("fhir-service");
        jwt.Claims.Should().Contain(c => c.Type == ChoClaimTypes.TenantId && c.Value == Tenant);
        jwt.Claims.Should().Contain(c => c.Type == ChoClaimTypes.Role && c.Value == ChoServiceRole.Name);
    }

    // ── Writes: actor from the token ──────────────────────────────────────────

    [Fact]
    public async Task AppealSubmit_TakesTheActorFromTheToken_NotTheBundle()
    {
        var appeals = new RecordingAppealAdapter();
        var host = _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IFhirAppealAdapter>();
            s.AddSingleton<IFhirAppealAdapter>(appeals);
        }));

        var token = ChoUser(Tenant, ChoRolePermissions.UMCoordinator);
        var response = await SendAsync(host, HttpMethod.Post, "/fhir/r4/$cho-appeal-submit", token,
            content: AppealBundle("pat-001", sender: "Practitioner/forged-sender"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var submitted = appeals.Submitted.Should().ContainSingle().Subject;
        var subject = new JwtSecurityTokenHandler().ReadJwtToken(token).Subject;
        submitted.Appeal.SubmittedBy.Should().Be(subject);
        submitted.Notes.Should().ContainSingle().Which.CreatedBy.Should().Be(subject);
    }

    [Fact]
    public async Task AppealSubmit_PatientTokenCannotAppealForAnotherMember()
    {
        var appeals = new RecordingAppealAdapter();
        var host = _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IFhirAppealAdapter>();
            s.AddSingleton<IFhirAppealAdapter>(appeals);
        }));

        var response = await SendAsync(host, HttpMethod.Post, "/fhir/r4/$cho-appeal-submit",
            PatientToken("pat-001", "patient/Task.write patient/*.read"),
            content: AppealBundle("pat-002", sender: "Practitioner/p1"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        appeals.Submitted.Should().BeEmpty();
    }

    // ── Helpers (resources, tokens, fakes) ────────────────────────────────────

    private static string? MemberOf(Resource resource) => (resource switch
    {
        Patient p => p.Id,
        Coverage c => c.Beneficiary?.Reference,
        Claim c => c.Patient?.Reference,
        Encounter e => e.Subject?.Reference,
        _ => null,
    })?.Replace("Patient/", string.Empty);

    private static string CraftChoToken(string tenant, string role, params SecurityClaim[] extra)
    {
        var key = new SymmetricSecurityKey(Convert.FromBase64String(ChoDevelopmentAuth.SymmetricKey));
        var claims = new List<SecurityClaim>
        {
            new("sub", "crafted-user"),
            new(ChoClaimTypes.TenantId, tenant),
            new(ChoClaimTypes.Role, role),
        };
        claims.AddRange(extra);
        var token = new JwtSecurityToken(
            ChoDevelopmentAuth.UserIssuer, ChoDevelopmentAuth.Audience, claims,
            DateTime.UtcNow.AddSeconds(-5), DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static StringContent AppealBundle(string memberId, string sender)
    {
        var bundle = new Bundle
        {
            Type = Bundle.BundleType.Transaction,
            Entry =
            [
                new Bundle.EntryComponent
                {
                    Resource = new Hl7.Fhir.Model.Task
                    {
                        Status = Hl7.Fhir.Model.Task.TaskStatus.Requested,
                        Intent = Hl7.Fhir.Model.Task.TaskIntent.Order,
                        For = new ResourceReference($"Patient/{memberId}"),
                        Focus = new ResourceReference("Claim/clm-001"),
                        Requester = new ResourceReference("Practitioner/p1"),
                        Description = "Appeal",
                    },
                },
                new Bundle.EntryComponent
                {
                    Resource = new Patient
                    {
                        Id = memberId,
                        Name = [new HumanName { Family = "Doe", Given = ["Jane"] }],
                    },
                },
                new Bundle.EntryComponent
                {
                    Resource = new Communication
                    {
                        Status = EventStatus.Completed,
                        Sender = new ResourceReference(sender),
                        Payload = [new Communication.PayloadComponent { Content = new FhirString("note") }],
                    },
                },
            ],
        };
        return new StringContent(JsonSerializer.Serialize(bundle, FhirJson), Encoding.UTF8, "application/fhir+json");
    }

    internal sealed record OutboundCall(string Path, string Query, string? Authorization, string? TenantHeader);

    /// <summary>Stands in for claims-service and records what fhir-service sent it.</summary>
    internal sealed class RecordingClaimsService : HttpMessageHandler
    {
        public List<OutboundCall> Calls { get; } = [];

        private static string Eob(string id, string patient) =>
            "{\"resourceType\":\"ExplanationOfBenefit\",\"id\":\"" + id + "\",\"status\":\"active\",\"use\":\"claim\"," +
            "\"type\":{\"coding\":[{\"system\":\"http://terminology.hl7.org/CodeSystem/claim-type\",\"code\":\"professional\"}]}," +
            "\"patient\":{\"reference\":\"Patient/" + patient + "\"},\"insurer\":{\"display\":\"CHO\"},\"provider\":{\"display\":\"P\"}," +
            "\"created\":\"2026-01-15T00:00:00Z\",\"outcome\":\"complete\",\"insurance\":[{\"focal\":true,\"coverage\":{\"display\":\"C\"}}]}";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            lock (Calls)
            {
                Calls.Add(new OutboundCall(uri.AbsolutePath, uri.Query,
                    request.Headers.Authorization?.ToString(),
                    request.Headers.TryGetValues("X-Tenant-ID", out var t) ? t.FirstOrDefault() : null));
            }

            var body = uri.AbsolutePath switch
            {
                "/fhir/ExplanationOfBenefit/eob-001" => Eob("eob-001", "pat-001"),
                "/fhir/ExplanationOfBenefit/eob-003" => Eob("eob-003", "pat-002"),
                "/fhir/ExplanationOfBenefit" => "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"total\":0}",
                _ => null,
            };

            return Task.FromResult(new HttpResponseMessage(body is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            {
                Content = new StringContent(body ?? "{\"resourceType\":\"OperationOutcome\"}", Encoding.UTF8, "application/fhir+json"),
            });
        }
    }

    private sealed class RecordingAppealAdapter : IFhirAppealAdapter
    {
        public List<AppealSubmitBundleDto> Submitted { get; } = [];

        public Task<IReadOnlyList<AppealSubmitChildOutcome>> SubmitAppealAsync(
            AppealSubmitBundleDto bundle, string tenantId, CancellationToken ct = default)
        {
            Submitted.Add(bundle);
            return Task.FromResult<IReadOnlyList<AppealSubmitChildOutcome>>([]);
        }

        public Task<AppealDto?> GetAppealAsync(string id, string tenantId, CancellationToken ct = default)
            => Task.FromResult<AppealDto?>(null);

        public Task<(IReadOnlyList<AppealDto> Items, int Total)> SearchAppealsAsync(
            AppealSearchQuery query, string tenantId, CancellationToken ct = default)
            => Task.FromResult<(IReadOnlyList<AppealDto>, int)>(([], 0));

        public Task<(AppealDto Appeal, AppealNoteDto Note)?> GetNoteByIdAsync(
            string noteId, string tenantId, CancellationToken ct = default)
            => Task.FromResult<(AppealDto, AppealNoteDto)?>(null);

        public Task<(AppealDto Appeal, AppealAttachmentDto Attachment)?> GetAttachmentByIdAsync(
            string attachmentId, string tenantId, CancellationToken ct = default)
            => Task.FromResult<(AppealDto, AppealAttachmentDto)?>(null);
    }
}
