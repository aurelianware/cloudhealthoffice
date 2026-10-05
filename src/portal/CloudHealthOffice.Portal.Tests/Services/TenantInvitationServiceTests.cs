using System.Net;
using System.Text;
using System.Text.Json;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Portal.Tests.Services;

/// <summary>The invite UI's calls reach the right tenant-service endpoints.</summary>
public class TenantInvitationServiceTests
{
    private const string Base = "http://tenant-service.test/api";

    private static readonly string IssuedJson = JsonSerializer.Serialize(new
    {
        invitation = new { id = "inv-1", userId = "user-9", email = "pat@partner.example", status = "Pending", roles = new[] { "ClaimsExaminer" }, expiresAt = "2026-10-10T00:00:00Z" },
        code = "CODE-ONCE",
        redemptionUrl = "https://portal.example/invite/CODE-ONCE",
    });

    private static (TenantInvitationService Service, FakeHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Services:TenantService"] = Base })
            .Build();
        return (new TenantInvitationService(new HttpClient(handler), configuration, NullLogger<TenantInvitationService>.Instance), handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Create_PostsToTheTenantsInvitations_AndReturnsTheOneTimeCode()
    {
        string? sent = null;
        var (service, handler) = Create(request =>
        {
            sent = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(HttpStatusCode.Created, IssuedJson);
        });

        var result = await service.CreateAsync("tenant-a", new InviteUserRequest
        {
            Email = "pat@partner.example",
            DisplayName = "Pat",
            Roles = new List<string> { "ClaimsExaminer" },
            Department = "Claims",
        });

        result.Succeeded.Should().BeTrue();
        result.Value!.Code.Should().Be("CODE-ONCE");
        result.Value.Invitation.Id.Should().Be("inv-1");
        var request = handler.CapturedRequests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.AbsoluteUri.Should().Be($"{Base}/v1/tenants/tenant-a/invitations");
        var body = JsonDocument.Parse(sent!).RootElement;
        body.GetProperty("email").GetString().Should().Be("pat@partner.example");
        body.GetProperty("roles")[0].GetString().Should().Be("ClaimsExaminer");
        body.GetProperty("department").GetString().Should().Be("Claims");
        body.TryGetProperty("azureAdObjectId", out _).Should().BeFalse();
    }

    [Fact]
    public async Task List_GetsTheTenantsInvitations()
    {
        var (service, handler) = Create(_ => Json(HttpStatusCode.OK,
            """[{"id":"inv-1","email":"a@b.example","status":"Pending","roles":["ClaimsExaminer"]},{"id":"inv-2","email":"c@d.example","status":"Expired","roles":[]}]"""));

        var list = await service.ListAsync("tenant-a");

        list.Select(i => i.Status).Should().Equal("Pending", "Expired");
        handler.CapturedRequests.Single().Method.Should().Be(HttpMethod.Get);
        handler.CapturedUrls.Single().Should().Be($"{Base}/v1/tenants/tenant-a/invitations");
    }

    [Fact]
    public async Task Resend_Revoke_AndUnlink_PostToTheirEndpoints()
    {
        var (service, handler) = Create(request => request.RequestUri!.AbsolutePath.EndsWith("/resend")
            ? Json(HttpStatusCode.OK, IssuedJson)
            : Json(HttpStatusCode.OK, """{"id":"inv-1","status":"Revoked"}"""));

        (await service.ResendAsync("tenant-a", "inv-1")).Value!.Code.Should().Be("CODE-ONCE");
        (await service.RevokeAsync("tenant-a", "inv-1")).Value!.Status.Should().Be("Revoked");
        (await service.UnlinkUserAsync("tenant-a", "user-1")).Succeeded.Should().BeTrue();

        handler.CapturedRequests.Should().OnlyContain(r => r.Method == HttpMethod.Post);
        handler.CapturedUrls.Should().Equal(
            $"{Base}/v1/tenants/tenant-a/invitations/inv-1/resend",
            $"{Base}/v1/tenants/tenant-a/invitations/inv-1/revoke",
            $"{Base}/v1/tenants/tenant-a/users/user-1/unlink");
    }

    [Fact]
    public async Task Refusals_CarryTheServicesMessage()
    {
        var (service, _) = Create(_ => Json(HttpStatusCode.Conflict,
            """{"error":"user_exists","message":"This address already belongs to an active or linked user in this tenant."}"""));

        var result = await service.CreateAsync("tenant-a", new InviteUserRequest { Email = "x@y.example", Roles = { "ClaimsExaminer" } });

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("already belongs");
    }
}
