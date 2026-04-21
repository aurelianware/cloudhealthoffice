using System.Net;
using System.Net.Http.Json;
using IdCardService.Adapters;
using IdCardService.Models;
using IdCardService.Repositories;
using IdCardService.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ClearExtensions;

namespace CloudHealthOffice.IdCardService.Tests;

/// <summary>
/// End-to-end coverage for the order → PDF → DocumentReference flow. The
/// WebApplicationFactory replaces upstream HTTP clients with NSubstitute
/// fakes so we can assert on the exact multipart upload that would land in
/// member-document-service without talking to one.
/// </summary>
public class OrderToDocumentFlowTests : IClassFixture<OrderToDocumentFlowTests.Factory>
{
    private readonly Factory _factory;
    private readonly HttpClient _client;

    public OrderToDocumentFlowTests(Factory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", TestFixtures.TenantId);
    }

    [Fact]
    public async Task PostOrder_IssuesCard_UploadsPdf_CreatesRecord()
    {
        _factory.ResetSubstitutes();

        _factory.Member.GetAsync(TestFixtures.TenantId, TestFixtures.MemberId, Arg.Any<CancellationToken>())
            .Returns(new MemberDto
            {
                MemberId = TestFixtures.MemberId,
                FirstName = "Jane",
                LastName = "Doe"
            });
        _factory.Coverage.GetActiveAsync(TestFixtures.TenantId, TestFixtures.MemberId, Arg.Any<CancellationToken>())
            .Returns(new CoverageDto
            {
                GroupNumber = TestFixtures.GroupNumber,
                PlanId = TestFixtures.PlanId,
                Status = 1
            });
        _factory.Sponsor.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SponsorDto { EmployerName = "Acme" });
        _factory.Plans.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new BenefitPlanDto { PlanName = "Gold HMO" });
        _factory.Documents.UploadPdfAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("doc-pdf-123");
        _factory.Documents.UploadPngAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("doc-png-123");

        // Seed the global template so resolution succeeds.
        var templates = _factory.Services.GetRequiredService<IIdCardTemplateRepository>();
        await templates.UpsertAsync(TestFixtures.GlobalDefault());

        var response = await _client.PostAsJsonAsync("/api/v1/id-cards/orders", new
        {
            memberId = TestFixtures.MemberId,
            channel = "Digital"
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IdCardOrderResponse>();
        Assert.NotNull(body);
        Assert.Equal("Issued", body!.Status);
        Assert.Equal("doc-pdf-123", body.DocumentId);
        Assert.NotNull(body.CardId);

        // Assert the document upload was issued with the IdCard category.
        await _factory.Documents.Received().UploadPdfAsync(
            TestFixtures.TenantId,
            TestFixtures.MemberId,
            Arg.Is<byte[]>(b => b.Length > 0 && b[0] == 0x25 /* %PDF */),
            Arg.Is<string>(n => n.EndsWith(".pdf")),
            "IdCard",
            Arg.Any<string?>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());

        // And the order is retrievable via GET.
        var statusResponse = await _client.GetAsync($"/api/v1/id-cards/{body.OrderId}");
        statusResponse.EnsureSuccessStatusCode();
        var status = await statusResponse.Content.ReadFromJsonAsync<IdCardOrderResponse>();
        Assert.Equal("Issued", status!.Status);

        // History endpoint returns the issued card.
        var historyResp = await _client.GetAsync($"/api/v1/members/{TestFixtures.MemberId}/id-cards");
        historyResp.EnsureSuccessStatusCode();
        var history = await historyResp.Content.ReadFromJsonAsync<List<IdCardHistoryEntry>>();
        Assert.NotNull(history);
        Assert.Contains(history!, e => e.CardId == body.CardId);
    }

    [Fact]
    public async Task Scan_WithValidQr_ReturnsEligibilitySnapshot()
    {
        _factory.ResetSubstitutes();

        // Issue a card first.
        _factory.Member.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new MemberDto { MemberId = TestFixtures.MemberId });
        _factory.Coverage.GetActiveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new CoverageDto { GroupNumber = "g", PlanId = "p", Status = 1 });
        _factory.Documents.UploadPdfAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("doc-scan-1");
        _factory.Eligibility.GetSnapshotAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new { active = true, planId = "p", sample = 271 });

        var templates = _factory.Services.GetRequiredService<IIdCardTemplateRepository>();
        await templates.UpsertAsync(TestFixtures.GlobalDefault());

        var orderResp = await _client.PostAsJsonAsync("/api/v1/id-cards/orders", new
        {
            memberId = TestFixtures.MemberId,
            channel = "Digital"
        });
        orderResp.EnsureSuccessStatusCode();
        var order = await orderResp.Content.ReadFromJsonAsync<IdCardOrderResponse>();
        Assert.Equal("Issued", order!.Status);

        // Reconstruct the QR payload using the same signing service the
        // adapter used — provides a realistic scan input.
        var qrService = _factory.Services.GetRequiredService<IQrCodeService>();
        var (_, qrPayload, _, _) = await qrService.GenerateAsync(
            TestFixtures.TenantId, TestFixtures.MemberId, order.CardId!, order.IssuedAt!.Value);

        var scanResp = await _client.PostAsJsonAsync("/api/v1/id-cards/scan", new
        {
            qrPayload,
            providerNpi = "1234567890"
        });

        // Note: because the adapter's cardId is generated inside IssueAsync
        // and the record uses that cardId, re-signing with a fresh call here
        // with the same cardId + issuedAt produces a valid signature (the
        // key is the same). In production the scanner reads the card's own
        // QR, so this is semantically equivalent.
        Assert.Equal(HttpStatusCode.OK, scanResp.StatusCode);
        var scanBody = await scanResp.Content.ReadFromJsonAsync<QrScanResponse>();
        Assert.NotNull(scanBody);
        Assert.Equal(TestFixtures.MemberId, scanBody!.MemberId);
        Assert.Equal(order.CardId, scanBody.CardId);
        Assert.True(scanBody.CardActive);
        Assert.True(scanBody.CoverageActive);
        Assert.NotNull(scanBody.EligibilitySnapshot);
    }

    public class Factory : WebApplicationFactory<Program>
    {
        public IMemberClient Member { get; private set; } = Substitute.For<IMemberClient>();
        public ICoverageClient Coverage { get; private set; } = Substitute.For<ICoverageClient>();
        public ISponsorClient Sponsor { get; private set; } = Substitute.For<ISponsorClient>();
        public IBenefitPlanClient Plans { get; private set; } = Substitute.For<IBenefitPlanClient>();
        public IMemberDocumentClient Documents { get; private set; } = Substitute.For<IMemberDocumentClient>();
        public IEligibilityClient Eligibility { get; private set; } = Substitute.For<IEligibilityClient>();

        public void ResetSubstitutes()
        {
            Member.ClearSubstitute();
            Coverage.ClearSubstitute();
            Sponsor.ClearSubstitute();
            Plans.ClearSubstitute();
            Documents.ClearSubstitute();
            Eligibility.ClearSubstitute();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((ctx, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["IdCard:SigningKeySecretPrefix"] = "idcard-signing-key",
                    ["IdCard:CurrentKeyVersion"] = "v1",
                    ["IdCard:AcceptedKeyVersions:0"] = "v1",
                    ["IdCard:DevSigningKeys:v1"] = "dev-key-v1-bytes-for-hmac-signing",
                    ["MongoDb:ConnectionString"] = "",
                    ["CosmosDb:ConnectionString"] = "",
                    ["ProviderJwt:Authority"] = "",
                    ["IdCard:Qr:PixelsPerModule"] = "4"
                });
            });

            builder.ConfigureServices(services =>
            {
                void Replace<T>(T impl) where T : class
                {
                    var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
                    foreach (var d in descriptors) services.Remove(d);
                    services.AddSingleton(impl);
                }

                Replace(Member);
                Replace(Coverage);
                Replace(Sponsor);
                Replace(Plans);
                Replace(Documents);
                Replace(Eligibility);
            });
        }
    }
}

