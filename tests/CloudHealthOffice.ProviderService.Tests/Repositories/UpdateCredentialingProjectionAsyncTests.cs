using EphemeralMongo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using ProviderService.Models;
using ProviderService.Repositories;

namespace CloudHealthOffice.ProviderService.Tests.Repositories;

/// <summary>
/// Mongo-backed coverage for the credentialing-projection write path
/// (capability 5.6): the projection-fields-only patch must succeed
/// against an Active row WITHOUT triggering the version-state guard
/// that <see cref="ProviderRepositoryMongo.UpdateAsync"/> enforces.
/// Identity-field writes against the same Active row must STILL throw.
/// </summary>
public class UpdateCredentialingProjectionAsyncTests : IAsyncLifetime
{
    private const string Tenant = "tenant-a";

    private IMongoRunner _runner = null!;
    private IMongoDatabase _database = null!;
    private ProviderRepositoryMongo _repo = null!;

    public Task InitializeAsync()
    {
        _runner = MongoRunner.Run(new MongoRunnerOptions { ConnectionTimeout = TimeSpan.FromSeconds(30) });
        var client = new MongoClient(_runner.ConnectionString);
        _database = client.GetDatabase($"cp_writepath_{Guid.NewGuid():N}");
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
    public async Task UpdateCredentialingProjection_patches_active_row_without_state_exception()
    {
        await SeedActiveAsync("cp1", "1234567890");
        var credentialingDate = DateTime.UtcNow;
        var nextDue = credentialingDate.AddYears(2);

        var patched = await _repo.UpdateCredentialingProjectionAsync(
            Tenant, "cp1", CredentialingStatus.Approved, credentialingDate, nextDue);
        patched.Should().BeTrue();

        var head = await _repo.GetLatestActiveAsync("cp1", DateTime.UtcNow);
        head.Should().NotBeNull();
        head!.CredentialingStatus.Should().Be(CredentialingStatus.Approved);
        head.CredentialingDate.Should().BeCloseTo(credentialingDate, TimeSpan.FromSeconds(1));
        head.RecredentialingDueDate.Should().BeCloseTo(nextDue, TimeSpan.FromSeconds(1));
        head.VersionState.Should().Be(ProviderVersionState.Active);
    }

    [Fact]
    public async Task UpdateCredentialingProjection_does_not_create_a_new_version()
    {
        await SeedActiveAsync("cp2", "1234567891");

        await _repo.UpdateCredentialingProjectionAsync(
            Tenant, "cp2", CredentialingStatus.Approved, DateTime.UtcNow, DateTime.UtcNow.AddYears(2));

        var (versions, _) = await _repo.ListVersionsAsync("cp2", 25, null);
        versions.Should().HaveCount(1);
        versions[0].VersionNumber.Should().Be(1);
    }

    [Fact]
    public async Task UpdateAsync_against_active_still_throws_after_credentialing_patch()
    {
        var head = await SeedActiveAsync("cp3", "1234567892");
        await _repo.UpdateCredentialingProjectionAsync(
            Tenant, "cp3", CredentialingStatus.Approved, DateTime.UtcNow, DateTime.UtcNow.AddYears(2));

        head.PrimarySpecialty = "Tampered";
        var act = () => _repo.UpdateAsync(head);
        await act.Should().ThrowAsync<ProviderVersionStateException>()
            .Where(ex => ex.CurrentState == ProviderVersionState.Active);
    }

    [Fact]
    public async Task UpdateCredentialingProjection_returns_false_when_no_active_head()
    {
        var patched = await _repo.UpdateCredentialingProjectionAsync(
            Tenant, "missing", CredentialingStatus.Approved,
            DateTime.UtcNow, DateTime.UtcNow.AddYears(2));
        patched.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateCredentialingProjection_skips_terminated_chain()
    {
        var head = await SeedActiveAsync("cp4", "1234567893");
        head.VersionState = ProviderVersionState.Terminated;
        head.TerminationDate = DateTime.UtcNow;
        await _repo.ReplaceVersionRowAsync(head);

        var patched = await _repo.UpdateCredentialingProjectionAsync(
            Tenant, "cp4", CredentialingStatus.Approved,
            DateTime.UtcNow, DateTime.UtcNow.AddYears(2));
        patched.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateCredentialingProjection_patches_legacy_row_with_missing_versionState()
    {
        // Legacy row: VersionState never persisted, Status=Active.
        var collection = _database.GetCollection<Provider>("Providers");
        await collection.InsertOneAsync(new Provider
        {
            Id = "legacy-active-cp",
            TenantId = Tenant,
            NPI = "9999999990",
            ProviderType = ProviderType.Individual,
            FirstName = "Legacy",
            LastName = "Active",
            PrimarySpecialty = "Cardiology",
            TaxonomyCode = "207RC0000X",
            Status = ProviderStatus.Active,
        });

        var patched = await _repo.UpdateCredentialingProjectionAsync(
            Tenant, "legacy-active-cp", CredentialingStatus.Approved,
            DateTime.UtcNow, DateTime.UtcNow.AddYears(2));
        patched.Should().BeTrue();
    }
}
