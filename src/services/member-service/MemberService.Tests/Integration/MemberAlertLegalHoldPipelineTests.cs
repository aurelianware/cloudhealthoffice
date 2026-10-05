using System.Net;
using System.Net.Http.Json;
using CloudHealthOffice.Infrastructure.Security;
using MemberService.Models;
using MemberService.Repositories;
using MemberService.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MemberService.Tests.Integration;

/// <summary>
/// The real pipeline (CHO tokens, default permissions, the action's check):
/// a LitigationHold alert needs records:legal-hold to create or end; other
/// alert types keep members:write. Also: member-service sends no CORS grant
/// (server-to-server only).
/// </summary>
public class MemberAlertLegalHoldPipelineTests : IClassFixture<MemberFhirSmokeTests.Factory>
{
    private const string Tenant = "tenant-int";
    private const string MemberId = "HOLD-001";

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly WebApplicationFactory<Program> _host;
    private readonly InMemoryMemberAlertRepository _alerts = new();

    public MemberAlertLegalHoldPipelineTests(MemberFhirSmokeTests.Factory factory)
    {
        if (!factory.MemberRepo.Members.Any(m => m.MemberId == MemberId && m.TenantId == Tenant))
        {
            factory.MemberRepo.Members.Add(new Member
            {
                TenantId = Tenant,
                MemberId = MemberId,
                FirstName = "Hal",
                LastName = "Hold",
                DateOfBirth = new DateTime(1980, 1, 1),
                EffectiveDate = new DateTime(2024, 1, 1),
                GroupNumber = "GRP",
                IsSubscriber = true,
            });
        }
        _host = factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<IMemberAlertRepository>();
            services.AddSingleton<IMemberAlertRepository>(_alerts);
        }));
    }

    private HttpClient As(string role)
    {
        var client = _host.CreateDefaultClient(new ChoDevelopmentTokenHandler("user-" + role, role));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    private static object Alert(string type) => new
    {
        alertType = type,
        severity = "Critical",
        reason = "Counsel filing pending",
    };

    [Fact]
    public async Task EnrollmentSpecialist_CannotCreateLitigationHold()
    {
        var response = await As(ChoRolePermissions.EnrollmentSpecialist)
            .PostAsJsonAsync($"/api/v1/members/{MemberId}/alerts", Alert("LitigationHold"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _alerts.Alerts.Should().NotContain(a => a.AlertType == MemberAlertType.LitigationHold);
    }

    [Fact]
    public async Task EnrollmentSpecialist_CanCreateOtherAlertTypes()
    {
        var response = await As(ChoRolePermissions.EnrollmentSpecialist)
            .PostAsJsonAsync($"/api/v1/members/{MemberId}/alerts", Alert("LanguageRequirement"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task ComplianceOfficer_CreatesAndEndsLitigationHold_EnrollmentSpecialistCannotEndIt()
    {
        var create = await As(ChoRolePermissions.ComplianceOfficer)
            .PostAsJsonAsync($"/api/v1/members/{MemberId}/alerts", Alert("LitigationHold"));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await create.Content.ReadFromJsonAsync<MemberAlert>(Json);

        var endByEnrollment = await As(ChoRolePermissions.EnrollmentSpecialist)
            .PostAsJsonAsync($"/api/v1/members/{MemberId}/alerts/{created!.Id}/end", new { });
        endByEnrollment.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _alerts.Alerts.Single(a => a.Id == created.Id).EndDate.Should().BeNull();

        var endByCompliance = await As(ChoRolePermissions.ComplianceOfficer)
            .PostAsJsonAsync($"/api/v1/members/{MemberId}/alerts/{created.Id}/end", new { });
        endByCompliance.StatusCode.Should().Be(HttpStatusCode.OK);
        _alerts.Alerts.Single(a => a.Id == created.Id).EndDate.Should().NotBeNull();
    }

    [Fact]
    public async Task ComplianceViewer_CannotCreateLitigationHold()
    {
        // *:read never satisfies a write.
        var response = await As(ChoRolePermissions.ComplianceViewer)
            .PostAsJsonAsync($"/api/v1/members/{MemberId}/alerts", Alert("LitigationHold"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task NoCorsGrant_ForBrowserOrigins()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, $"/api/v1/members/{MemberId}");
        request.Headers.Add("Origin", "https://evil.example");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await _host.CreateClient().SendAsync(request);

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }
}
