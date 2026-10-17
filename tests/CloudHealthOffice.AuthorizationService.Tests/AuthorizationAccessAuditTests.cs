using System.Net;
using System.Net.Http.Json;
using AuthorizationService.Models;
using CloudHealthOffice.Infrastructure.Audit;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Xunit;
using Authorization = AuthorizationService.Models.Authorization;

namespace CloudHealthOffice.AuthorizationService.Tests;

/// <summary>
/// Access accounting for prior authorizations: who read an authorization (by
/// id or auth number, with its member), refused reads, and the
/// authorization's access log (audit:read; reading it is logged).
/// </summary>
public class AuthorizationAccessAuditTests : IClassFixture<AuthorizationApiFactory>
{
    private const string Tenant = "tenant-audit";

    private readonly WebApplicationFactory<Program> _host;
    private readonly RecordAccessStoreInMemory _log = new();

    public AuthorizationAccessAuditTests(AuthorizationApiFactory factory)
    {
        var auth = new Authorization { Id = "auth-1", TenantId = Tenant, AuthorizationNumber = "PA-1", MemberId = "MBR-9" };
        factory.AuthorizationRepository.GetByIdAsync("auth-1").Returns(auth);
        factory.AuthorizationRepository.GetByAuthorizationNumberAsync("PA-1").Returns(auth);
        _host = factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<IRecordAccessStore>();
            services.AddSingleton<IRecordAccessStore>(_log);
        }));
    }

    private HttpClient As(string subject, string role)
    {
        var client = _host.CreateDefaultClient(new ChoDevelopmentTokenHandler(subject, role));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    [Fact]
    public async Task Reading_an_authorization_is_logged_with_its_member()
    {
        Assert.Equal(HttpStatusCode.OK, (await As("um-1", ChoRolePermissions.UMCoordinator).GetAsync("/api/authorizations/auth-1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As("um-2", ChoRolePermissions.UMCoordinator).GetAsync("/api/authorizations/number/PA-1")).StatusCode);

        Assert.Contains(_log.All, e => e.ActorId == "um-1" && e.RecordType == "Authorization" && e.RecordId == "auth-1" && e.MemberId == "MBR-9" && e.Action == "View");
        Assert.Contains(_log.All, e => e.ActorId == "um-2" && e.RecordId == "auth-1" && e.MemberId == "MBR-9");
    }

    [Fact]
    public async Task A_role_without_authorizations_read_is_refused_and_logged()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await As("fin-1", ChoRolePermissions.Finance).GetAsync("/api/authorizations/auth-1")).StatusCode);

        Assert.Contains(_log.All, e => e.ActorId == "fin-1" && e.RecordId == "auth-1" && e.Outcome == RecordAccessOutcomes.Denied);
    }

    [Fact]
    public async Task The_access_log_is_for_audit_readers_and_is_itself_logged()
    {
        await As("um-3", ChoRolePermissions.UMCoordinator).GetAsync("/api/authorizations/auth-1");

        Assert.Equal(HttpStatusCode.Forbidden, (await As("um-3", ChoRolePermissions.UMCoordinator).GetAsync("/api/authorizations/auth-1/access-log")).StatusCode);

        var response = await As("cv-1", ChoRolePermissions.ComplianceViewer).GetAsync("/api/authorizations/auth-1/access-log");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains((await response.Content.ReadFromJsonAsync<List<RecordAccessEvent>>())!, e => e.ActorId == "um-3");
        Assert.Contains(_log.All, e => e.ActorId == "cv-1" && e.Action == RecordAccessActions.AccessLog);
    }
}
