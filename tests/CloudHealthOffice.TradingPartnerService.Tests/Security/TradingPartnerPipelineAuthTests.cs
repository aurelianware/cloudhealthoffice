using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.TradingPartnerService.Models;
using CloudHealthOffice.TradingPartnerService.Services;
using FluentAssertions;
using Xunit;

namespace CloudHealthOffice.TradingPartnerService.Tests.Security;

/// <summary>
/// The real trading-partner-service pipeline. Every caller needs a CHO token, the
/// tenant comes from the token (route tenants only echo it), reads need
/// trading-partners:read, writes settings:manage, the actor comes from the token,
/// and no response ever carries a credential.
/// </summary>
public class TradingPartnerPipelineAuthTests : IClassFixture<TradingPartnerPipelineFactory>
{
    private const string Tenant = "tenant-1";
    private const string OtherTenant = "tenant-2";
    private const string Subject = "tp-admin-7";
    private const string Npi = "1234567890";
    private const string PasswordRef = "tp--tenant-1--availity-sftp-password";
    private const string KeyRef = "tp--tenant-1--availity-sftp-key";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TradingPartnerPipelineFactory _factory;

    public TradingPartnerPipelineAuthTests(TradingPartnerPipelineFactory factory)
    {
        _factory = factory;
        _factory.Reset();
    }

    private HttpClient Client(string tenant, params string[] roles) => ClientAs(tenant, Subject, roles);

