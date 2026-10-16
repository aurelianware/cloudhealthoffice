using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CloudHealthOffice.Infrastructure.Security;
using WorkManagementService.Tests.Support;

namespace WorkManagementService.Tests.Security;

/// <summary>
/// Plan notices and pinned records (Home). Every staff role reads notices
/// and pins its own records; only notices:write (tenant administrators)
/// posts, edits and withdraws. A notice shows inside its window, to its
/// roles; read marks and pins are the caller's own.
/// </summary>
[Collection(nameof(WorkServiceCollection))]
public class WorkspaceApiTests
{
    private const string Admin = ChoRolePermissions.TenantAdmin;
    private const string Examiner = ChoRolePermissions.ClaimsExaminer;
    private const string Finance = ChoRolePermissions.Finance;

    private readonly WorkServiceFactory _factory;

    public WorkspaceApiTests(WorkServiceFactory factory)
    {
        _factory = factory;
        _factory.Reset();
    }

    private static async Task<JsonArray> Array(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!.AsArray();

    private async Task<string> Post(object notice)
    {
        var r = await _factory.Client("admin", Admin).PostAsJsonAsync("/api/v1/workspace/notices", notice);
        r.StatusCode.Should().Be(HttpStatusCode.Created);
        return JsonNode.Parse(await r.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();
    }

    [Fact]
    public async Task Staff_see_live_notices_for_their_roles_important_first()
    {
        await Post(new { title = "Fee schedule effective 11/1", body = "New rates load Friday." });
        await Post(new { title = "Maintenance Saturday", severity = "Important" });
        await Post(new { title = "Payment run moved", targetRoles = new[] { Finance } });
        await Post(new { title = "Next week", startsAt = DateTime.UtcNow.AddDays(7) });
        await Post(new { title = "Old", startsAt = DateTime.UtcNow.AddDays(-9), expiresAt = DateTime.UtcNow.AddDays(-1) });

        var examiner = await Array(await _factory.Client("ex1", Examiner).GetAsync("/api/v1/workspace/notices"));
        var finance = await Array(await _factory.Client("fin1", Finance).GetAsync("/api/v1/workspace/notices"));

        examiner.Select(n => n!["title"]!.GetValue<string>()).Should().Equal("Maintenance Saturday", "Fee schedule effective 11/1");
        finance.Select(n => n!["title"]!.GetValue<string>()).Should().Contain("Payment run moved");
    }

    [Fact]
    public async Task Read_marks_are_per_user()
    {
        var id = await Post(new { title = "Read me" });

        (await _factory.Client("ex1", Examiner).PostAsync($"/api/v1/workspace/notices/{id}/read", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await Array(await _factory.Client("ex1", Examiner).GetAsync("/api/v1/workspace/notices")))[0]!["read"]!.GetValue<bool>().Should().BeTrue();
        (await Array(await _factory.Client("ex2", Examiner).GetAsync("/api/v1/workspace/notices")))[0]!["read"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Only_notices_write_posts_edits_and_withdraws()
    {
        var examiner = _factory.Client("ex1", Examiner);
        (await examiner.PostAsJsonAsync("/api/v1/workspace/notices", new { title = "x" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await examiner.GetAsync("/api/v1/workspace/notices/all")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var id = await Post(new { title = "Typo" });
        (await examiner.PutAsJsonAsync($"/api/v1/workspace/notices/{id}", new { title = "x" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var admin = _factory.Client("admin", Admin);
        (await admin.PutAsJsonAsync($"/api/v1/workspace/notices/{id}", new { title = "Fixed" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.PostAsync($"/api/v1/workspace/notices/{id}/withdraw", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Array(await examiner.GetAsync("/api/v1/workspace/notices"))).Should().BeEmpty();
        var all = await Array(await admin.GetAsync("/api/v1/workspace/notices/all"));
        all[0]!["state"]!.GetValue<string>().Should().Be("Withdrawn");
        all[0]!["title"]!.GetValue<string>().Should().Be("Fixed");
        all[0]!["createdBy"]!.GetValue<string>().Should().Be("admin");
    }

    [Theory]
    [InlineData("", "title")]
    [InlineData("ok", "expire")]
    [InlineData("ok", "role")]
    public async Task Bad_notices_are_refused(string title, string problem)
    {
        object body = problem switch
        {
            "expire" => new { title, startsAt = DateTime.UtcNow, expiresAt = DateTime.UtcNow.AddHours(-1) },
            "role" => new { title, targetRoles = new[] { "Nope" } },
            _ => new { title },
        };
        var r = await _factory.Client("admin", Admin).PostAsJsonAsync("/api/v1/workspace/notices", body);
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Pins_are_the_callers_own()
    {
        var me = _factory.Client("ex1", Examiner);
        (await me.PutAsJsonAsync("/api/v1/workspace/pins", new { kind = "member", recordId = "M1", label = "Ana Reyes" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await me.PutAsJsonAsync("/api/v1/workspace/pins", new { kind = "Claim", recordId = "C9", label = "Claim CLM-9" })).StatusCode.Should().Be(HttpStatusCode.OK);
        // Pinning again is idempotent.
        (await me.PutAsJsonAsync("/api/v1/workspace/pins", new { kind = "Member", recordId = "M1", label = "Ana Reyes" })).StatusCode.Should().Be(HttpStatusCode.OK);

        var mine = await Array(await me.GetAsync("/api/v1/workspace/pins"));
        mine.Select(p => p!["kind"]!.GetValue<string>()).Should().BeEquivalentTo("Member", "Claim");
        (await Array(await _factory.Client("ex2", Examiner).GetAsync("/api/v1/workspace/pins"))).Should().BeEmpty();

        (await _factory.Client("ex2", Examiner).DeleteAsync("/api/v1/workspace/pins/Member/M1")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await me.DeleteAsync("/api/v1/workspace/pins/member/M1")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Array(await me.GetAsync("/api/v1/workspace/pins"))).Should().ContainSingle();
    }

    [Fact]
    public async Task Unknown_kinds_are_refused()
    {
        var r = await _factory.Client("ex1", Examiner).PutAsJsonAsync("/api/v1/workspace/pins", new { kind = "Spaceship", recordId = "1" });
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(ChoRolePermissions.ClaimsExaminer)]
    [InlineData(ChoRolePermissions.MemberServices)]
    [InlineData(ChoRolePermissions.ProviderRelations)]
    [InlineData(ChoRolePermissions.FinanceApprover)]
    [InlineData(ChoRolePermissions.ComplianceOfficer)]
    [InlineData(ChoRolePermissions.ComplianceViewer)]
    public async Task Every_staff_role_has_a_workspace(string role)
    {
        (await _factory.Client("u1", role).GetAsync("/api/v1/workspace/notices")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _factory.Client("u1", role).GetAsync("/api/v1/workspace/pins")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
