using EphemeralMongo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using ProviderService.Models;
using ProviderService.Repositories;

namespace CloudHealthOffice.ProviderService.Tests.Repositories;

/// <summary>
/// Mongo-backed coverage for the integrity-projection write path
/// (capability 5.4.5): the projection-fields-only patch must succeed
/// against an Active row WITHOUT triggering the version-state guard
/// that <see cref="ProviderRepositoryMongo.UpdateAsync"/> enforces.
/// Identity-field writes against the same Active row must STILL throw.
/// </summary>
public class IntegrityProjectionWritePathTests : IAsyncLifetime
{
    private const string Tenant = "tenant-a";

    private IMongoRunner _runner = null!;
    private IMongoDatabase _database = null!;
    private ProviderRepositoryMongo _repo = null!;

    public Task InitializeAsync()
    {
        _runner = MongoRunner.Run(new MongoRunnerOptions { ConnectionTimeout = TimeSpan.FromSeconds(30) });
        var client = new MongoClient(_runner.ConnectionString);
        _database = client.GetDatabase($"projection_writepath_{Guid.NewGuid():N}");
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

    private async Task<Provider> SeedActiveAsync(string providerId, string npi)
    {
        var draft = new Provider
        {
            Id = providerId,
            ProviderId = providerId,
            TenantId = Tenant,
            NPI = npi,
            ProviderType = ProviderType.Individual,
            FirstName = "Patch",
            LastName = "Target",
            PrimarySpecialty = "Internal Medicine",
            TaxonomyCode = "207R00000X",
        };
        await _repo.CreateDraftAsync(draft);
        var head = await _repo.GetVersionAsync(providerId, draft.VersionId);
        head!.VersionState = ProviderVersionState.Active;
        await _repo.ActivateAndSupersedeAsync(head, predecessor: null);
        return head;
    }

    [Fact]
    public async Task UpdateIntegrityProjection_patches_active_row_without_state_exception()
    {
        await SeedActiveAsync("ip1", "1234567890");
        var verifiedAt = DateTimeOffset.UtcNow;
        var nextDue = verifiedAt.AddDays(1);

        var patched = await _repo.UpdateIntegrityProjectionAsync(
            Tenant, "ip1", 92, "Clear", verifiedAt, nextDue);
        patched.Should().BeTrue();

        var head = await _repo.GetLatestActiveAsync("ip1", DateTime.UtcNow);
        head.Should().NotBeNull();
        head!.IntegrityScore.Should().Be(92);
        head.IntegrityRating.Should().Be("Clear");
        head.LastVerifiedAt.Should().BeCloseTo(verifiedAt, TimeSpan.FromSeconds(1));
        head.NextVerificationDue.Should().BeCloseTo(nextDue, TimeSpan.FromSeconds(1));
        head.VersionState.Should().Be(ProviderVersionState.Active); // state untouched
    }

    [Fact]
    public async Task UpdateIntegrityProjection_does_not_create_a_new_version()
    {
        await SeedActiveAsync("ip2", "1234567891");

        await _repo.UpdateIntegrityProjectionAsync(
            Tenant, "ip2", 80, "Clear", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));

        var (versions, _) = await _repo.ListVersionsAsync("ip2", 25, null);
        versions.Should().HaveCount(1); // chain unchanged
        versions[0].VersionNumber.Should().Be(1);
    }

    [Fact]
    public async Task UpdateAsync_against_active_still_throws_after_projection_patch()
    {
        var head = await SeedActiveAsync("ip3", "1234567892");
        await _repo.UpdateIntegrityProjectionAsync(
            Tenant, "ip3", 85, "Clear", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));

