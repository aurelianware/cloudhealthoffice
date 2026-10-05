using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AttachmentService.Models;
using AttachmentService.Tests.Support;
using CloudHealthOffice.Infrastructure.Security;

namespace AttachmentService.Tests.Security;

/// <summary>
/// Pins the shared CHO authentication contract for attachment-service: the
/// tenant and the submitting user come from the validated token only. The
/// <c>tenantId</c> query parameter and form field the service used to trust
/// are ignored, so one tenant can no longer read or write another's PHI.
/// </summary>
[Collection(nameof(AttachmentServiceCollection))]
public class AttachmentAuthenticationTests
{
    private const string Tenant = "tenant-a";
    private const string OtherTenant = "tenant-b";
    private const string TokenUser = "token-user-42";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly AttachmentServiceFactory _factory;

    public AttachmentAuthenticationTests(AttachmentServiceFactory factory)
    {
        _factory = factory;
        _factory.Reset();
    }

    private HttpClient NewClient(string tenant = Tenant, params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(TokenUser, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    private static MultipartFormDataContent CreateForm(string formTenantId)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(formTenantId), "TenantId" },
            { new StringContent("attacker"), "CreatedBy" },
            { new StringContent("CLM-1"), "ClaimId" },
            { new StringContent("PAYER-1"), "PayerId" },
            { new StringContent("Payer"), "PayerName" },
            { new StringContent("PROV-1"), "ProviderId" },
            { new StringContent("SUB-1"), "SubscriberId" },
            { new StringContent("Medical Records"), "DocumentType" },
            { new StringContent("PDF"), "DocumentFormat" },
        };
        form.Add(new ByteArrayContent("%PDF-1.4 test"u8.ToArray()), "file", "record.pdf");
        return form;
    }

    // ── Pipeline: token required, tenant from the token ─────────────────

    [Fact]
    public async Task NoToken_IsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/attachments/claim/CLM-1?tenantId=" + Tenant);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TenantHeaderWithoutToken_IsUnauthorized()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        var response = await client.GetAsync("/api/attachments/claim/CLM-1");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TenantHeaderDisagreeingWithToken_IsForbidden()
    {
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", ChoDevelopmentAuth.UserToken(Tenant, ChoRolePermissions.TenantAdmin));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", OtherTenant);

        var response = await client.GetAsync("/api/attachments/claim/CLM-1");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task RoleWithoutAttachmentPermissions_IsForbidden()
    {
        var client = NewClient(Tenant, ChoRolePermissions.Finance);

        var response = await client.GetAsync("/api/attachments/claim/CLM-1");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ReadOnlyRole_CannotSubmitAttachment()
    {
        // MemberServices holds attachments:read but not attachments:write.
        var client = NewClient(Tenant, ChoRolePermissions.MemberServices);

        var response = await client.PostAsync("/api/attachments", CreateForm(Tenant));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Attachments.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadOnlyRole_CanRead()
    {
        _factory.Attachments.Seed(Tenant);
        var client = NewClient(Tenant, ChoRolePermissions.MemberServices);

        var response = await client.GetAsync("/api/attachments/claim/CLM-1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Tenant isolation: the query string no longer picks the tenant ───

    [Fact]
    public async Task GetByClaim_QueryTenantId_IsIgnored_TokenTenantIsUsed()
    {
        _factory.Attachments.Seed(OtherTenant, claimId: "CLM-1");
        var mine = _factory.Attachments.Seed(Tenant, claimId: "CLM-1");
        var client = NewClient(Tenant);

        var response = await client.GetAsync("/api/attachments/claim/CLM-1?tenantId=" + OtherTenant);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await response.Content.ReadFromJsonAsync<List<Attachment>>(Json);
        list!.Select(a => a.Id).Should().Equal(mine.Id);
    }

    [Fact]
    public async Task GetById_OtherTenantsAttachment_ViaQueryTenantId_IsNotFound()
    {
        var theirs = _factory.Attachments.Seed(OtherTenant);
        var client = NewClient(Tenant);

        var response = await client.GetAsync($"/api/attachments/{theirs.Id}?tenantId={OtherTenant}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetById_WithoutQueryTenantId_UsesTokenTenant()
    {
        var mine = _factory.Attachments.Seed(Tenant);
        var client = NewClient(Tenant);

        var response = await client.GetAsync($"/api/attachments/{mine.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Download_OtherTenantsAttachment_ViaQueryTenantId_IsNotFound()
    {
        var theirs = _factory.Attachments.Seed(OtherTenant);
        theirs.BlobContainerName = "attachments";
        theirs.BlobName = $"{OtherTenant}/claims/CLM-1/{theirs.Id}.pdf";
        await _factory.Documents.UploadAsync("attachments", theirs.BlobName, new MemoryStream("phi"u8.ToArray()), "application/pdf");
        var client = NewClient(Tenant);

        var response = await client.GetAsync($"/api/attachments/{theirs.Id}/download?tenantId={OtherTenant}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GenerateAcknowledgment_OtherTenantsAttachment_ViaQueryTenantId_IsNotFound()
    {
        var theirs = _factory.Attachments.Seed(OtherTenant);
        var client = NewClient(Tenant);

        var response = await client.PostAsync($"/api/attachments/{theirs.Id}/acknowledgment?tenantId={OtherTenant}", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        theirs.Generated999.Should().BeNull();
    }

    // ── Actor and tenant from the token on writes ───────────────────────

    [Fact]
    public async Task CreateAttachment_FormTenantAndActor_AreIgnored_TokenWins()
    {
        var client = NewClient(Tenant);

        var response = await client.PostAsync("/api/attachments", CreateForm(OtherTenant));

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        var stored = _factory.Attachments.Items.Values.Should().ContainSingle().Subject;
        stored.TenantId.Should().Be(Tenant);
        stored.CreatedBy.Should().Be(TokenUser);
        stored.BlobName.Should().StartWith(Tenant + "/");
        _factory.Documents.GetBytes("attachments", stored.BlobName!).Should().NotBeNull();
    }

    [Fact]
    public async Task GenerateAcknowledgment_RecordsActorFromToken()
    {
        var mine = _factory.Attachments.Seed(Tenant);
        var client = NewClient(Tenant);

        var response = await client.PostAsync($"/api/attachments/{mine.Id}/acknowledgment", null);

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var stored = _factory.Attachments.Items[mine.Id];
        stored.Generated999.Should().NotBeNullOrEmpty();
        stored.LastUpdatedBy.Should().Be(TokenUser);
        _factory.TradingPartners.Verify(t => t.GetByPayerIdAsync("PAYER-1", Tenant), Times.Once);
    }

    [Fact]
    public async Task Health_IsAnonymous()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

[CollectionDefinition(nameof(AttachmentServiceCollection))]
public class AttachmentServiceCollection : ICollectionFixture<AttachmentServiceFactory>;
