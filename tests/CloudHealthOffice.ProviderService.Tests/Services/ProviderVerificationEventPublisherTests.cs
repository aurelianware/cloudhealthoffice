using EphemeralMongo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using ProviderService.Models;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Services;

/// <summary>
/// Mongo-backed verification-event publisher: idempotency on duplicate
/// <see cref="ProviderVerificationEvent.EventId"/> and monotonic
/// <see cref="ProviderVerificationEvent.Version"/> per
/// <c>(TenantId, ProviderId)</c>. Mirrors the pattern in
/// <c>ProviderVersionEventPublisherTests</c>.
/// </summary>
public class ProviderVerificationEventPublisherTests : IAsyncLifetime
{
    private const string Tenant = "tenant-a";
    private const string ProviderId = "provider-001";

    private IMongoRunner _runner = null!;
    private IMongoDatabase _database = null!;
    private MongoProviderVerificationEventPublisher _publisher = null!;

    public Task InitializeAsync()
    {
        _runner = MongoRunner.Run(new MongoRunnerOptions { ConnectionTimeout = TimeSpan.FromSeconds(30) });
        var client = new MongoClient(_runner.ConnectionString);
        _database = client.GetDatabase("provider_verification_event_test");
        var config = new ConfigurationBuilder().Build();
        _publisher = new MongoProviderVerificationEventPublisher(
            _database, config, NullLogger<MongoProviderVerificationEventPublisher>.Instance);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { _runner.Dispose(); }
        catch (TypeLoadException) { /* see MpipRateServiceTests note */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Publish_assigns_monotonic_version_per_provider()
    {
        var t1 = DateTimeOffset.UtcNow;
        var t2 = t1.AddHours(1);
        var t3 = t1.AddHours(2);

        var v1 = await _publisher.PublishRefreshedAsync(
            Tenant, ProviderId, 80, "Clear", t1, t1.AddDays(1), "user", null);
        var v2 = await _publisher.PublishRefreshedAsync(
            Tenant, ProviderId, 82, "Clear", t2, t2.AddDays(1), "user", null);
        var v3 = await _publisher.PublishRefreshedAsync(
            Tenant, ProviderId, 84, "Clear", t3, t3.AddDays(1), "user", null);

        v1.Version.Should().Be(1);
        v2.Version.Should().Be(2);
        v3.Version.Should().Be(3);
    }

    [Fact]
    public async Task Publish_with_same_verifiedAt_is_idempotent()
    {
        var verifiedAt = DateTimeOffset.UtcNow;
        var first = await _publisher.PublishRefreshedAsync(
            Tenant, ProviderId, 90, "Clear", verifiedAt, verifiedAt.AddDays(1), "user", null);
        var second = await _publisher.PublishRefreshedAsync(
            Tenant, ProviderId, 90, "Clear", verifiedAt, verifiedAt.AddDays(1), "user", null);

        first.EventId.Should().Be(second.EventId);
        first.Version.Should().Be(second.Version);
    }

    [Fact]
    public async Task Publish_writes_payload_fields_for_consumer()
    {
        var verifiedAt = DateTimeOffset.UtcNow;
        var nextDue = verifiedAt.AddDays(1);
        var evt = await _publisher.PublishRefreshedAsync(
            Tenant, ProviderId, 77, "Advisory", verifiedAt, nextDue, "user", "corr-1");

        evt.IntegrityScore.Should().Be(77);
        evt.IntegrityRating.Should().Be("Advisory");
        evt.VerifiedAt.Should().Be(verifiedAt);
        evt.NextVerificationDue.Should().Be(nextDue);
        evt.ActorId.Should().Be("user");
        evt.CorrelationId.Should().Be("corr-1");
    }

    [Fact]
    public async Task Publish_cross_tenant_same_eventId_does_not_collide()
    {
        // Pre-fix: evt.Id = evt.EventId, which is only documented as
        // unique within (TenantId, ProviderId). Two tenants verifying
        // the same NPI at the same instant share an EventId
        // ("refreshed:provider-001:<verifiedAtIso>") and would collide
        // on Mongo's _id. Post-fix: _id is scoped to {PartitionKey}:{EventId},
        // and the (TenantId, ProviderId, EventId) UNIQUE index is the
        // primary idempotency guard.
        var verifiedAt = DateTimeOffset.UtcNow;

        var a = await _publisher.PublishRefreshedAsync(
            "tenant-a", ProviderId, 80, "Clear", verifiedAt, verifiedAt.AddDays(1), null, null);
        var b = await _publisher.PublishRefreshedAsync(
            "tenant-b", ProviderId, 80, "Clear", verifiedAt, verifiedAt.AddDays(1), null, null);

        a.EventId.Should().Be(b.EventId); // same NPI + same instant
        a.Id.Should().NotBe(b.Id);        // but different _id (PartitionKey-scoped)
        a.TenantId.Should().NotBe(b.TenantId);
    }
}
