using EphemeralMongo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using ProviderService.Models;
using ProviderService.Repositories;

namespace CloudHealthOffice.ProviderService.Tests.Repositories;

/// <summary>
/// Mongo-backed coverage for the panel-gating-defaults write path
/// (capability 5.5): the positional-array patch must succeed against
/// an Active row WITHOUT triggering the version-state guard that
/// <see cref="ProviderRepositoryMongo.UpdateAsync"/> enforces. Identity
/// field writes against the same row must STILL throw — the carve-out
/// applies only to the five panel-gating fields on a single
/// participation slot.
/// </summary>
public class UpdatePanelGatingDefaultsTests : IAsyncLifetime
{
    private const string Tenant = "tenant-a";

    private IMongoRunner _runner = null!;
    private IMongoDatabase _database = null!;
    private ProviderRepositoryMongo _repo = null!;

    public Task InitializeAsync()
    {
        _runner = MongoRunner.Run(new MongoRunnerOptions { ConnectionTimeout = TimeSpan.FromSeconds(30) });
        var client = new MongoClient(_runner.ConnectionString);
        _database = client.GetDatabase($"panelgating_writepath_{Guid.NewGuid():N}");
        var ctx = new DefaultHttpContext();
        ctx.Items["TenantId"] = Tenant;
        var accessor = new HttpContextAccessor { HttpContext = ctx };
        _repo = new ProviderRepositoryMongo(_database, accessor, NullLogger<ProviderRepositoryMongo>.Instance);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { _runner.Dispose(); }
        catch (TypeLoadException) { /* see MpipRateServiceTests note */ }
        return Task.CompletedTask;
    }

    private async Task<Provider> SeedActiveWithParticipationsAsync(
        string providerId, string npi, params NetworkParticipation[] participations)
    {
        var draft = new Provider
        {
            Id = providerId,
            ProviderId = providerId,
            TenantId = Tenant,
            NPI = npi,
            ProviderType = ProviderType.Individual,
            FirstName = "Panel",
            LastName = "Target",
            PrimarySpecialty = "Internal Medicine",
            TaxonomyCode = "207R00000X",
            NetworkParticipations = participations.ToList(),
        };
        await _repo.CreateDraftAsync(draft);
        var head = await _repo.GetVersionAsync(providerId, draft.VersionId);
        head!.VersionState = ProviderVersionState.Active;
        await _repo.ActivateAndSupersedeAsync(head, predecessor: null);
        return head;
    }

    private static NetworkParticipation LegacyParticipation(LineOfBusiness lob = LineOfBusiness.Commercial) =>
        new()
        {
            PlanId = "plan-1",
            NetworkId = "net-1",
            LineOfBusiness = lob,
            NetworkTier = "Tier1",
            EffectiveDate = DateTime.UtcNow.AddYears(-1),
            AcceptingNewPatients = true,
        };

    [Fact]
    public async Task UpdatePanelGatingDefaults_patches_positional_slot_without_state_exception()
    {
        await SeedActiveWithParticipationsAsync("pg1", "1234500001",
            LegacyParticipation(LineOfBusiness.Commercial),
            LegacyParticipation(LineOfBusiness.Medicare));

        var fields = PanelGatingFields.LegacyUnconstrained();
        var patched = await _repo.UpdatePanelGatingDefaultsAsync(Tenant, "pg1", 1, fields);
        patched.Should().BeTrue();

        var head = await _repo.GetLatestActiveAsync("pg1", DateTime.UtcNow);
        head.Should().NotBeNull();
        head!.NetworkParticipations.Should().HaveCount(2);
        // Slot 1 patched (still null defaults — that's the contract);
        // slot 0 untouched.
        head.NetworkParticipations[1].PanelLimit.Should().BeNull();
        head.NetworkParticipations[1].AcceptedLobs.Should().NotBeNull();
        head.VersionState.Should().Be(ProviderVersionState.Active); // untouched
    }

