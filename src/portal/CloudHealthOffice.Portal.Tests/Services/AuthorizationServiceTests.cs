using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Web;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class AuthorizationServiceTests
{
    private readonly Mock<ILogger<AuthorizationService>> _logger = new();
    private readonly Mock<ITokenAcquisition> _tokenAcquisition = new();
    private readonly IConfiguration _configuration;

    public AuthorizationServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:AuthorizationService"] = "http://localhost:5003"
            })
            .Build();

        _tokenAcquisition
            .Setup(t => t.GetAccessTokenForUserAsync(It.IsAny<IEnumerable<string>>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<System.Security.Claims.ClaimsPrincipal?>(),
                It.IsAny<TokenAcquisitionOptions?>()))
            .ReturnsAsync("fake-token");
    }

    private AuthorizationService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new AuthorizationService(httpClient, _configuration, _logger.Object, _tokenAcquisition.Object);
    }

    // ── GetAuthorizationsAsync ──

    [Fact]
    public async Task GetAuthorizationsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetAuthorizationsAsync());
        ex.ServiceName.Should().Be("Authorization Service");
    }

    [Fact]
    public async Task GetAuthorizationsAsync_WithMemberId_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetAuthorizationsAsync(memberId: "MBR-001"));
        ex.ServiceName.Should().Be("Authorization Service");
    }

    // ── GetAuthorizationByIdAsync ──

    [Fact]
    public async Task GetAuthorizationByIdAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetAuthorizationByIdAsync("AUTH-001"));
        ex.ServiceName.Should().Be("Authorization Service");
    }

    // ── SubmitAuthorizationAsync ──

    [Fact]
    public async Task SubmitAuthorizationAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.SubmitAuthorizationAsync(new SubmitAuthorizationRequest()));
        ex.ServiceName.Should().Be("Authorization Service");
    }

    [Fact]
    public async Task GetAuthorizationsAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetAuthorizationsAsync());
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }

    // ════════════════════════════════════════════════════════════════
    // Happy-path and edge-case tests
    // ════════════════════════════════════════════════════════════════

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // ── GetAuthorizationsAsync ──

    [Fact]
    public async Task GetAuthorizationsAsync_WhenApiReturns200_DeserializesAuthorizationsList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { id = "id-1", authorizationNumber = "AUTH-001",
                  memberId = "MBR-001", patientFirstName = "Jane", patientLastName = "Doe",
                  serviceTypeCode = "MRI", status = 4, // Approved
                  submittedDate = "2026-02-01", requestingProviderName = "Dr. Lee",
                  reviewedDate = "2026-02-02" },
            new { id = "id-2", authorizationNumber = "AUTH-002",
                  memberId = "MBR-002", patientFirstName = "John", patientLastName = "Roe",
                  serviceTypeCode = "Surgery", status = 3, // Pended
                  submittedDate = "2026-03-10", requestingProviderName = "Dr. Kim",
                  reviewedDate = (string?)null }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetAuthorizationsAsync();

        result.Should().HaveCount(2);
        result[0].AuthorizationId.Should().Be("AUTH-001");
        result[0].StatusText.Should().Be("Approved");
        result[0].MemberName.Should().Be("Jane Doe");
        result[1].ServiceType.Should().Be("Surgery");
    }

    [Fact]
    public async Task GetAuthorizationsAsync_WithoutMemberId_UrlHasNoQueryString()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetAuthorizationsAsync();

        handler.CapturedUrls.Should().ContainSingle()
            .Which.Should().EndWith("/authorizations/search");
    }

    [Fact]
    public async Task GetAuthorizationsAsync_WithMemberId_UrlContainsMemberIdFilter()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetAuthorizationsAsync(memberId: "MBR-42");

        handler.CapturedUrls.Should().ContainSingle()
            .Which.Should().Contain("memberId=MBR-42");
    }

    [Fact]
    public async Task GetAuthorizationsAsync_WhenApiReturnsNull_ReturnsEmptyList()
    {
        var sut = CreateService(new HttpClient(
            new FakeHandler(HttpStatusCode.OK, "null")));

        var result = await sut.GetAuthorizationsAsync();

        result.Should().BeEmpty();
    }

    // ── GetAuthorizationByIdAsync ──

    [Fact]
    public async Task GetAuthorizationByIdAsync_WhenApiReturns200_DeserializesAuthorizationDetails()
    {
        var json = JsonSerializer.Serialize(new
        {
            id = "id-100", authorizationNumber = "AUTH-100",
            memberId = "MBR-42", patientFirstName = "Alice", patientLastName = "Wonder",
            serviceTypeCode = "Physical Therapy", status = 4, // Approved
            submittedDate = "2026-01-15", requestingProviderName = "Dr. House",
            reviewedDate = "2026-01-16",
            providerId = "PRV-50",
            diagnosisCode = "M54.5", diagnosisDescription = "Low back pain",
            procedureCode = "97110", procedureDescription = "Therapeutic exercises",
            unitsRequested = 12, unitsApproved = 10,
            serviceStartDate = "2026-02-01", serviceEndDate = "2026-05-01",
            reviewerNotes = "Approved 10 of 12 units"
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetAuthorizationByIdAsync("AUTH-100");

        result.Should().NotBeNull();
        result!.AuthorizationId.Should().Be("AUTH-100");
        result.MemberId.Should().Be("MBR-42");
        result.DiagnosisCode.Should().Be("M54.5");
        result.ProcedureCode.Should().Be("97110");
        result.UnitsRequested.Should().Be(12);
        result.UnitsApproved.Should().Be(10);
        result.ReviewerNotes.Should().Be("Approved 10 of 12 units");
    }

    [Fact]
    public async Task GetAuthorizationByIdAsync_WhenApiReturnsNull_ReturnsNull()
    {
        var sut = CreateService(new HttpClient(
            new FakeHandler(HttpStatusCode.OK, "null")));

        var result = await sut.GetAuthorizationByIdAsync("AUTH-NONE");

        result.Should().BeNull();
    }

    // ── SubmitAuthorizationAsync ──

    [Fact]
    public async Task SubmitAuthorizationAsync_WhenApiReturns200_ExtractsAuthorizationId()
    {
        var json = JsonSerializer.Serialize(
            new { authorizationId = "AUTH-NEW-555" }, JsonOpts);
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.SubmitAuthorizationAsync(new SubmitAuthorizationRequest
        {
            MemberId = "MBR-42", ProviderId = "PRV-50",
            ServiceType = "MRI", DiagnosisCode = "M54.5",
            ProcedureCode = "70553", UnitsRequested = 1,
            ServiceStartDate = new DateTime(2026, 4, 1)
        });

        result.Should().Be("AUTH-NEW-555");
    }

    [Fact]
    public async Task SubmitAuthorizationAsync_WhenResponseMissingAuthorizationId_ReturnsEmptyString()
    {
        var json = JsonSerializer.Serialize(new { other = "value" }, JsonOpts);
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.SubmitAuthorizationAsync(new SubmitAuthorizationRequest());

        result.Should().BeEmpty();
    }
}
