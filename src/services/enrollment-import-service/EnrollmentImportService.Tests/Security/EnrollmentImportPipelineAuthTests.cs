using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using CloudHealthOffice.Infrastructure.Security;
using EnrollmentImportService.Clients;
using EnrollmentImportService.Controllers;
using EnrollmentImportService.HostedServices;
using EnrollmentImportService.Models;
using EnrollmentImportService.Repositories;
using EnrollmentImportService.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EnrollmentImportService.Tests.Security;

/// <summary>
/// The real enrollment-import-service pipeline. Every caller needs a CHO token;
/// the tenant and the importing user come from that token, never from
/// X-Tenant-ID, X-Actor-ID or the request body.
/// </summary>
public class EnrollmentImportPipelineAuthTests : IClassFixture<EnrollmentImportPipelineAuthTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<EnrollmentController>
    {
        public Mock<IEnrollmentImportService> Import { get; } = new();
        public Mock<IEnrollmentImportRunRepository> Runs { get; } = new();
        public Mock<IEnrollmentTransactionRepository> Transactions { get; } = new();
        public Mock<IEnrollmentEventRepository> Events { get; } = new();
        public Mock<IPlanCodeGapReportService> GapReport { get; } = new();
        public CapturingHandler CoverageOutbound { get; } = new();
        public CapturingHandler MemberOutbound { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            // Fake host: the MongoClient constructor succeeds and no I/O runs,
            // because every Mongo-backed service is replaced below.
            builder.UseSetting("MongoDb:ConnectionString", "mongodb://fake-host:27017");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEnrollmentImportService>();
                services.AddSingleton(_ => Import.Object);
                services.RemoveAll<IEnrollmentImportRunRepository>();
                services.AddSingleton(_ => Runs.Object);
                services.RemoveAll<IEnrollmentTransactionRepository>();
                services.AddSingleton(_ => Transactions.Object);
                services.RemoveAll<IEnrollmentEventRepository>();
                services.AddSingleton(_ => Events.Object);
                services.RemoveAll<IPlanCodeGapReportService>();
                services.AddSingleton(_ => GapReport.Object);

                var initializer = services.Single(d => d.ImplementationType == typeof(EnrollmentIndexInitializer));
                services.Remove(initializer);

                services.AddHttpClient(HttpCoverageServiceClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => CoverageOutbound);
                services.AddHttpClient(HttpMemberServiceClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => MemberOutbound);
            });
        }
    }

    public sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { }) });
        }
    }

    private const string Tenant = "tenant-1";
    private const string OtherTenant = "tenant-2";
    private const string User = "enrollment-user-7";
    private readonly Factory _factory;

    public EnrollmentImportPipelineAuthTests(Factory factory)
    {
        _factory = factory;
        _factory.Import.Reset();
        _factory.Runs.Reset();
        _factory.Transactions.Reset();
        _factory.Events.Reset();
        _factory.Import.Setup(s => s.ImportEnrollmentAsync(It.IsAny<Enrollment834>(), It.IsAny<string>()))
            .ReturnsAsync(new ImportResult { BatchId = "B-1" });
        _factory.Runs.Setup(r => r.ListRecentAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<EnrollmentImportRun>());
        _factory.Transactions.Setup(r => r.ListRecentAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<EnrollmentTransaction>());
    }

    private HttpClient Client(string tenant, params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(User, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    private static Enrollment834 Batch() => new()
    {
        FileName = "employer.834",
        TransactionCount = 1,
        Enrollments = new()
        {
            new MemberEnrollment
            {
                SubscriberId = "M-001",
                MaintenanceType = "021",
                BenefitStatus = "A",
                Relationship = "18",
                EnrollmentDate = "2026-01-01",
                Demographics = new Demographics { FirstName = "Jane", LastName = "Doe" }
            }
        }
    };

    private void VerifyNothingRan()
    {
        _factory.Import.VerifyNoOtherCalls();
        _factory.Runs.VerifyNoOtherCalls();
        _factory.Transactions.VerifyNoOtherCalls();
        _factory.Events.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NoToken_IsRejected()
    {
        var response = await _factory.CreateClient().GetAsync("/api/v1/enrollment/import-runs");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        VerifyNothingRan();
    }

    // Before: every action took the tenant from this header with no authentication.
    [Theory]
    [InlineData("GET", "/api/v1/enrollment/import-runs")]
    [InlineData("GET", "/api/v1/enrollment/transactions?memberId=M-001")]
    [InlineData("GET", "/api/v1/enrollment/transactions/recent")]
    [InlineData("GET", "/api/v1/members/M-001/enrollment-events")]
    [InlineData("POST", "/api/v1/enrollment/import")]
    [InlineData("POST", "/api/v1/enrollment/plan-code-gap-report")]
    [InlineData("POST", "/api/v1/enrollments/manual")]
    public async Task TenantHeaderWithoutToken_IsRejected(string method, string path)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
            request.Content = JsonContent.Create(Batch());

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        VerifyNothingRan();
    }

    [Fact]
    public async Task TenantHeaderDisagreeingWithToken_IsForbidden()
    {
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", ChoDevelopmentAuth.UserToken(Tenant, ChoRolePermissions.EnrollmentSpecialist));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", OtherTenant);

        var response = await client.GetAsync("/api/v1/enrollment/transactions/recent");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        VerifyNothingRan();
    }

    [Fact]
    public async Task Reads_AreServedFromTokenTenant()
    {
        var client = Client(Tenant, ChoRolePermissions.EnrollmentSpecialist);

        (await client.GetAsync("/api/v1/enrollment/import-runs?limit=5")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/enrollment/transactions/recent?limit=7")).StatusCode.Should().Be(HttpStatusCode.OK);

        _factory.Runs.Verify(r => r.ListRecentAsync(Tenant, 5), Times.Once);
        _factory.Transactions.Verify(r => r.ListRecentAsync(Tenant, 7), Times.Once);
    }

    [Fact]
    public async Task Import_RunsInTokenTenant_AsTokenSubject_IgnoringBodyActor()
    {
        Enrollment834? seen = null;
        string? seenTenant = null;
        _factory.Import.Setup(s => s.ImportEnrollmentAsync(It.IsAny<Enrollment834>(), It.IsAny<string>()))
            .Callback((Enrollment834 b, string t) => { seen = b; seenTenant = t; })
            .ReturnsAsync(new ImportResult { BatchId = "B-1" });

        // A body that tries to claim another actor: ActorId is never bound from JSON.
        var body = new Dictionary<string, object?>
        {
            ["fileName"] = "employer.834",
            ["actorId"] = "someone-else",
            ["enrollments"] = Batch().Enrollments
        };
        var response = await Client(Tenant, ChoRolePermissions.EnrollmentSpecialist)
            .PostAsJsonAsync("/api/v1/enrollment/import", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        seenTenant.Should().Be(Tenant);
        seen!.ActorId.Should().Be(User);
    }

    [Fact]
    public async Task ManualEnrollment_TakesActorFromToken_NotFromActorHeader()
    {
        Enrollment834? seen = null;
        string? seenTenant = null;
        _factory.Import.Setup(s => s.ImportEnrollmentAsync(It.IsAny<Enrollment834>(), It.IsAny<string>()))
            .Callback((Enrollment834 b, string t) => { seen = b; seenTenant = t; })
            .ReturnsAsync(new ImportResult { BatchId = "B-1" });

        var client = Client(Tenant, ChoRolePermissions.EnrollmentSpecialist);
        client.DefaultRequestHeaders.Add("X-Actor-ID", "spoofed-actor");

        var response = await client.PostAsJsonAsync("/api/v1/enrollments/manual", Batch().Enrollments[0]);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        seenTenant.Should().Be(Tenant);
        seen!.ActorId.Should().Be(User);
        seen.FileName.Should().Be($"manual:{User}");
    }

    [Fact]
    public async Task RoleWithoutEnrollmentRead_IsForbidden()
    {
        // Finance holds no enrollment permission.
        var response = await Client(Tenant, ChoRolePermissions.Finance).GetAsync("/api/v1/enrollment/import-runs");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        VerifyNothingRan();
    }

    [Fact]
    public async Task ReadOnlyRole_CanRead()
    {
        // ComplianceOfficer holds *:read, so enrollment:read but not enrollment:process.
        var response = await Client(Tenant, ChoRolePermissions.ComplianceOfficer).GetAsync("/api/v1/enrollment/import-runs");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Runs.Verify(r => r.ListRecentAsync(Tenant, 100), Times.Once);
    }

    [Fact]
    public async Task ReadOnlyRole_CannotImport()
    {
        var client = Client(Tenant, ChoRolePermissions.ComplianceOfficer);

        var import = await client.PostAsJsonAsync("/api/v1/enrollment/import", Batch());
        var manual = await client.PostAsJsonAsync("/api/v1/enrollments/manual", Batch().Enrollments[0]);

        import.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        manual.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        VerifyNothingRan();
    }

    [Theory]
    [InlineData(ChoRolePermissions.ComplianceOfficer)] // *:read only
    [InlineData(ChoRolePermissions.EnrollmentSpecialist)]
    public async Task PlanCodeGapReport_NeedsOnlyEnrollmentRead(string role)
    {
        _factory.GapReport.Setup(g => g.BuildReportAsync(It.IsAny<Enrollment834>(), Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanCodeGapReport());

        var response = await Client(Tenant, role).PostAsJsonAsync("/api/v1/enrollment/plan-code-gap-report", Batch());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PlanCodeGapReport_RoleWithoutEnrollmentRead_IsForbidden()
    {
        var response = await Client(Tenant, ChoRolePermissions.Finance)
            .PostAsJsonAsync("/api/v1/enrollment/plan-code-gap-report", Batch());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task HealthProbes_NeedNoToken()
    {
        var probe = await _factory.CreateClient().GetAsync("/health/live");
        var echo = await _factory.CreateClient().GetAsync("/api/v1/enrollment/health");

        probe.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        probe.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        echo.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static void ShouldCarryServiceToken(HttpRequestMessage? sent, string tenant)
    {
        sent.Should().NotBeNull();
        sent!.Headers.Authorization.Should().NotBeNull();
        sent.Headers.Authorization!.Scheme.Should().Be("Bearer");
        var token = new JwtSecurityTokenHandler().ReadJwtToken(sent.Headers.Authorization.Parameter);
        token.Claims.Should().Contain(c => c.Type == ChoClaimTypes.TenantId && c.Value == tenant);
        token.Subject.Should().Be("enrollment-import-service");
    }

    [Fact]
    public async Task CoverageServiceCall_WithoutCaller_CarriesServiceTokenForNamedTenant()
    {
        // A batch/background import has no inbound request. coverage-service
        // rejects untokened calls, so the real client must get a service token
        // minted for the tenant it names in X-Tenant-ID.
        var coverage = _factory.Services.GetRequiredService<ICoverageServiceClient>();

        await coverage.CreateAsync(Tenant, new CreateCoverageRequestDto { MemberId = "M-001" });

        ShouldCarryServiceToken(_factory.CoverageOutbound.Last, Tenant);
        _factory.CoverageOutbound.Last!.RequestUri!.Host.Should().Be("coverage-service");
    }

    [Fact]
    public async Task CoverageReinstatement_PostsToCoverageServiceReinstate_WithServiceToken()
    {
        var coverage = _factory.Services.GetRequiredService<ICoverageServiceClient>();

        await coverage.ReinstateAsync(Tenant, "cov-9", "41");

        var sent = _factory.CoverageOutbound.Last!;
        sent.Method.Should().Be(HttpMethod.Post);
        sent.RequestUri!.PathAndQuery.Should().Be("/api/v1/coverage/cov-9/reinstate?reasonCode=41");
        ShouldCarryServiceToken(sent, Tenant);
    }

    [Fact]
    public async Task MemberServiceCall_WithoutCaller_CarriesServiceTokenForNamedTenant()
    {
        var members = _factory.Services.GetRequiredService<IMemberServiceClient>();

        await members.ExistsAsync(OtherTenant, "M-001");

        ShouldCarryServiceToken(_factory.MemberOutbound.Last, OtherTenant);
    }
}
