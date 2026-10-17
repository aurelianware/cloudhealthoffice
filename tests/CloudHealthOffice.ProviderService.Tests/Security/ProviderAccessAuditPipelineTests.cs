using System.Net;
using System.Net.Http.Json;
using CloudHealthOffice.Infrastructure.Audit;
using CloudHealthOffice.Infrastructure.Security;
using Moq;
using ProviderService.Models;

namespace CloudHealthOffice.ProviderService.Tests.Security;

/// <summary>
/// Access accounting for providers: reads of a provider record (by id or NPI)
/// and of its bank details are logged, refused reads too, and the provider's
/// access log needs audit:read (reading it is logged).
/// </summary>
public class ProviderAccessAuditPipelineTests : IClassFixture<ProviderPipelineAuthTests.Factory>
{
    private const string Tenant = "tenant-1";
    private readonly ProviderPipelineAuthTests.Factory _f;

    public ProviderAccessAuditPipelineTests(ProviderPipelineAuthTests.Factory factory)
    {
        _f = factory;
        var provider = new Provider
        {
            Id = "p-aud", ProviderId = "p-aud", TenantId = Tenant, NPI = "1234567893",
            ProviderType = ProviderType.Individual, CredentialingStatus = CredentialingStatus.Approved,
        };
        _f.Providers.Setup(r => r.GetByIdAsync("p-aud")).ReturnsAsync(provider);
        _f.Providers.Setup(r => r.GetByNPIAsync("1234567893")).ReturnsAsync(provider);
    }

    private HttpClient As(string subject, string role)
    {
        var client = _f.CreateDefaultClient(new ChoDevelopmentTokenHandler(subject, role));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    [Fact]
    public async Task Reading_a_provider_by_id_or_npi_is_logged_against_the_provider()
    {
        (await As("pr-1", ChoRolePermissions.ProviderRelations).GetAsync("/api/providers/p-aud")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await As("pr-2", ChoRolePermissions.ProviderRelations).GetAsync("/api/providers/npi/1234567893")).StatusCode.Should().Be(HttpStatusCode.OK);

        _f.AccessLog.All.Should().Contain(e => e.ActorId == "pr-1" && e.RecordType == "Provider" && e.RecordId == "p-aud" && e.Action == "View" && e.TenantId == Tenant);
        _f.AccessLog.All.Should().Contain(e => e.ActorId == "pr-2" && e.RecordId == "p-aud" && e.Action == "View");
    }

    [Fact]
    public async Task Bank_detail_reads_are_logged_under_the_npi_including_refusals()
    {
        await As("fin-1", ChoRolePermissions.Finance).GetAsync("/api/providers/npi/1234567893/bank-account");
        (await As("um-1", ChoRolePermissions.UMCoordinator).GetAsync("/api/providers/npi/1234567893/bank-account"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        _f.AccessLog.All.Should().Contain(e => e.ActorId == "fin-1" && e.RecordId == "1234567893" && e.Action == "BankAccount");
        _f.AccessLog.All.Should().Contain(e => e.ActorId == "um-1" && e.RecordId == "1234567893" && e.Outcome == RecordAccessOutcomes.Denied);
    }

    [Fact]
    public async Task The_access_log_is_for_audit_readers_and_is_itself_logged()
    {
        await As("pr-3", ChoRolePermissions.ProviderRelations).GetAsync("/api/providers/p-aud");

        (await As("pr-3", ChoRolePermissions.ProviderRelations).GetAsync("/api/providers/p-aud/access-log"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var response = await As("co-1", ChoRolePermissions.ComplianceOfficer).GetAsync("/api/providers/p-aud/access-log");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<List<RecordAccessEvent>>()).Should().Contain(e => e.ActorId == "pr-3");
        _f.AccessLog.All.Should().Contain(e => e.ActorId == "co-1" && e.Action == RecordAccessActions.AccessLog);
    }
}
