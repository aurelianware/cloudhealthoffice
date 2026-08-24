using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Responders.Directory;
using CloudHealthOffice.Infrastructure.Responders.Models;

namespace CloudHealthOffice.EligibilityService.Tests;

public class PayerClaimAttachmentDevControllerTests : IClassFixture<EligibilityApiFactory>
{
    private readonly HttpClient _client;

    public PayerClaimAttachmentDevControllerTests(EligibilityApiFactory factory)
    {
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "untrusted-tenant");
    }

    [Fact]
    public async Task SyntheticDentalImage_MatchesPendingClaim_WithoutAdjudicating()
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10])
        {
            Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") }
        }, "file", "xray.jpg");
        form.Add(new StringContent(ChoDemoEligibilitySeed.ExternalPayerId), "payerId");
        form.Add(new StringContent("untrusted-tenant"), "claimedTenantId");
        form.Add(new StringContent("DentalImage"), "attachmentType");
        form.Add(new StringContent("image/jpeg"), "contentType");
        form.Add(new StringContent("ext-demo-1"), "externalTransactionId");
        form.Add(new StringContent(ChoDemoClaimAttachmentSeed.AttachmentControlNumber), "attachmentControlNumber");

        var response = await _client.PostAsync(
            $"/api/dev/payer/claims/{ChoDemoClaimAttachmentSeed.ClaimId}/attachments", form);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal(nameof(InboundClaimAttachmentStatus.AvailableToClaim), result.GetProperty("status").GetString());
        Assert.Equal(ChoDemoEligibilitySeed.TenantId, result.GetProperty("tenantId").GetString());
        Assert.Equal(ChoDemoClaimAttachmentSeed.ClaimId, result.GetProperty("claimId").GetString());
        Assert.False(result.GetProperty("claimAdjudicated").GetBoolean());
        Assert.False(result.GetProperty("claimPaid").GetBoolean());
        Assert.True(result.GetProperty("availableToExaminer").GetBoolean());
        Assert.Equal("ClaimId", result.GetProperty("matchingIdentifier").GetString());

        var list = await _client.GetAsync(
            $"/api/dev/payer/claims/{ChoDemoClaimAttachmentSeed.ClaimId}/attachments?payerId={ChoDemoEligibilitySeed.ExternalPayerId}");
        list.EnsureSuccessStatusCode();
        using var listed = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.Equal(1, listed.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task DuplicatePost_IsReplay()
    {
        static MultipartFormDataContent Form()
        {
            var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x11])
            {
                Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") }
            }, "file", "xray.jpg");
            form.Add(new StringContent(ChoDemoEligibilitySeed.ExternalPayerId), "payerId");
            form.Add(new StringContent("image/jpeg"), "contentType");
            form.Add(new StringContent("ext-dup-1"), "externalTransactionId");
            form.Add(new StringContent("acn-dup-1"), "attachmentControlNumber");
            return form;
        }

        var first = await _client.PostAsync(
            $"/api/dev/payer/claims/{ChoDemoClaimAttachmentSeed.ClaimId}/attachments", Form());
        first.EnsureSuccessStatusCode();
        var second = await _client.PostAsync(
            $"/api/dev/payer/claims/{ChoDemoClaimAttachmentSeed.ClaimId}/attachments", Form());
        second.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("result").GetProperty("replay").GetBoolean());
    }
}
