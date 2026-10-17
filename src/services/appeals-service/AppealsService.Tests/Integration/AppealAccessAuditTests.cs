using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppealsService.Controllers;
using AppealsService.Models;
using CloudHealthOffice.Infrastructure.Audit;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AppealsService.Tests.Integration;

/// <summary>
/// Access accounting for appeals (portal-navigation.md §6): reads of an appeal
/// (by id or number, its history, notes and documents) are logged against the
/// appeal and its member, refused reads too, and the appeal's access log needs
/// audit:read (reading it is logged).
/// </summary>
public class AppealAccessAuditTests : IClassFixture<AppealsWebApplicationFactory>
{
    private const string Tenant = "tenant-audit";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly WebApplicationFactory<Program> _host;
    private readonly RecordAccessStoreInMemory _log = new();

    public AppealAccessAuditTests(AppealsWebApplicationFactory factory)
    {
        _host = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IRecordAccessStore>();
            s.AddSingleton<IRecordAccessStore>(_log);
        }));
    }

    private HttpClient As(string subject, string role)
    {
        var client = _host.CreateDefaultClient(new ChoDevelopmentTokenHandler(subject, role));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    private async Task<Appeal> CreateAsync()
    {
        var response = await As("um-create", ChoRolePermissions.UMCoordinator).PostAsJsonAsync("/api/appeals", new CreateAppealRequest
        {
            ClaimId = "CLM-AUD", ClaimNumber = "CLM-AUD", MemberId = "M-AUD", PatientName = "Pat Audit", ProviderNPI = "1234567890",
            AppealReason = "Medically necessary.", LineOfBusiness = LineOfBusiness.Commercial,
            AppealType = AppealType.Reconsideration, AppealLevel = AppealLevel.FirstLevel, AppealedAmount = 100m, DeniedAmount = 100m,
        }, Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Appeal>(Json))!;
    }

    [Fact]
    public async Task Reading_an_appeal_by_id_or_number_is_logged_with_its_member()
    {
        var appeal = await CreateAsync();

        (await As("um-1", ChoRolePermissions.UMCoordinator).GetAsync($"/api/appeals/{appeal.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await As("um-2", ChoRolePermissions.UMCoordinator).GetAsync($"/api/appeals/number/{appeal.AppealNumber}")).StatusCode.Should().Be(HttpStatusCode.OK);

        _log.All.Should().Contain(e => e.ActorId == "um-1" && e.RecordType == "Appeal" && e.RecordId == appeal.Id && e.MemberId == "M-AUD" && e.Action == "View");
        _log.All.Should().Contain(e => e.ActorId == "um-2" && e.RecordId == appeal.Id && e.Action == "View");
    }

    [Fact]
    public async Task A_role_without_appeals_read_is_refused_and_logged()
    {
        var appeal = await CreateAsync();

        (await As("fin-1", ChoRolePermissions.Finance).GetAsync($"/api/appeals/{appeal.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        _log.All.Should().Contain(e => e.ActorId == "fin-1" && e.RecordId == appeal.Id && e.Outcome == RecordAccessOutcomes.Denied);
    }

    [Fact]
    public async Task The_access_log_is_for_audit_readers_and_is_itself_logged()
    {
        var appeal = await CreateAsync();
        await As("um-3", ChoRolePermissions.UMCoordinator).GetAsync($"/api/appeals/{appeal.Id}/history");

        (await As("um-3", ChoRolePermissions.UMCoordinator).GetAsync($"/api/appeals/{appeal.Id}/access-log")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var response = await As("co-1", ChoRolePermissions.ComplianceOfficer).GetAsync($"/api/appeals/{appeal.Id}/access-log");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<List<RecordAccessEvent>>()).Should().Contain(e => e.ActorId == "um-3" && e.Action == "History");
        _log.All.Should().Contain(e => e.ActorId == "co-1" && e.Action == RecordAccessActions.AccessLog);
    }
}
