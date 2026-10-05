using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CloudHealthOffice.Infrastructure.Security;
using RfaiService.Models;
using RfaiService.Tests.Support;

namespace RfaiService.Tests.Security;

/// <summary>
/// Pins the shared CHO authentication contract for rfai-service: a CHO token
/// is required, the tenant and the acting user come from that token only, the
/// legacy <c>by-auth/{tenantId}/{authNumber}</c> route only echoes the token
/// tenant, and reads never hand out the storage location of submitted PHI.
/// </summary>
[Collection(nameof(RfaiServiceCollection))]
public class RfaiAuthenticationTests
{
    private const string Tenant = "tenant-a";
    private const string OtherTenant = "tenant-b";
    private const string TokenUser = "token-user-42";

    private readonly RfaiServiceFactory _factory;

    public RfaiAuthenticationTests(RfaiServiceFactory factory)
    {
        _factory = factory;
        _factory.Cases.Clear();
    }

    private HttpClient UserClient(string tenant = Tenant, params string[] roles)
    {
        var client = _factory.CreateDefaultClient(
            new ChoDevelopmentTokenHandler(TokenUser, roles.Length > 0 ? roles : [ChoRolePermissions.UMCoordinator]));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    private HttpClient BearerClient(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<JsonNode> Body(HttpResponseMessage response)
        => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    // ── Pipeline: token required, tenant from the token ─────────────────

    [Fact]
    public async Task NoToken_IsUnauthorized()
    {
        _factory.Cases.Seed(Tenant);

        var response = await _factory.CreateClient().GetAsync("/api/rfai/case-1");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TenantHeaderWithoutToken_IsUnauthorized()
    {
        _factory.Cases.Seed(Tenant);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        var response = await client.GetAsync("/api/rfai/case-1");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task NoTenantAnywhere_NeverFallsBackToDefaultTenant()
    {
        _factory.Cases.Seed("default-tenant");

        var response = await _factory.CreateClient().GetAsync("/api/rfai/case-1");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TenantHeaderDisagreeingWithToken_IsForbidden()
    {
        _factory.Cases.Seed(OtherTenant);
        var client = BearerClient(ChoDevelopmentAuth.UserTokenIssuer()
            .IssueUserToken(TokenUser, Tenant, [ChoRolePermissions.UMCoordinator]));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", OtherTenant);

        var response = await client.GetAsync("/api/rfai/case-1");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task OtherTenantsCase_IsNotFound()
    {
        _factory.Cases.Seed(OtherTenant);

        var response = await UserClient(Tenant).GetAsync("/api/rfai/case-1");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Permissions ──────────────────────────────────────────────────────

    [Fact]
    public async Task RoleWithoutRfaiRead_CannotReadCases()
    {
        _factory.Cases.Seed(Tenant);

        var response = await UserClient(Tenant, ChoRolePermissions.ClaimsExaminer).GetAsync("/api/rfai/case-1");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ReadOnlyRole_CannotCreateOrClose()
    {
        _factory.Cases.Seed(Tenant);
        var client = UserClient(Tenant, ChoRolePermissions.ComplianceOfficer);

        var read = await client.GetAsync("/api/rfai/case-1");
        var create = await client.PostAsJsonAsync("/api/rfai", NewCreateBody());
        var close = await client.PostAsJsonAsync("/api/rfai/case-1/close", new { reason = "done" });

        read.StatusCode.Should().Be(HttpStatusCode.OK);
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        close.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Cases.Find(Tenant, "case-1")!.Status.Should().Be(RfaiStatus.Open);
    }

    [Fact]
    public async Task ArgoRfaiWorkloadWithClaimsReadOnly_CannotReachRfai()
    {
        // wf-277-rfai is registered with claims:read only (docs/security/argo-service-tokens.md);
        // it calls claims-service, not rfai-service, and must not be able to.
        _factory.Cases.Seed(Tenant);
        var client = BearerClient(ChoDevelopmentAuth.WorkloadTokenIssuer()
            .IssueWorkloadToken("wf-277-rfai", Tenant, ["claims:read"]));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        var response = await client.GetAsync("/api/rfai/case-1");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Legacy route: the path tenant only echoes the token tenant ───────

    [Fact]
    public async Task LegacyRoute_MatchingTenant_Succeeds()
    {
        _factory.Cases.Seed(Tenant);

        var response = await UserClient(Tenant).GetAsync($"/api/rfai/by-auth/{Tenant}/AUTH-1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Body(response)).AsArray().Should().HaveCount(1);
    }

    [Fact]
    public async Task LegacyRoute_PathTenantOtherThanToken_IsForbidden()
    {
        _factory.Cases.Seed(OtherTenant);

        var response = await UserClient(Tenant).GetAsync($"/api/rfai/by-auth/{OtherTenant}/AUTH-1");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task LegacyRoute_HeaderAndPathNamingAnotherTenant_CannotOverrideToken()
    {
        // Previously the tenant came from X-Tenant-ID, so header + path naming
        // tenant-b read tenant-b's cases whatever the token said.
        _factory.Cases.Seed(OtherTenant);
        var client = BearerClient(ChoDevelopmentAuth.UserTokenIssuer()
            .IssueUserToken(TokenUser, Tenant, [ChoRolePermissions.UMCoordinator]));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", OtherTenant);

        var response = await client.GetAsync($"/api/rfai/by-auth/{OtherTenant}/AUTH-1");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("AUTH-1");
    }

    // ── Actor from the token on every write ──────────────────────────────

    [Fact]
    public async Task Create_RequestedByIsTheTokenActor_NotTheBody()
    {
        var response = await UserClient().PostAsJsonAsync("/api/rfai", NewCreateBody());

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await Body(response))["id"]!.GetValue<string>();
        var stored = _factory.Cases.Find(Tenant, id)!;
        stored.RequestedBy.Should().Be(TokenUser);
        stored.TenantId.Should().Be(Tenant);
    }

    [Fact]
    public async Task Close_ClosedByIsTheTokenActor_NotTheBody()
    {
        _factory.Cases.Seed(Tenant);

        var response = await UserClient().PostAsJsonAsync("/api/rfai/case-1/close",
            new { by = "attacker", reason = "done" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = _factory.Cases.Find(Tenant, "case-1")!;
        stored.Status.Should().Be(RfaiStatus.Closed);
        stored.ClosedBy.Should().Be(TokenUser);
        stored.ClosureReason.Should().Be("done");
    }

    [Fact]
    public async Task Cancel_ClosedByIsTheTokenActor_NotTheBody()
    {
        _factory.Cases.Seed(Tenant);

        var response = await UserClient().PostAsJsonAsync("/api/rfai/case-1/cancel",
            new { by = "attacker", reason = "withdrawn" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = _factory.Cases.Find(Tenant, "case-1")!;
        stored.Status.Should().Be(RfaiStatus.Cancelled);
        stored.ClosedBy.Should().Be(TokenUser);
    }

    [Fact]
    public async Task RecordResponse_SubmittedByIsTheTokenActor_NotTheBody()
    {
        _factory.Cases.Seed(Tenant);

        var response = await UserClient().PostAsJsonAsync("/api/rfai/case-1/responses", new
        {
            artifacts = new[]
            {
                new { submissionId = "sub-1", storageKey = "k/1.pdf", submittedBy = "attacker" },
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = _factory.Cases.Find(Tenant, "case-1")!;
        stored.ReceivedAttachments.Single(a => a.SubmissionId == "sub-1").SubmittedBy.Should().Be(TokenUser);
    }

    [Fact]
    public async Task RecordResponse_FromFhirServiceServiceToken_RecordsTheServiceAsSubmitter()
    {
        // SMART callers reach rfai-service through fhir-service's own service token.
        _factory.Cases.Seed(Tenant);
        var client = BearerClient(ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken("fhir-service", Tenant));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        var response = await client.PostAsJsonAsync("/api/rfai/case-1/responses", new
        {
            artifacts = new[] { new { submissionId = "sub-1", submittedBy = "smart-client-claims-to-be-someone" } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Cases.Find(Tenant, "case-1")!.ReceivedAttachments
            .Single(a => a.SubmissionId == "sub-1").SubmittedBy.Should().Be("fhir-service");
    }

    [Fact]
    public async Task AttachmentReceived_SubmittedByIsTheTokenActor()
    {
        _factory.Cases.Seed(Tenant);

        var response = await UserClient().PostAsJsonAsync("/api/rfai/case-1/attachments/received", new
        {
            attachmentControlNumber = "ACN-9",
            storageProvider = "azure-blob",
            storageKey = "k/9.pdf",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Cases.Find(Tenant, "case-1")!.ReceivedAttachments
            .Single(a => a.SubmissionId == "ACN-9").SubmittedBy.Should().Be(TokenUser);
    }

    // ── Minimum necessary: no storage pointers to submitted PHI on reads ─

    [Theory]
    [InlineData("/api/rfai/case-1")]
    [InlineData("/api/rfai/by-tracking/TRK-1")]
    [InlineData("/api/rfai/by-auth/AUTH-1")]
    [InlineData("/api/rfai/by-auth/tenant-a/AUTH-1")]
    public async Task Reads_DoNotExposeAttachmentStorageLocation(string path)
    {
        _factory.Cases.Seed(Tenant);

        var response = await UserClient().GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await response.Content.ReadAsStringAsync();
        text.Should().Contain("sub-0", "the receipt metadata is still returned");
        text.Should().NotContain("rfai/case-1/sub-0.pdf");
        text.Should().NotContain("azure-blob");

        // The stored record keeps the pointer.
        _factory.Cases.Find(Tenant, "case-1")!.ReceivedAttachments[0].StorageKey
            .Should().Be($"{Tenant}/rfai/case-1/sub-0.pdf");
    }

    [Fact]
    public async Task RecordResponse_ResultDoesNotExposeAttachmentStorageLocation()
    {
        _factory.Cases.Seed(Tenant);

        var response = await UserClient().PostAsJsonAsync("/api/rfai/case-1/responses", new
        {
            artifacts = new[] { new { submissionId = "sub-1", storageProvider = "azure-blob", storageKey = "secret/key-1.pdf" } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await response.Content.ReadAsStringAsync();
        text.Should().NotContain("secret/key-1.pdf");
        text.Should().NotContain("azure-blob");
        _factory.Cases.Find(Tenant, "case-1")!.ReceivedAttachments
            .Single(a => a.SubmissionId == "sub-1").StorageKey.Should().Be("secret/key-1.pdf");
    }

    private static object NewCreateBody() => new
    {
        tenantId = OtherTenant,
        authNumber = "AUTH-NEW",
        correlationKey = "decision-1",
        reviewDecision = "A4",
        requestedBy = "attacker",
        requestSource = RfaiRequestSources.ReviewDecisionA4,
        requestedItems = new[] { new { code = "03", description = "Lab results" } },
    };
}
