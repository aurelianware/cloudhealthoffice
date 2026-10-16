using CloudHealthOffice.Infrastructure.Data;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using TenantService.Models;
using TenantService.Services;

namespace CloudHealthOffice.TenantService.Tests;

/// <summary>
/// Optimistic concurrency on tenant users (EphemeralMongo). RecordLoginAsync
/// read the user, set LastLoginAt and saved the whole profile (roles, status,
/// ...) from that copy, so a login racing an admin's role removal or
/// deactivation restored the revoked role or status. A login now writes
/// LastLoginAt alone, and a profile update is conditional on the
/// <see cref="TenantUser.Revision"/> it read.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class TenantUserConcurrencyTests : IAsyncLifetime
{
    private const string Tenant = "acme";
    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;

    public TenantUserConcurrencyTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("tenant_user_revision");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    private TenantUserRepository Repo() => new(_database, NullLogger<TenantUserRepository>.Instance);

    private static TenantUserManagementService Service(ITenantUserRepository users) => new(
        users, Mock.Of<ITenantRoleRepository>(), Mock.Of<ITenantRepository>(),
        NullLogger<TenantUserManagementService>.Instance, Mock.Of<ICurrentActor>(a => a.UserId == "admin-1"));

    private async Task<TenantUser> SeedAsync(string id = "user-1") => await Repo().CreateAsync(new TenantUser
    {
        Id = id, TenantId = Tenant, Email = id + "@acme.example", DisplayName = "Pat",
        Roles = new List<string> { "ClaimsExaminer", "TenantAdmin" }, Status = TenantUserStatus.Active,
    });

    private async Task<TenantUser> ReadAsync(string id = "user-1") => (await Repo().GetByIdAsync(id))!;

    private async Task RemoveAdminRoleAsync()
    {
        var admin = await ReadAsync();
        admin.Roles = new List<string> { "ClaimsExaminer" };
        await Repo().UpdateAsync(admin);
    }

    private async Task DeactivateAsync()
    {
        var admin = await ReadAsync();
        admin.Status = TenantUserStatus.Disabled;
        await Repo().UpdateAsync(admin);
    }

    [Fact]
    public async Task A_login_racing_a_role_removal_does_not_restore_the_role()
    {
        await SeedAsync();
        var users = new InterleavingUsers(Repo(), RemoveAdminRoleAsync);

        await Service(users).RecordLoginAsync("user-1");

        var stored = await ReadAsync();
        stored.Roles.Should().Equal("ClaimsExaminer");
        stored.LastLoginAt.Should().NotBeNull("the login is still recorded");
    }

    [Fact]
    public async Task A_login_racing_a_deactivation_does_not_reactivate_the_user()
    {
        await SeedAsync();
        var users = new InterleavingUsers(Repo(), DeactivateAsync);

        await Service(users).RecordLoginAsync("user-1");

        var stored = await ReadAsync();
        stored.Status.Should().Be(TenantUserStatus.Disabled);
        stored.LastLoginAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_login_for_a_missing_user_is_still_not_found()
    {
        var act = () => Service(Repo()).RecordLoginAsync("nobody");

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Two_concurrent_updates_from_the_same_read_one_wins_the_other_conflicts()
    {
        await SeedAsync();
        var a = await ReadAsync();
        var b = await ReadAsync();
        a.Roles = new List<string> { "ClaimsExaminer" };
        b.DisplayName = "Pat Renamed";

        var outcomes = await Task.WhenAll(Try(() => Repo().UpdateAsync(a)), Try(() => Repo().UpdateAsync(b)));

        outcomes.Count(o => o is null).Should().Be(1);
        outcomes.OfType<UserChangedConcurrentlyException>().Should().ContainSingle()
            .Which.Should().BeAssignableTo<ConcurrencyConflictException>();
        (await ReadAsync()).Revision.Should().Be(1);
    }

    [Fact]
    public async Task An_admin_save_from_a_copy_read_before_a_role_removal_conflicts_instead_of_restoring_it()
    {
        await SeedAsync();
        var stale = await ReadAsync();
        await RemoveAdminRoleAsync();
        stale.DisplayName = "edited from a stale read";

        var act = () => Repo().UpdateAsync(stale);

        await act.Should().ThrowAsync<UserChangedConcurrentlyException>();
        stale.Revision.Should().Be(0);
        (await ReadAsync()).Roles.Should().Equal("ClaimsExaminer");
    }

    [Fact]
    public async Task A_user_written_before_the_revision_existed_updates_once_then_carries_one()
    {
        await SeedAsync();
        await _database.GetCollection<BsonDocument>("TenantUsers").UpdateOneAsync(
            new BsonDocument("_id", "user-1"),
            new BsonDocument("$unset", new BsonDocument { { "Revision", "" }, { "revision", "" } }));
        var legacy = await ReadAsync();
        var legacyToo = await ReadAsync();
        legacy.Revision.Should().Be(0);
        legacy.Department = "Claims";

        await Repo().UpdateAsync(legacy);

        (await ReadAsync()).Revision.Should().Be(1);
        legacy.Revision.Should().Be(1);
        legacyToo.Roles = new List<string> { "TenantAdmin" };
        await ((Func<Task>)(() => Repo().UpdateAsync(legacyToo))).Should().ThrowAsync<UserChangedConcurrentlyException>();
    }

    [Fact]
    public async Task Updating_a_missing_user_is_still_UserChangedConcurrently()
    {
        var act = () => Repo().UpdateAsync(new TenantUser { Id = "nobody", Email = "n@acme.example" });

        await act.Should().ThrowAsync<UserChangedConcurrentlyException>();
    }

    [Fact]
    public async Task Login_and_unlink_bump_the_revision()
    {
        await SeedAsync();

        await Service(Repo()).RecordLoginAsync("user-1");
        (await ReadAsync()).Revision.Should().Be(1);

        await Repo().UnlinkAsync(Tenant, "user-1", "admin-1");
        (await ReadAsync()).Revision.Should().Be(2);
    }

    private static async Task<Exception?> Try(Func<Task> write)
    {
        try
        {
            await write();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>
    /// Runs the admin's write once: right after the login's read of the user,
    /// or (when the login does not read it) right before the login's write.
    /// </summary>
    private sealed class InterleavingUsers(ITenantUserRepository inner, Func<Task> concurrentWrite) : ITenantUserRepository
    {
        private Func<Task>? _write = concurrentWrite;

        private async Task RunOnceAsync()
        {
            var write = _write;
            _write = null;
            if (write != null) await write();
        }

        public async Task<TenantUser?> GetByIdAsync(string id)
        {
            var user = await inner.GetByIdAsync(id);
            await RunOnceAsync();
            return user;
        }

        public async Task<bool> RecordLoginAsync(string userId, DateTime at)
        {
            await RunOnceAsync();
            return await inner.RecordLoginAsync(userId, at);
        }

        public Task<TenantUser> UpdateAsync(TenantUser user) => inner.UpdateAsync(user);
        public Task<TenantUser?> GetByEmailAsync(string tenantId, string email) => inner.GetByEmailAsync(tenantId, email);
        public Task<TenantUser?> GetByAzureAdObjectIdAsync(string azureAdObjectId) => inner.GetByAzureAdObjectIdAsync(azureAdObjectId);
        public Task<IEnumerable<TenantUser>> GetByTenantIdAsync(string tenantId) => inner.GetByTenantIdAsync(tenantId);
        public Task<IEnumerable<TenantUser>> GetByRoleAsync(string tenantId, string roleName) => inner.GetByRoleAsync(tenantId, roleName);
        public Task<IEnumerable<TenantUser>> GetByDepartmentAsync(string tenantId, string department) => inner.GetByDepartmentAsync(tenantId, department);
        public Task<IEnumerable<TenantUser>> GetBySupervisorIdAsync(string tenantId, string supervisorId) => inner.GetBySupervisorIdAsync(tenantId, supervisorId);
        public Task<TenantUser> CreateAsync(TenantUser user) => inner.CreateAsync(user);
        public Task<TenantUser?> UnlinkAsync(string tenantId, string userId, string actor) => inner.UnlinkAsync(tenantId, userId, actor);
        public Task DeleteAsync(string id) => inner.DeleteAsync(id);
        public Task<bool> ExistsAsync(string tenantId, string email) => inner.ExistsAsync(tenantId, email);
    }
}
