using CloudHealthOffice.Infrastructure.Data;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Stripe;
using TenantService.Models;
using TenantService.Services;

namespace CloudHealthOffice.TenantService.Tests;

/// <summary>
/// Optimistic concurrency on tenants (EphemeralMongo). <see cref="TenantRepository.UpdateAsync"/>
/// replaced the whole tenant on Id alone, so a Stripe webhook and an admin edit
/// that read the tenant at the same time overwrote each other: a subscription
/// cancellation could be undone by a settings save, and a webhook wrote back
/// the admin's old settings. A save is now conditional on the
/// <see cref="Tenant.Revision"/> it read; webhooks re-read and re-apply, admin
/// saves get a 409.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class TenantConcurrencyTests : IAsyncLifetime
{
    private const string TenantKey = "acme-1234";
    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;

    public TenantConcurrencyTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("tenant_revision");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    private TenantRepository Repo() => new(_database, NullLogger<TenantRepository>.Instance);

    private static global::TenantService.Services.StripeService Webhooks(ITenantRepository tenants) =>
        new(new ConfigurationBuilder().Build(), tenants, NullLogger<global::TenantService.Services.StripeService>.Instance);

    private async Task SeedAsync(string status = "active", BillingInfo? billing = null) => await Repo().CreateAsync(new Tenant
    {
        Id = "t-1", TenantId = TenantKey, TenantName = "Acme", OrganizationName = "Acme Health",
        Status = status, ContactInfo = new ContactInfo { Email = "ops@acme.example" }, Billing = billing,
    });

    private async Task<Tenant> ReadAsync() => (await Repo().GetByTenantIdAsync(TenantKey))!;

    private async Task AdminRenamesAsync()
    {
        var t = await ReadAsync();
        t.TenantName = "Acme Renamed";
        t.Configuration.EnabledModules = new List<string> { "claims", "eligibility", "billing" };
        await Repo().UpdateAsync(t);
    }

    private static Event SubscriptionEvent(string type) => new()
    {
        Id = "evt_1",
        Type = type,
        Data = new EventData
        {
            Object = new Subscription
            {
                Id = "sub_ABC123",
                CustomerId = "cus_ABC123",
                Metadata = new Dictionary<string, string> { ["tenant_id"] = TenantKey },
            },
        },
    };

    private static Event InvoiceEvent(string type) => new()
    {
        Id = "evt_2",
        Type = type,
        Data = new EventData { Object = new Stripe.Invoice { Id = "in_1", CustomerId = "cus_ABC123" } },
    };

    [Fact]
    public async Task A_cancellation_webhook_racing_an_admin_edit_keeps_both()
    {
        await SeedAsync();
        var tenants = new InterleavingTenants(Repo(), AdminRenamesAsync);

        await Webhooks(tenants).HandleEventAsync(SubscriptionEvent(EventTypes.CustomerSubscriptionDeleted));

        var stored = await ReadAsync();
        stored.Status.Should().Be("suspended", "the webhook is re-applied, not lost");
        stored.TenantName.Should().Be("Acme Renamed", "the admin edit is not overwritten by the webhook's stale copy");
        stored.Configuration.EnabledModules.Should().Contain("billing");
        stored.Revision.Should().Be(2);
    }

    [Fact]
    public async Task A_subscription_created_webhook_racing_an_admin_edit_keeps_both()
    {
        await SeedAsync(status: "pending");
        var tenants = new InterleavingTenants(Repo(), AdminRenamesAsync);

        await Webhooks(tenants).HandleEventAsync(SubscriptionEvent(EventTypes.CustomerSubscriptionCreated));

        var stored = await ReadAsync();
        stored.Billing!.StripeSubscriptionId.Should().Be("sub_ABC123");
        stored.TenantName.Should().Be("Acme Renamed");
    }

    [Fact]
    public async Task A_payment_succeeded_webhook_racing_an_admin_edit_reactivates_and_keeps_the_edit()
    {
        await SeedAsync(status: "suspended", billing: new BillingInfo { StripeCustomerId = "cus_ABC123" });
        var tenants = new InterleavingTenants(Repo(), AdminRenamesAsync);

        await Webhooks(tenants).HandleEventAsync(InvoiceEvent(EventTypes.InvoicePaymentSucceeded));

        var stored = await ReadAsync();
        stored.Status.Should().Be("active");
        stored.TenantName.Should().Be("Acme Renamed");
    }

    [Fact]
    public async Task A_webhook_that_keeps_conflicting_gives_up_after_bounded_attempts_so_Stripe_retries()
    {
        await SeedAsync();
        var tenants = new InterleavingTenants(Repo(), AdminRenamesAsync, times: int.MaxValue);

        var act = () => Webhooks(tenants).HandleEventAsync(SubscriptionEvent(EventTypes.CustomerSubscriptionDeleted));

        await act.Should().ThrowAsync<TenantChangedConcurrentlyException>();
        tenants.Reads.Should().Be(global::TenantService.Services.StripeService.MaxWebhookWriteAttempts);
        (await ReadAsync()).Status.Should().Be("active");
    }

    [Fact]
    public async Task An_admin_save_from_a_copy_read_before_a_cancellation_conflicts_instead_of_reactivating()
    {
        await SeedAsync();
        var stale = await ReadAsync();
        await Webhooks(Repo()).HandleEventAsync(SubscriptionEvent(EventTypes.CustomerSubscriptionDeleted));
        stale.TenantName = "edited from a stale read";

        var act = () => Repo().UpdateAsync(stale);

        (await act.Should().ThrowAsync<TenantChangedConcurrentlyException>())
            .Which.Should().BeAssignableTo<ConcurrencyConflictException>();
        stale.Revision.Should().Be(0, "a failed save leaves the read revision");
        var stored = await ReadAsync();
        stored.Status.Should().Be("suspended");
        stored.TenantName.Should().Be("Acme");
    }

    [Fact]
    public async Task Two_concurrent_admin_saves_from_the_same_read_one_wins_the_other_conflicts()
    {
        await SeedAsync();
        var a = await ReadAsync();
        var b = await ReadAsync();
        a.TenantName = "A";
        b.TenantName = "B";

        var outcomes = await Task.WhenAll(Try(() => Repo().UpdateAsync(a)), Try(() => Repo().UpdateAsync(b)));

        outcomes.Count(o => o is null).Should().Be(1);
        outcomes.OfType<TenantChangedConcurrentlyException>().Should().ContainSingle();
        (await ReadAsync()).Revision.Should().Be(1);
    }

    [Fact]
    public async Task Onboarding_activation_bumps_the_revision_so_a_stale_save_cannot_undo_it()
    {
        await SeedAsync(status: "pending");
        var stale = await ReadAsync();
        (await Repo().TryActivatePendingAsync(TenantKey, "svc")).Should().BeTrue();

        var act = () => Repo().UpdateAsync(stale);

        await act.Should().ThrowAsync<TenantChangedConcurrentlyException>();
        (await ReadAsync()).Status.Should().Be("active");
    }

    [Fact]
    public async Task A_tenant_written_before_the_revision_existed_updates_once_then_carries_one()
    {
        await SeedAsync();
        await _database.GetCollection<BsonDocument>("Tenants").UpdateOneAsync(
            new BsonDocument("_id", "t-1"),
            new BsonDocument("$unset", new BsonDocument { { "Revision", "" }, { "revision", "" } }));
        var legacy = await ReadAsync();
        var legacyToo = await ReadAsync();
        legacy.Revision.Should().Be(0);
        legacy.TenantName = "first write after the upgrade";

        await Repo().UpdateAsync(legacy);

        (await ReadAsync()).Revision.Should().Be(1);
        await ((Func<Task>)(() => Repo().UpdateAsync(legacyToo))).Should().ThrowAsync<TenantChangedConcurrentlyException>();
    }

    [Fact]
    public async Task Updating_a_missing_tenant_is_still_a_no_op_not_a_conflict()
    {
        var ghost = new Tenant { Id = "nope", TenantId = "nope" };

        var act = () => Repo().UpdateAsync(ghost);

        await act.Should().NotThrowAsync();
        (await Repo().GetByIdAsync("nope")).Should().BeNull();
    }

    [Fact]
    public async Task A_webhook_for_a_missing_tenant_is_still_ignored()
    {
        var act = () => Webhooks(Repo()).HandleEventAsync(SubscriptionEvent(EventTypes.CustomerSubscriptionDeleted));

        await act.Should().NotThrowAsync();
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
    /// Runs <paramref name="interleave"/> (an admin edit) right after a webhook's
    /// read of the tenant, before its write: the race the revision closes.
    /// </summary>
    private sealed class InterleavingTenants(ITenantRepository inner, Func<Task> interleave, int times = 1) : ITenantRepository
    {
        private int _remaining = times;
        public int Reads { get; private set; }

        private async Task RunAsync()
        {
            Reads++;
            if (_remaining-- > 0)
                await interleave();
        }

        public async Task<Tenant?> GetByTenantIdAsync(string tenantId)
        {
            var t = await inner.GetByTenantIdAsync(tenantId);
            await RunAsync();
            return t;
        }

        public async Task<IEnumerable<Tenant>> GetAllAsync(int pageSize = 100, string? continuationToken = null)
        {
            var all = (await inner.GetAllAsync(pageSize, continuationToken)).ToList();
            await RunAsync();
            return all;
        }

        public Task<Tenant?> GetByIdAsync(string tenantId) => inner.GetByIdAsync(tenantId);
        public Task<IEnumerable<Tenant>> GetByStatusAsync(string status) => inner.GetByStatusAsync(status);
        public Task<Tenant> CreateAsync(Tenant tenant) => inner.CreateAsync(tenant);
        public Task<Tenant> UpdateAsync(Tenant tenant) => inner.UpdateAsync(tenant);
        public Task DeleteAsync(string tenantId) => inner.DeleteAsync(tenantId);
        public Task<bool> ExistsAsync(string tenantId) => inner.ExistsAsync(tenantId);
        public Task<Tenant?> GetByApiKeyHashAsync(string keyHash) => inner.GetByApiKeyHashAsync(keyHash);
        public Task<bool> TryActivatePendingAsync(string tenantId, string actor) => inner.TryActivatePendingAsync(tenantId, actor);
    }
}