    [Fact]
    public async Task UpdatePanelGatingDefaults_does_not_create_a_new_version()
    {
        await SeedActiveWithParticipationsAsync("pg2", "1234500002", LegacyParticipation());

        await _repo.UpdatePanelGatingDefaultsAsync(Tenant, "pg2", 0, PanelGatingFields.LegacyUnconstrained());

        var (versions, _) = await _repo.ListVersionsAsync("pg2", 25, null);
        versions.Should().HaveCount(1); // chain unchanged
        versions[0].VersionNumber.Should().Be(1);
    }

    [Fact]
    public async Task UpdateAsync_against_active_still_throws_after_panel_gating_patch()
    {
        var head = await SeedActiveWithParticipationsAsync("pg3", "1234500003", LegacyParticipation());
        await _repo.UpdatePanelGatingDefaultsAsync(Tenant, "pg3", 0, PanelGatingFields.LegacyUnconstrained());

        // Identity-field write must still surface 409 — the panel-gating
        // patch did not weaken the state guard.
        head.PrimarySpecialty = "Tampered";
        var act = () => _repo.UpdateAsync(head);
        await act.Should().ThrowAsync<ProviderVersionStateException>()
            .Where(ex => ex.CurrentState == ProviderVersionState.Active);
    }

    [Fact]
    public async Task UpdatePanelGatingDefaults_returns_false_when_index_out_of_range()
    {
        await SeedActiveWithParticipationsAsync("pg4", "1234500004", LegacyParticipation());
        var patched = await _repo.UpdatePanelGatingDefaultsAsync(Tenant, "pg4", 99, PanelGatingFields.LegacyUnconstrained());
        patched.Should().BeFalse();
    }

    [Fact]
    public async Task UpdatePanelGatingDefaults_returns_false_when_no_active_head()
    {
        var patched = await _repo.UpdatePanelGatingDefaultsAsync(Tenant, "missing", 0, PanelGatingFields.LegacyUnconstrained());
        patched.Should().BeFalse();
    }

    [Fact]
    public async Task UpdatePanelGatingDefaults_skips_terminated_chain()
    {
        var head = await SeedActiveWithParticipationsAsync("pg5", "1234500005", LegacyParticipation());
        head.VersionState = ProviderVersionState.Terminated;
        head.TerminationDate = DateTime.UtcNow;
        await _repo.ReplaceVersionRowAsync(head);

        var patched = await _repo.UpdatePanelGatingDefaultsAsync(Tenant, "pg5", 0, PanelGatingFields.LegacyUnconstrained());
        patched.Should().BeFalse();
    }

    [Fact]
    public async Task UpdatePanelGatingDefaults_skips_legacy_terminated_row()
    {
        var collection = _database.GetCollection<Provider>("Providers");
        await collection.InsertOneAsync(new Provider
        {
            Id = "legacy-pg-term",
            TenantId = Tenant,
            NPI = "8888888881",
            ProviderType = ProviderType.Individual,
            FirstName = "Legacy",
            LastName = "Terminated",
            PrimarySpecialty = "Cardiology",
            TaxonomyCode = "207RC0000X",
            Status = ProviderStatus.Terminated,
            NetworkParticipations = new List<NetworkParticipation> { LegacyParticipation() },
            // VersionState / VersionId / VersionNumber intentionally unset
        });

        var patched = await _repo.UpdatePanelGatingDefaultsAsync(Tenant, "legacy-pg-term", 0, PanelGatingFields.LegacyUnconstrained());
        patched.Should().BeFalse();
    }

    [Fact]
    public async Task ListProvidersForPanelGatingBackfill_includes_rows_with_untouched_participation()
    {
        await SeedActiveWithParticipationsAsync("pg6", "1234500006", LegacyParticipation());
        await SeedActiveWithParticipationsAsync("pg7", "1234500007",
            new NetworkParticipation
            {
                PlanId = "plan-2",
                NetworkId = "net-1",
                LineOfBusiness = LineOfBusiness.Commercial,
                NetworkTier = "Tier1",
                EffectiveDate = DateTime.UtcNow.AddYears(-1),
                PanelLimit = 100, // already touched — should be excluded
            });

        var results = await _repo.ListProvidersForPanelGatingBackfillAsync(Tenant, 0, 100);
        results.Select(p => p.ProviderId).Should().Contain("pg6");
        results.Select(p => p.ProviderId).Should().NotContain("pg7");
    }
}
