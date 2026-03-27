using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class MemberServiceTests
{
    private readonly Mock<ILogger<MemberService>> _logger = new();
    private readonly IConfiguration _configuration;

    public MemberServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:MemberService"] = "http://localhost:5001"
            })
            .Build();
    }

    private MemberService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new MemberService(httpClient, _configuration, _logger.Object);
    }

    // ── SearchMembersAsync ──

    [Fact]
    public async Task SearchMembersAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.SearchMembersAsync("Smith"));
        ex.ServiceName.Should().Be("Member Service");
    }

    // ── GetMemberByIdAsync ──

    [Fact]
    public async Task GetMemberByIdAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetMemberByIdAsync("MBR-8201"));
        ex.ServiceName.Should().Be("Member Service");
    }

    // ── GetMemberPcpAsync ──

    [Fact]
    public async Task GetMemberPcpAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetMemberPcpAsync("MBR-8201"));
        ex.ServiceName.Should().Be("Member Service");
    }

    // ── AssignPcpAsync ──

    [Fact]
    public async Task AssignPcpAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.AssignPcpAsync(new AssignPcpRequest()));
        ex.ServiceName.Should().Be("Member Service");
    }

    // ── GetCoverageHistoryAsync ──

    [Fact]
    public async Task GetCoverageHistoryAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetCoverageHistoryAsync("MBR-8201"));
        ex.ServiceName.Should().Be("Member Service");
    }

    // ── GetMember834TransactionsAsync ──

    [Fact]
    public async Task GetMember834TransactionsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetMember834TransactionsAsync("MBR-8201"));
        ex.ServiceName.Should().Be("Member Service");
    }

    // ── TerminateEnrollmentAsync ──

    [Fact]
    public async Task TerminateEnrollmentAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.TerminateEnrollmentAsync(new TerminateEnrollmentRequest()));
        ex.ServiceName.Should().Be("Member Service");
    }

    [Fact]
    public async Task SearchMembersAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.SearchMembersAsync("Smith"));
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }
}
