using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.EligibilityService.Tests;

public class GatewayClaimAttachmentDemoTests : IClassFixture<EligibilityApiFactory>
{
    private readonly EligibilityApiFactory _factory;
    private readonly HttpClient _client;

    public GatewayClaimAttachmentDemoTests(EligibilityApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-alpha");
    }

    [Fact]
    public async Task DevelopmentEndpoint_StoresAndSubmitsSyntheticPdf()
    {
        var submit = await _client.PostAsJsonAsync("/api/dev/gateway/claims", new
        {
            tenantId = "tenant-alpha",
            claimId = "CLM-ATT-1001",
            claimType = "Professional",
            frequencyCode = "1",
            payerId = "60054",
            placeOfServiceCode = "11",
            totalCharge = 10,
            billingProvider = new { npi = "1999999984", organizationName = "Therapy Associates" },
            subscriber = new { memberId = "U7777788888", firstName = "John", lastName = "Anon" },
            serviceLines = new[]
            {
                new { lineNumber = 1, procedureCode = "90837", units = 1, chargeAmount = 10 }
            }
        });
        submit.EnsureSuccessStatusCode();
        using var submitDoc = JsonDocument.Parse(await submit.Content.ReadAsStringAsync());
        var transmissionId = submitDoc.RootElement.GetProperty("result").GetProperty("transmissionId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(transmissionId));

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("%PDF-1.4 synthetic"u8.ToArray())
        {
            Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf") }
        }, "file", "note.pdf");
        form.Add(new StringContent("ClinicalNote"), "attachmentType");
        form.Add(new StringContent("application/pdf"), "contentType");
        form.Add(new StringContent("att-dev-1"), "attachmentId");

        var response = await _client.PostAsync($"/api/dev/gateway/claims/{transmissionId}/attachments", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("isSuccess").GetBoolean());
        Assert.Equal(
            nameof(ClaimAttachmentTransmissionStatus.GatewayAccepted),
            doc.RootElement.GetProperty("result").GetProperty("status").GetString());
        Assert.Equal("att-dev-1", doc.RootElement.GetProperty("result").GetProperty("attachmentId").GetString());

        var tx = await _client.GetAsync($"/api/dev/gateway/transmissions/{transmissionId}");
        tx.EnsureSuccessStatusCode();
        using var txDoc = JsonDocument.Parse(await tx.Content.ReadAsStringAsync());
        Assert.Equal(
            nameof(GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway),
            txDoc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task TenantMismatchHeader_IsRejected()
    {
        var submit = await _client.PostAsJsonAsync("/api/dev/gateway/claims", new
        {
            tenantId = "tenant-alpha",
            claimId = "CLM-ATT-1002",
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

        using var other = _factory.CreateClient();
        other.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-beta");
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("%PDF-1.4 synthetic"u8.ToArray()), "file", "note.pdf");
        form.Add(new StringContent("application/pdf"), "contentType");
        var response = await other.PostAsync($"/api/dev/gateway/claims/{transmissionId}/attachments", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OversizedFile_ReturnsBadRequest()
    {
        var submit = await _client.PostAsJsonAsync("/api/dev/gateway/claims", new
        {
            tenantId = "tenant-alpha",
            claimId = "CLM-ATT-1003",
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

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(new byte[64])
        {
            Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf") }
        }, "file", "big.pdf");
        form.Add(new StringContent("application/pdf"), "contentType");
        var response = await _client.PostAsync($"/api/dev/gateway/claims/{transmissionId}/attachments", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("maximum size", body, StringComparison.OrdinalIgnoreCase);
    }
}
