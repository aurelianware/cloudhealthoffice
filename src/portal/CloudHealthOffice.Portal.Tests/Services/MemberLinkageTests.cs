using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

/// <summary>
/// Service-layer coverage for the four new member-linkage methods added to
/// the portal (<see cref="IClaimsService.SearchClaimsByMemberAsync"/>,
/// <see cref="IArService.GetMemberArSummaryAsync"/>,
/// <see cref="IPremiumBillingService.GetMemberPremiumSummaryAsync"/>,
/// <see cref="ISponsorService.GetSponsorMemberViewAsync"/>).
/// Verifies URL construction, 404 → null semantics, and basic deserialization.
/// </summary>
public class MemberLinkageTests
{
    // Mirrors prod appsettings.json: each Services:* base URL includes the
    // version prefix the service mounts its controllers under, so call sites
    // only append the resource segment. Sponsor specifically lives under
    // /api/v1; asserting against that prod-shape base avoids locking in a
    // less-realistic URL in the test.
    private readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:ClaimsService"] = "http://claims-svc/claims",
            ["Services:ArService"] = "http://ar-svc/api",
            ["Services:BillingService"] = "http://billing-svc/api",
            ["Services:SponsorService"] = "http://sponsor-svc/api/v1"
        })
        .Build();

    [Fact]
    public async Task SearchClaimsByMemberAsync_BuildsV1UrlAndForwardsFilters()
    {
        var body = "{\"total\":1,\"page\":1,\"pageSize\":10,\"resources\":[{\"resourceType\":\"ExplanationOfBenefit\"}]}";
        var handler = new FakeHandler(HttpStatusCode.OK, body);
        var sut = new ClaimsService(new HttpClient(handler), _configuration,
            new Mock<ILogger<ClaimsService>>().Object);

        var result = await sut.SearchClaimsByMemberAsync("MEM-7",
            new MemberClaimsFilter
            {
                Status = "Paid",
                ClaimType = "Professional",
                AmountMin = 25m,
                AmountMax = 1000m
            });

        result.Total.Should().Be(1);
        handler.CapturedUrls[0].Should().Contain("/api/v1/claims?memberId=MEM-7");
        handler.CapturedUrls[0].Should().Contain("status=Paid");
        handler.CapturedUrls[0].Should().Contain("claimType=Professional");
        handler.CapturedUrls[0].Should().Contain("amountMin=25");
        handler.CapturedUrls[0].Should().Contain("amountMax=1000");
        // Should strip the redundant /claims suffix from the base URL and call /api/v1/claims at service root
        handler.CapturedUrls[0].Should().NotContain("/claims/api/v1/claims");
    }

    [Fact]
    public async Task GetMemberArSummaryAsync_Returns404AsNull()
    {
        var handler = new FakeHandler(HttpStatusCode.NotFound);
        var sut = new ArServiceImpl(new HttpClient(handler), _configuration,
            new Mock<ILogger<ArServiceImpl>>().Object);

        var result = await sut.GetMemberArSummaryAsync("MEM-missing");
        result.Should().BeNull();
        handler.CapturedUrls[0].Should().Be("http://ar-svc/api/v1/members/MEM-missing/ar-summary");
    }

    [Fact]
    public async Task GetMemberPremiumSummaryAsync_HitsCorrectPath()
    {
        var body = "{\"memberId\":\"MEM-1\",\"autopayEnabled\":true,\"grace\":{\"isInGrace\":false,\"graceType\":\"Standard\",\"daysRemaining\":0}}";
        var handler = new FakeHandler(HttpStatusCode.OK, body);
        var sut = new PremiumBillingService(new HttpClient(handler), _configuration,
            new Mock<ILogger<PremiumBillingService>>().Object);

        var result = await sut.GetMemberPremiumSummaryAsync("MEM-1");
        result!.MemberId.Should().Be("MEM-1");
        result.AutopayEnabled.Should().BeTrue();
        handler.CapturedUrls[0].Should().Be("http://billing-svc/api/v1/members/MEM-1/premium-summary");
    }

    [Fact]
    public async Task GetSponsorMemberViewAsync_HitsCorrectPath()
    {
        var body = "{\"groupNumber\":\"GRP-1\",\"sponsorName\":\"Acme\",\"lineOfBusiness\":\"Commercial\",\"status\":\"Active\"}";
        var handler = new FakeHandler(HttpStatusCode.OK, body);
        var sut = new SponsorService(new HttpClient(handler), _configuration,
            new Mock<ILogger<SponsorService>>().Object);

        var result = await sut.GetSponsorMemberViewAsync("GRP-1");
        result!.GroupNumber.Should().Be("GRP-1");
        handler.CapturedUrls[0].Should().Be("http://sponsor-svc/api/v1/sponsors/GRP-1/member-view");
    }
}