    private HttpClient ClientAs(string tenant, string subject, params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(subject, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    private HttpClient ServiceClient(string tenant, string clientId = "payment-service")
    {
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken(clientId, tenant));
        return client;
    }

    private static TradingPartner Partner(string tenant, string partnerId = "availity", string env = "prod") => new()
    {
        Id = $"{partnerId}-{tenant}-{env}",
        TenantId = tenant,
        TradingPartnerId = partnerId,
        Environment = env,
        PartnerName = "Availity " + tenant,
        PartnerType = "Clearinghouse",
        X12Config = new X12Config { SenderId = "SND-" + tenant, ReceiverId = "RCV-" + tenant },
        SftpConfig = new SftpConfig
        {
            Enabled = true,
            Username = "sftp-user-" + tenant,
            Host = "sftp-" + tenant + ".example.com",
            PasswordSecretRef = $"tp--{tenant}--sftp-password",
            PrivateKeySecretRef = $"tp--{tenant}--sftp-key",
            Paths = new SftpPaths { Inbound = { ["837"] = "/in/837" } }
        },
        BlobConfig = new BlobConfig { ContainerName = "cho-prod", Paths = { ["raw"] = "raw/{transactionType}" } },
        ContactInfo = new ContactInfo { Email = "edi@" + tenant + ".example.com" },
        BillingProviderNpis = { Npi },
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        CreatedBy = "original-creator",
        LastTestedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private static Dictionary<string, object?> Body(object? sftp = null, string partnerId = "availity") => new()
    {
        ["tradingPartnerId"] = partnerId,
        ["environment"] = "prod",
        ["partnerName"] = "Availity LLC",
        ["partnerType"] = "Clearinghouse",
        ["x12Config"] = new { senderId = "030240928", receiverId = "PAYER01" },
        ["sftpConfig"] = sftp ?? new { enabled = true, username = "u1", host = "sftp.example.com" },
        ["billingProviderNpis"] = new[] { Npi }
    };

    // ── Authentication ──────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/TradingPartners/tenant/tenant-1")]
    [InlineData("/api/TradingPartners/tenant-1/availity/prod")]
    [InlineData("/api/tradingpartners/by-npi/tenant-1/1234567890/prod")]
    [InlineData("/api/TradingPartners/tenant-1/availity/prod/sftp/inbound/837")]
    [InlineData("/api/TradingPartners/tenant-1/availity/prod/blob/raw/837")]
    [InlineData("/api/TradingPartners/tenant-1/availity/prod/x12")]
    public async Task NoToken_IsRejected(string path)
    {
        _factory.Repository.Seed(Partner(Tenant));

        var response = await _factory.CreateClient().GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Repository.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ByNpi_TenantHeaderWithoutToken_IsRejected()
    {
        // Before: [AllowAnonymous]; anyone in the cluster could read any tenant's
        // partner (SFTP host/user, contacts, ISA ids) by naming the tenant.
        _factory.Repository.Seed(Partner(Tenant));
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        var response = await client.GetAsync($"/api/tradingpartners/by-npi/{Tenant}/{Npi}/prod");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("SND-" + Tenant);
    }

    [Fact]
    public async Task WritesWithoutToken_AreRejected()
    {
        var client = _factory.CreateClient();

        (await client.PostAsJsonAsync("/api/TradingPartners", Body(), Json)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.DeleteAsync($"/api/TradingPartners/{Tenant}/availity/prod")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsync($"/api/TradingPartners/{Tenant}/availity/prod/test", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Repository.All.Should().BeEmpty();
    }

    [Fact]
    public async Task TenantHeaderDisagreeingWithToken_IsForbidden()
    {
        _factory.Repository.Seed(Partner(OtherTenant));
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", ChoDevelopmentAuth.UserToken(Tenant, ChoRolePermissions.TenantAdmin));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", OtherTenant);

        var response = await client.GetAsync($"/api/TradingPartners/tenant/{OtherTenant}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task HealthEndpoints_NeedNoToken()
    {
        (await _factory.CreateClient().GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Tenant from the token: route tenants only echo it ───────────────

    [Theory]
    [InlineData("/api/TradingPartners/tenant/tenant-2")]
    [InlineData("/api/TradingPartners/tenant-2/availity/prod")]
    [InlineData("/api/tradingpartners/by-npi/tenant-2/1234567890/prod")]
    [InlineData("/api/TradingPartners/tenant-2/availity/prod/sftp/inbound/837")]
    [InlineData("/api/TradingPartners/tenant-2/availity/prod/blob/raw/837")]
    [InlineData("/api/TradingPartners/tenant-2/availity/prod/x12")]
    public async Task PathTenantOtherThanTokenTenant_CannotRead(string path)
    {
        _factory.Repository.Seed(Partner(OtherTenant));

        var response = await Client(Tenant).GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("SND-" + OtherTenant).And.NotContain("sftp-" + OtherTenant);
        _factory.Repository.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ByNpi_ServiceTokenForAnotherTenant_IsForbidden()
    {
        _factory.Repository.Seed(Partner(OtherTenant));

        var response = await ServiceClient(Tenant).GetAsync($"/api/tradingpartners/by-npi/{OtherTenant}/{Npi}/prod");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Repository.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task PathTenantOtherThanTokenTenant_CannotUpdateDeleteOrTest()
    {
        var original = Partner(OtherTenant);
        _factory.Repository.Seed(original);
        var client = Client(Tenant);

        (await client.PutAsJsonAsync($"/api/TradingPartners/{OtherTenant}/availity/prod", Body(), Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.DeleteAsync($"/api/TradingPartners/{OtherTenant}/availity/prod"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsync($"/api/TradingPartners/{OtherTenant}/availity/prod/test", null))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var stored = _factory.Repository.Find(OtherTenant, "availity", "prod");
        stored.Should().NotBeNull();
        stored!.PartnerName.Should().Be("Availity " + OtherTenant);
        stored.LastTestedAt.Should().Be(original.LastTestedAt);
        _factory.Repository.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_StoresUnderTokenTenant_AndIgnoresBodyTenant()
    {
        var body = Body();
        body["tenantId"] = OtherTenant;
        body["id"] = "forged-id";

        var response = await Client(Tenant).PostAsJsonAsync("/api/TradingPartners", body, Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        _factory.Repository.All.Should().ContainSingle();
        var stored = _factory.Repository.Find(Tenant, "availity", "prod");
        stored!.TenantId.Should().Be(Tenant);
        // Server-assigned and unambiguous (never the body's id, never the old {partner}-{tenant}-{env}).
        stored.Id.Should().Be(TradingPartnerIds.For(Tenant, "availity", "prod")).And.NotBe("forged-id");
        _factory.Repository.All.Should().NotContain(p => p.TenantId == OtherTenant);
    }

    [Fact]
    public async Task List_ReturnsOnlyTokenTenantPartners()
    {
        _factory.Repository.Seed(Partner(Tenant));
        _factory.Repository.Seed(Partner(OtherTenant));

        var list = await Client(Tenant, ChoRolePermissions.ClaimsSupervisor)
            .GetFromJsonAsync<List<TradingPartnerView>>($"/api/TradingPartners/tenant/{Tenant}", Json);

        list.Should().ContainSingle().Which.TenantId.Should().Be(Tenant);
    }

    // ── Permissions ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(ChoRolePermissions.ClaimsSupervisor)]
    [InlineData(ChoRolePermissions.Finance)]
    [InlineData(ChoRolePermissions.TenantAdmin)]
    public async Task Readers_WithTradingPartnersRead_CanRead(string role)
    {
        _factory.Repository.Seed(Partner(Tenant));

        var response = await Client(Tenant, role).GetAsync($"/api/TradingPartners/{Tenant}/availity/prod");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(ChoRolePermissions.ClaimsExaminer)]
    [InlineData(ChoRolePermissions.MemberServices)]
    [InlineData(ChoRolePermissions.FinanceApprover)]
    public async Task RolesWithoutTradingPartnersRead_CannotReadConfiguration(string role)
    {
        _factory.Repository.Seed(Partner(Tenant));

        var response = await Client(Tenant, role).GetAsync($"/api/TradingPartners/{Tenant}/availity/prod");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(ChoRolePermissions.ClaimsSupervisor)]
    [InlineData(ChoRolePermissions.Finance)]
    public async Task ReadOnlyRoles_CannotWrite(string role)
    {
        // Before: TestConnection carried no admin requirement, so any authenticated
        // caller could change the record (lastTestedAt) of any tenant.
        _factory.Repository.Seed(Partner(Tenant));
        var client = Client(Tenant, role);

        (await client.PostAsJsonAsync("/api/TradingPartners", Body(partnerId: "new-partner"), Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PutAsJsonAsync($"/api/TradingPartners/{Tenant}/availity/prod", Body(), Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.DeleteAsync($"/api/TradingPartners/{Tenant}/availity/prod"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsync($"/api/TradingPartners/{Tenant}/availity/prod/test", null))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        _factory.Repository.Calls.Should().BeEmpty();
        _factory.Repository.Find(Tenant, "availity", "prod")!.LastTestedAt.Should().Be(Partner(Tenant).LastTestedAt);
    }

    [Fact]
    public async Task TenantAdmin_CanCreateUpdateTestAndDelete()
    {
        var client = Client(Tenant);

        (await client.PostAsJsonAsync("/api/TradingPartners", Body(), Json)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await client.PutAsJsonAsync($"/api/TradingPartners/{Tenant}/availity/prod", Body(), Json)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync($"/api/TradingPartners/{Tenant}/availity/prod/test", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.DeleteAsync($"/api/TradingPartners/{Tenant}/availity/prod")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.Repository.All.Should().BeEmpty();
    }

    // ── payment-service's NPI lookup ────────────────────────────────────

    [Fact]
    public async Task ByNpi_PaymentServiceTokenForRunTenant_ResolvesPartner()
    {
        _factory.Repository.Seed(Partner(Tenant));
        // What payment-service's TradingPartnersClient sends with no caller: the
        // tenant in the route and X-Tenant-ID, a service token for that tenant.
        var client = ServiceClient(Tenant);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/tradingpartners/by-npi/{Tenant}/{Npi}/Prod");
        request.Headers.Add("X-Tenant-ID", Tenant);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var summary = await response.Content.ReadFromJsonAsync<PaymentServiceTradingPartnerSummary>(Json);
        summary!.TradingPartnerId.Should().Be("availity");
        summary.TenantId.Should().Be(Tenant);
        summary.X12Config!.SenderId.Should().Be("SND-" + Tenant);
        summary.X12Config.ReceiverId.Should().Be("RCV-" + Tenant);
        summary.BillingProviderNpis.Should().Contain(Npi);
    }

    [Fact]
    public async Task ByNpi_FinanceApproverExecutingARun_ResolvesPartner()
    {
        // Run execution needs payments:approve (FinanceApprover), whose token
        // payment-service forwards. FinanceApprover holds payments:read but not
        // trading-partners:read.
        _factory.Repository.Seed(Partner(Tenant));

        var response = await ClientAs(Tenant, "approver-1", ChoRolePermissions.FinanceApprover)
            .GetAsync($"/api/tradingpartners/by-npi/{Tenant}/{Npi}/prod");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ByNpi_ReturnsRoutingFieldsOnly()
    {
        _factory.Repository.Seed(Partner(Tenant));

        var response = await ServiceClient(Tenant).GetAsync($"/api/tradingpartners/by-npi/{Tenant}/{Npi}/prod");

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().NotContain("sftp").And.NotContain("contactInfo").And.NotContain("SecretRef").And.NotContain("tp--");
    }

    [Fact]
    public async Task ByNpi_UnknownNpi_Is404()
    {
        _factory.Repository.Seed(Partner(Tenant));

        var response = await ServiceClient(Tenant).GetAsync($"/api/tradingpartners/by-npi/{Tenant}/9999999999/prod");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ByNpi_RoleWithNeitherPermission_IsForbidden()
    {
        _factory.Repository.Seed(Partner(Tenant));

        var response = await Client(Tenant, ChoRolePermissions.MemberServices)
            .GetAsync($"/api/tradingpartners/by-npi/{Tenant}/{Npi}/prod");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Actor from the token ────────────────────────────────────────────

    [Fact]
    public async Task Create_RecordsActorFromToken_NotFromBody()
    {
        var body = Body();
        body["createdBy"] = "forged-user";
        body["updatedBy"] = "forged-user";
        body["createdAt"] = "2001-01-01T00:00:00Z";
        body["lastTestedAt"] = "2001-01-01T00:00:00Z";

        var response = await Client(Tenant).PostAsJsonAsync("/api/TradingPartners", body, Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var stored = _factory.Repository.Find(Tenant, "availity", "prod")!;
        stored.CreatedBy.Should().Be(Subject);
        stored.UpdatedBy.Should().Be(Subject);
        stored.CreatedAt.Should().BeAfter(new DateTime(2020, 1, 1));
        stored.LastTestedAt.Should().BeNull();
    }

    [Fact]
    public async Task Update_RecordsActorFromToken_AndKeepsCreationAndTestHistory()
    {
        var original = Partner(Tenant);
        _factory.Repository.Seed(original);
        var body = Body();
        body["createdBy"] = "forged-user";
        body["updatedBy"] = "forged-user";
        body["createdAt"] = "2001-01-01T00:00:00Z";
        body["lastTestedAt"] = "2001-01-01T00:00:00Z";

        var response = await ClientAs(Tenant, "second-admin", ChoRolePermissions.TenantAdmin)
            .PutAsJsonAsync($"/api/TradingPartners/{Tenant}/availity/prod", body, Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = _factory.Repository.Find(Tenant, "availity", "prod")!;
        stored.PartnerName.Should().Be("Availity LLC");
        stored.UpdatedBy.Should().Be("second-admin");
        stored.CreatedBy.Should().Be("original-creator");
        stored.CreatedAt.Should().Be(original.CreatedAt);
        stored.LastTestedAt.Should().Be(original.LastTestedAt);
    }

    [Fact]
    public async Task TestConnection_RecordsWhoStartedIt()
    {
        _factory.Repository.Seed(Partner(Tenant));

        var response = await Client(Tenant).PostAsync($"/api/TradingPartners/{Tenant}/availity/prod/test", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Repository.Find(Tenant, "availity", "prod")!.LastTestedBy.Should().Be(Subject);
    }

    // ── Credentials: never accepted as literals, never returned ─────────

    public static IEnumerable<object[]> LiteralCredentialBodies()
    {
        yield return [new Dictionary<string, object?> { ["sftpConfig"] = new { host = "h", username = "u", password = "hunter2" } }];
        yield return [new Dictionary<string, object?> { ["sftpConfig"] = new { host = "h", privateKey = "literal-key-material" } }];
        yield return [new Dictionary<string, object?> { ["sftpConfig"] = new { host = "h", private_key_passphrase = "p" } }];
        yield return [new Dictionary<string, object?> { ["apiKey"] = "sk-live-123" }];
        yield return [new Dictionary<string, object?> { ["as2Config"] = new { certificate = "pub", privateKeyPem = "-----BEGIN PRIVATE KEY-----" } }];
        yield return [new Dictionary<string, object?> { ["endpoints"] = new[] { new { url = "https://x", clientSecret = "s" } } }];
    }

    [Theory]
    [MemberData(nameof(LiteralCredentialBodies))]
    public async Task Create_WithLiteralCredential_IsRefused_AndNothingStored(Dictionary<string, object?> extra)
    {
        var body = Body();
        foreach (var (key, value) in extra)
            body[key] = value;

        var response = await Client(Tenant).PostAsJsonAsync("/api/TradingPartners", body, Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("hunter2").And.NotContain("BEGIN");
        _factory.Repository.All.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_WithLiteralCredential_IsRefused_AndRecordUnchanged()
    {
        _factory.Repository.Seed(Partner(Tenant));
        var body = Body(sftp: new { host = "h", username = "u", password = "hunter2" });

        var response = await Client(Tenant).PutAsJsonAsync($"/api/TradingPartners/{Tenant}/availity/prod", body, Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Repository.Find(Tenant, "availity", "prod")!.PartnerName.Should().Be("Availity " + Tenant);
    }

    [Fact]
    public async Task KeyVaultReferences_AreStored_ButNeverReturned()
    {
        var body = Body(sftp: new { enabled = true, host = "h", username = "u", passwordSecretRef = PasswordRef, privateKeySecretRef = KeyRef });
        var client = Client(Tenant);

        var created = await client.PostAsJsonAsync("/api/TradingPartners", body, Json);
        var single = await client.GetAsync($"/api/TradingPartners/{Tenant}/availity/prod");
        var list = await client.GetAsync($"/api/TradingPartners/tenant/{Tenant}");
        var byNpi = await client.GetAsync($"/api/tradingpartners/by-npi/{Tenant}/{Npi}/prod");
        var updated = await client.PutAsJsonAsync($"/api/TradingPartners/{Tenant}/availity/prod", Body(), Json);

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var stored = _factory.Repository.Find(Tenant, "availity", "prod")!;
        foreach (var response in new[] { created, single, list, byNpi, updated })
        {
            response.IsSuccessStatusCode.Should().BeTrue();
            var text = await response.Content.ReadAsStringAsync();
            text.Should().NotContain(PasswordRef).And.NotContain(KeyRef).And.NotContain("SecretRef");
        }

        var view = await single.Content.ReadFromJsonAsync<TradingPartnerView>(Json);
        view!.SftpConfig!.PasswordConfigured.Should().BeTrue();
        view.SftpConfig.PrivateKeyConfigured.Should().BeTrue();
        stored.SftpConfig!.Username.Should().Be("u1");
    }

    [Fact]
    public async Task Update_WithoutReferences_KeepsStoredReferences_EmptyStringClears()
    {
        _factory.Repository.Seed(Partner(Tenant));
        var client = Client(Tenant);

        (await client.PutAsJsonAsync($"/api/TradingPartners/{Tenant}/availity/prod", Body(), Json))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var kept = _factory.Repository.Find(Tenant, "availity", "prod")!.SftpConfig!;
        kept.PasswordSecretRef.Should().Be($"tp--{Tenant}--sftp-password");
        kept.PrivateKeySecretRef.Should().Be($"tp--{Tenant}--sftp-key");

        (await client.PutAsJsonAsync($"/api/TradingPartners/{Tenant}/availity/prod",
                Body(sftp: new { host = "h", username = "u", passwordSecretRef = "" }), Json))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var cleared = _factory.Repository.Find(Tenant, "availity", "prod")!.SftpConfig!;
        cleared.PasswordSecretRef.Should().BeNull();
        cleared.PrivateKeySecretRef.Should().Be($"tp--{Tenant}--sftp-key");
    }

    [Theory]
    [InlineData("tp--tenant-2--sftp-password")] // another tenant's secret
    [InlineData("cosmos-key")]                  // a platform secret
    [InlineData("tp--tenant-1--")]              // prefix only
    [InlineData("hunter2 with spaces")]         // a literal password
    public async Task SecretReferenceOutsideCallerTenant_IsRefused(string reference)
    {
        var body = Body(sftp: new { host = "h", username = "u", passwordSecretRef = reference });

        var response = await Client(Tenant).PostAsJsonAsync("/api/TradingPartners", body, Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Repository.All.Should().BeEmpty();
    }

    /// <summary>The fields payment-service's TradingPartnerSummary reads.</summary>
    private sealed class PaymentServiceTradingPartnerSummary
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("tenantId")] public string TenantId { get; set; } = string.Empty;
        [JsonPropertyName("tradingPartnerId")] public string TradingPartnerId { get; set; } = string.Empty;
        [JsonPropertyName("environment")] public string Environment { get; set; } = string.Empty;
        [JsonPropertyName("partnerName")] public string PartnerName { get; set; } = string.Empty;
        [JsonPropertyName("x12Config")] public X12Config? X12Config { get; set; }
        [JsonPropertyName("billingProviderNpis")] public List<string> BillingProviderNpis { get; set; } = new();
    }
}