        // Identity-field write must still surface 409 — the projection
        // patch did not weaken the state guard.
        head.PrimarySpecialty = "Tampered";
        head.IntegrityScore = 999; // even a projection field via UpdateAsync should fail
        var act = () => _repo.UpdateAsync(head);
        await act.Should().ThrowAsync<ProviderVersionStateException>()
            .Where(ex => ex.CurrentState == ProviderVersionState.Active);
    }

    [Fact]
    public async Task UpdateIntegrityProjection_returns_false_when_no_active_head()
    {
        // No provider seeded.
        var patched = await _repo.UpdateIntegrityProjectionAsync(
            Tenant, "missing", 50, "Caution",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));
        patched.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateIntegrityProjection_skips_terminated_chain()
    {
        var head = await SeedActiveAsync("ip4", "1234567893");
        // Terminate the head.
        head.VersionState = ProviderVersionState.Terminated;
        head.TerminationDate = DateTime.UtcNow;
        await _repo.ReplaceVersionRowAsync(head);

        var patched = await _repo.UpdateIntegrityProjectionAsync(
            Tenant, "ip4", 90, "Clear",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));
        patched.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateIntegrityProjection_skips_legacy_terminated_row()
    {
        // Legacy row: VersionState never persisted, Status=Terminated.
        // Pre-fix the missing-VersionState branch was treated as
        // "Active" without consulting Status, so this would have been
        // patched. Post-fix it must be excluded.
        var collection = _database.GetCollection<Provider>("Providers");
        await collection.InsertOneAsync(new Provider
        {
            Id = "legacy-term",
            TenantId = Tenant,
            NPI = "9999999990",
            ProviderType = ProviderType.Individual,
            FirstName = "Legacy",
            LastName = "Terminated",
            PrimarySpecialty = "Cardiology",
            TaxonomyCode = "207RC0000X",
            Status = ProviderStatus.Terminated,
            // VersionState / VersionId / VersionNumber intentionally unset
        });

        var patched = await _repo.UpdateIntegrityProjectionAsync(
            Tenant, "legacy-term", 90, "Clear",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));
        patched.Should().BeFalse();
    }

    [Fact]
    public async Task ListProvidersForIntegrityRefresh_excludes_legacy_terminated_row()
    {
        // Twin of the above for the sweep query: a legacy
        // Terminated/Suspended row must NOT show up in the
        // refresh list.
        var collection = _database.GetCollection<Provider>("Providers");
        await collection.InsertOneAsync(new Provider
        {
            Id = "legacy-term-2",
            TenantId = Tenant,
            NPI = "9999999991",
            ProviderType = ProviderType.Individual,
            FirstName = "Legacy",
            LastName = "Suspended",
            PrimarySpecialty = "Cardiology",
            TaxonomyCode = "207RC0000X",
            Status = ProviderStatus.Inactive,
        });
        // Sanity: an active legacy row should still be listed.
        await collection.InsertOneAsync(new Provider
        {
            Id = "legacy-active",
            TenantId = Tenant,
            NPI = "9999999992",
            ProviderType = ProviderType.Individual,
            FirstName = "Legacy",
            LastName = "Active",
            PrimarySpecialty = "Internal Medicine",
            TaxonomyCode = "207R00000X",
            Status = ProviderStatus.Active,
        });

        var rows = await _repo.ListProvidersForIntegrityRefreshAsync(
            Tenant, DateTimeOffset.UtcNow, includeNeverVerified: true,
            skip: 0, pageSize: 100);

        rows.Select(p => p.Id).Should().NotContain("legacy-term-2");
        rows.Select(p => p.Id).Should().Contain("legacy-active");
    }

    [Fact]
    public async Task ListProvidersForIntegrityRefresh_filters_by_due_date()
    {
        var due = await SeedActiveAsync("p-due", "5111111111");
        await _repo.UpdateIntegrityProjectionAsync(
            Tenant, "p-due", 70, "Advisory",
            DateTimeOffset.UtcNow.AddDays(-2),
            DateTimeOffset.UtcNow.AddDays(-1)); // due in the past

        var fresh = await SeedActiveAsync("p-fresh", "5222222222");
        await _repo.UpdateIntegrityProjectionAsync(
            Tenant, "p-fresh", 70, "Advisory",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(7)); // due in the future

        await SeedActiveAsync("p-never", "5333333333"); // never verified — null

        var dueRows = await _repo.ListProvidersForIntegrityRefreshAsync(
            Tenant, DateTimeOffset.UtcNow, includeNeverVerified: true,
            skip: 0, pageSize: 100);

        dueRows.Select(p => p.ProviderId).Should().BeEquivalentTo(new[] { "p-due", "p-never" });
    }

    [Fact]
    public async Task ListProviderTenantIds_returns_distinct_tenants()
    {
        await SeedActiveAsync("p-a", "6111111111");
        // Insert a second tenant directly (bypass HttpContext).
        var collection = _database.GetCollection<Provider>("Providers");
        await collection.InsertOneAsync(new Provider
        {
            Id = "p-other",
            ProviderId = "p-other",
            TenantId = "tenant-b",
            NPI = "6222222222",
            ProviderType = ProviderType.Individual,
            PrimarySpecialty = "Cardiology",
            TaxonomyCode = "207RC0000X",
            VersionId = "p-other",
            VersionNumber = 1,
            VersionState = ProviderVersionState.Active,
        });

        var tenants = await _repo.ListProviderTenantIdsAsync();
        tenants.Should().BeEquivalentTo(new[] { Tenant, "tenant-b" });
    }
}
