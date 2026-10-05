using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using EligibilityService.Models;
using NSubstitute;

namespace CloudHealthOffice.EligibilityService.Tests.Security;

/// <summary>
/// The real eligibility-service pipeline: a CHO token is required, the tenant
/// comes from that token only (never from X-Tenant-ID, the query string or the
/// body), and permissions follow the service defaults (eligibility:check /
/// settings:manage) with 270-style POST inquiries on eligibility:check.
/// </summary>
public class EligibilityPipelineAuthTests : IClassFixture<EligibilityApiFactory>
{
    private readonly EligibilityApiFactory _factory;

    public EligibilityPipelineAuthTests(EligibilityApiFactory factory)
    {
        _factory = factory;
        _factory.EligibilityService.QuickEligibilityCheckAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<DateTime>())
            .Returns((true, "1", "IND", "Active coverage"));
        _factory.EligibilityService.ProcessInquiryAsync(Arg.Any<EligibilityInquiry>())
            .Returns(new EligibilityResponse { Id = "resp-1", InquiryId = "inq-1", TenantId = "tenant-a" });
        _factory.EligibilityService.CheckAuthRequirementAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns((false, "No authorization required"));
    }

    private HttpClient ClientFor(string tenant, params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler("dev-user", roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    // ── Tenant from the token only ──────────────────────────────────────

    [Fact]
    public async Task TenantHeaderWithoutToken_IsRejected()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-a");

        var response = await client.GetAsync("/api/eligibility/check?subscriberId=SUB-001");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TenantQueryWithoutToken_IsRejected()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/eligibility/check?subscriberId=SUB-001&tenantId=tenant-a");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TenantHeaderDisagreeingWithToken_IsForbidden()
    {
        using var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ChoDevelopmentAuth.UserToken("tenant-a", ChoRolePermissions.TenantAdmin));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-b");

        var response = await client.GetAsync("/api/eligibility/check?subscriberId=SUB-001");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TenantQueryParameter_DoesNotOverrideTokenTenant()
    {
        using var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ChoDevelopmentAuth.UserToken("tenant-q-token", ChoRolePermissions.TenantAdmin));

        var response = await client.GetAsync(
            "/api/eligibility/check?subscriberId=SUB-Q-001&tenantId=tenant-q-other");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _factory.EligibilityService.Received().QuickEligibilityCheckAsync(
            "tenant-q-token", "SUB-Q-001", Arg.Any<string?>(), Arg.Any<DateTime>());
        await _factory.EligibilityService.DidNotReceive().QuickEligibilityCheckAsync(
            "tenant-q-other", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<DateTime>());
    }

    [Fact]
    public async Task ClaimSubmissionDemo_BodyTenant_IsIgnored()
    {
        using var alpha = ClientFor("tenant-body-alpha");
        var submit = await alpha.PostAsJsonAsync("/api/dev/gateway/claims", new
        {
            tenantId = "tenant-body-beta",
            claimId = "CLM-BODY-TENANT-1",
            claimType = "Professional",
            frequencyCode = "1",
            payerId = "60054",
            placeOfServiceCode = "11",
            totalCharge = 10,
            billingProvider = new { npi = "1999999984" },
            subscriber = new { memberId = "U7777788888" },
            serviceLines = new[] { new { lineNumber = 1, procedureCode = "90837", units = 1, chargeAmount = 10 } }
        });
        submit.EnsureSuccessStatusCode();
        using var submitDoc = JsonDocument.Parse(await submit.Content.ReadAsStringAsync());
        var transmissionId = submitDoc.RootElement.GetProperty("result").GetProperty("transmissionId").GetString();

        var tx = await alpha.GetAsync($"/api/dev/gateway/transmissions/{transmissionId}");

        Assert.Equal(HttpStatusCode.OK, tx.StatusCode);
        using var txDoc = JsonDocument.Parse(await tx.Content.ReadAsStringAsync());
        Assert.Equal("tenant-body-alpha", txDoc.RootElement.GetProperty("tenantId").GetString());
    }

    [Fact]
    public async Task EligibilityGatewayDemo_BodyTenant_IsIgnored()
    {
        using var alpha = ClientFor("tenant-elig-alpha");

        var response = await alpha.PostAsJsonAsync("/api/gateway-demo/eligibility", new
        {
            tenantId = "tenant-elig-beta",
            payerId = "60054",
            subscriberId = "U7777788888",
            subscriberFirstName = "John",
            subscriberLastName = "Anon",
            providerNpi = "1999999984"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("tenant-elig-alpha", body);
        Assert.DoesNotContain("tenant-elig-beta", body);
    }

    // ── Dev gateway surfaces do not cross tenants ───────────────────────

    [Fact]
    public async Task AcknowledgmentDemo_OtherTenant_SeesNothing()
    {
        using var alpha = ClientFor("tenant-ack-alpha");
        var transmissionId = await SubmitClaimAsync(alpha, "tenant-ack-alpha", "CLM-ACK-ISO-1");
        (await alpha.PostAsJsonAsync($"/api/dev/gateway/claims/{transmissionId}/277ca",
            new { acknowledgmentId = "ack-iso-1", status = "Accepted" })).EnsureSuccessStatusCode();

        using var beta = ClientFor("tenant-ack-beta");

        var tx = await beta.GetAsync($"/api/dev/gateway/transmissions/{transmissionId}");
        var acks = await beta.GetAsync($"/api/dev/gateway/acknowledgments?transmissionId={transmissionId}");
        var inject = await beta.PostAsJsonAsync($"/api/dev/gateway/claims/{transmissionId}/277ca",
            new { acknowledgmentId = "ack-iso-2", status = "Rejected" });

        Assert.Equal(HttpStatusCode.NotFound, tx.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, acks.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, inject.StatusCode);

        // The owner still sees exactly its own acknowledgment.
        var own = await alpha.GetAsync($"/api/dev/gateway/acknowledgments?transmissionId={transmissionId}");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        using var ownDoc = JsonDocument.Parse(await own.Content.ReadAsStringAsync());
        Assert.Equal(1, ownDoc.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task AttachmentDemoList_OtherTenant_IsNotFound()
    {
        using var alpha = ClientFor("tenant-att-alpha");
        var transmissionId = await SubmitClaimAsync(alpha, "tenant-att-alpha", "CLM-ATT-ISO-1");

        using var beta = ClientFor("tenant-att-beta");
        var response = await beta.GetAsync($"/api/dev/gateway/claims/{transmissionId}/attachments");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var own = await alpha.GetAsync($"/api/dev/gateway/claims/{transmissionId}/attachments");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    // ── Permissions ─────────────────────────────────────────────────────

    [Fact]
    public async Task RoleWithoutEligibilityCheck_CannotReadOrInquire()
    {
        using var finance = ClientFor("tenant-a", ChoRolePermissions.Finance);

        var check = await finance.GetAsync("/api/eligibility/check?subscriberId=SUB-001");
        var inquiry = await finance.PostAsJsonAsync("/api/eligibility/inquiry", new { subscriberId = "SUB-001" });

        Assert.Equal(HttpStatusCode.Forbidden, check.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, inquiry.StatusCode);
    }

    [Fact]
    public async Task EligibilityCheckRole_CanSendReadOnlyPostInquiries()
    {
        using var memberServices = ClientFor("tenant-a", ChoRolePermissions.MemberServices);

        var inquiry = await memberServices.PostAsJsonAsync("/api/eligibility/inquiry", new { subscriberId = "SUB-001" });
        var validate = await memberServices.PostAsJsonAsync("/api/eligibility/validate-auth",
            new { subscriberId = "SUB-001", serviceTypeCode = "30" });
        var x12 = await memberServices.PostAsync("/api/eligibility/270",
            new StringContent("not x12", Encoding.UTF8, "text/plain"));
        var batch = await memberServices.PostAsync("/api/v1/eligibility/batch",
            new StringContent("subscriberId\n", Encoding.UTF8, "text/csv"));
        var payer = await memberServices.PostAsJsonAsync("/api/dev/payer/eligibility", new { payerId = "60054" });
        var payerX12 = await memberServices.PostAsync("/api/dev/payer/eligibility/x12",
            new StringContent("not x12", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.OK, inquiry.StatusCode);
        Assert.Equal(HttpStatusCode.OK, validate.StatusCode);
        foreach (var response in new[] { x12, batch, payer, payerX12 })
        {
            Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task EligibilityCheckRole_CannotRunPayerDirectorySync()
    {
        using var memberServices = ClientFor("tenant-a", ChoRolePermissions.MemberServices);

        var response = await memberServices.PostAsync("/api/payer-references/sync", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task<string> SubmitClaimAsync(HttpClient client, string tenantId, string claimId)
    {
        var submit = await client.PostAsJsonAsync("/api/dev/gateway/claims", new
        {
            tenantId, // matches the token tenant
            claimId,
            claimType = "Professional",
            frequencyCode = "1",
            payerId = "60054",
            placeOfServiceCode = "11",
            totalCharge = 10,
            billingProvider = new { npi = "1999999984" },
            subscriber = new { memberId = "U7777788888" },
            serviceLines = new[] { new { lineNumber = 1, procedureCode = "90837", units = 1, chargeAmount = 10 } }
        });
        submit.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await submit.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("result").GetProperty("transmissionId").GetString()!;
    }
}
