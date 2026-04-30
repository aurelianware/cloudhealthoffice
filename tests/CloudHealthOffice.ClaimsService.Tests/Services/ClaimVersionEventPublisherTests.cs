using ClaimsService.Models;
using ClaimsService.Services;
using EphemeralMongo;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace CloudHealthOffice.ClaimsService.Tests.Services;

/// <summary>
/// Mongo-backed claim-version event publisher: idempotency on duplicate
/// <see cref="ClaimVersionEvent.EventId"/>, monotonic
/// <see cref="ClaimVersionEvent.Version"/> per
/// <c>(TenantId, ClaimVersionId)</c>, partition-key shape, and cross-tenant
/// isolation. Mirrors <c>ProviderVersionEventPublisherTests</c>.
/// </summary>
public class ClaimVersionEventPublisherTests : IAsyncLifetime
{
    private const string Tenant = "tenant-claims";
    private const string ClaimVersionId = "chain-001";

    private IMongoRunner _runner = null!;
    private IMongoDatabase _database = null!;
    private MongoClaimVersionEventPublisher _publisher = null!;

    public Task InitializeAsync()
    {
        _runner = MongoRunner.Run(new MongoRunnerOptions { ConnectionTimeout = TimeSpan.FromSeconds(30) });
        var client = new MongoClient(_runner.ConnectionString);
        _database = client.GetDatabase($"claim_event_test_{Guid.NewGuid():N}");
        var config = new ConfigurationBuilder().Build();
        _publisher = new MongoClaimVersionEventPublisher(
            _database, config, NullLogger<MongoClaimVersionEventPublisher>.Instance);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { _runner.Dispose(); }
        catch (TypeLoadException) { /* EphemeralMongo / MongoDB.Driver 3.x mismatch on disposal */ }
        return Task.CompletedTask;
    }

    private static Claim Sample(string versionId, int n = 1, ClaimVersionState state = ClaimVersionState.Submitted) => new()
    {
        Id = versionId,
        ClaimVersionId = ClaimVersionId,
        TenantId = Tenant,
        ClaimNumber = "CN-001",
        MemberId = "M1",
        BillingProviderNPI = "1234567890",
        VersionNumber = n,
        VersionState = state,
        SubmittedDate = DateTime.UtcNow
    };

    [Fact]
    public async Task Publish_assigns_monotonic_version_per_chain()
    {
        var v1 = await _publisher.PublishVersionSubmittedAsync(Sample("V1"), "user", null);
        var v2 = await _publisher.PublishVersionAdjudicatedAsync(Sample("V1"), "user", null);
        var v3 = await _publisher.PublishVersionPaidAsync(Sample("V1"), "user", null);

        v1.Version.Should().Be(1);
        v2.Version.Should().Be(2);
        v3.Version.Should().Be(3);
        v1.ClaimVersionId.Should().Be(ClaimVersionId);
        v1.PartitionKey.Should().Be($"{Tenant}:{ClaimVersionId}");
    }

    [Fact]
    public async Task Publish_with_duplicate_eventId_is_idempotent()
    {
        var version = Sample("V2");
        var first = await _publisher.PublishVersionSubmittedAsync(version, "user", null);
        var second = await _publisher.PublishVersionSubmittedAsync(version, "user", null);

        first.EventId.Should().Be(second.EventId);
        first.Version.Should().Be(second.Version);
        // Re-emitting the same event must NOT bump the monotonic counter.
        first.Version.Should().Be(1);
    }

    [Fact]
    public async Task Different_chains_increment_independently()
    {
        // Two chains under the same tenant share a Version namespace only
        // within their own (TenantId, ClaimVersionId) tuple.
        var chainA = new Claim
        {
            Id = "row-A1", ClaimVersionId = "chain-A", TenantId = Tenant,
            ClaimNumber = "CN-A", MemberId = "M1", BillingProviderNPI = "1234567890",
            VersionNumber = 1, VersionState = ClaimVersionState.Submitted
        };
        var chainB = new Claim
        {
            Id = "row-B1", ClaimVersionId = "chain-B", TenantId = Tenant,
            ClaimNumber = "CN-B", MemberId = "M2", BillingProviderNPI = "1234567890",
            VersionNumber = 1, VersionState = ClaimVersionState.Submitted
        };

        var a1 = await _publisher.PublishVersionSubmittedAsync(chainA, "user", null);
        var b1 = await _publisher.PublishVersionSubmittedAsync(chainB, "user", null);
        var a2 = await _publisher.PublishVersionAdjudicatedAsync(chainA, "user", null);

        a1.Version.Should().Be(1);
        b1.Version.Should().Be(1); // chain-B starts fresh
        a2.Version.Should().Be(2);
    }

    [Fact]
    public async Task Cross_tenant_chains_are_isolated()
    {
        // Two different tenants with the same ClaimVersionId must each
        // start at Version=1. Cross-tenant safety holds at three layers:
        //   - the unique compound index on (TenantId, ClaimVersionId, EventId)
        //   - the partition key shape "{TenantId}:{ClaimVersionId}"
        //   - the document _id "{PartitionKey}:{EventId}" (tenant-prefixed)
        // so a deterministic EventId from one tenant cannot mask a write
        // from another.
        var t1 = new Claim
        {
            Id = "t1-row", ClaimVersionId = "shared-chain", TenantId = "tenant-1",
            ClaimNumber = "CN-1", MemberId = "M1", BillingProviderNPI = "1234567890",
            VersionNumber = 1, VersionState = ClaimVersionState.Submitted
        };
        var t2 = new Claim
        {
            Id = "t2-row", ClaimVersionId = "shared-chain", TenantId = "tenant-2",
            ClaimNumber = "CN-2", MemberId = "M2", BillingProviderNPI = "1234567890",
            VersionNumber = 1, VersionState = ClaimVersionState.Submitted
        };

        var e1 = await _publisher.PublishVersionSubmittedAsync(t1, null, null);
        var e2 = await _publisher.PublishVersionSubmittedAsync(t2, null, null);

        e1.Version.Should().Be(1);
        e2.Version.Should().Be(1);
        e1.PartitionKey.Should().Be("tenant-1:shared-chain");
        e2.PartitionKey.Should().Be("tenant-2:shared-chain");
        // Distinct Mongo _id values prove the cross-tenant isolation at
        // the document level, not just the application-level index.
        e1.Id.Should().NotBe(e2.Id);
        e1.Id.Should().StartWith("tenant-1:");
        e2.Id.Should().StartWith("tenant-2:");
    }

    [Fact]
    public async Task Superseded_event_carries_from_to_version_pair()
    {
        var fromVersion = Sample("V-OLD", n: 1, state: ClaimVersionState.Adjudicated);
        var toVersion = Sample("V-NEW", n: 2);
        toVersion.PredecessorVersionId = fromVersion.Id;
        fromVersion.SupersededAt = DateTime.UtcNow;
        fromVersion.SupersededByVersionId = toVersion.Id;

        var evt = await _publisher.PublishVersionSupersededAsync(
            fromVersion, toVersion, "adjustment requested", "user", "corr-1");

        evt.EventType.Should().Be(ClaimVersionEventType.ClaimVersionSuperseded);
        evt.EventId.Should().Be($"superseded:{fromVersion.Id}->{toVersion.Id}");
        evt.VersionId.Should().Be(fromVersion.Id);
        evt.CorrelationId.Should().Be("corr-1");
        evt.Payload.Should().NotBeNull();
        evt.Payload!["fromVersionId"]!.GetValue<string>().Should().Be(fromVersion.Id);
        evt.Payload!["toVersionId"]!.GetValue<string>().Should().Be(toVersion.Id);
    }

    [Fact]
    public async Task Voided_event_records_reason()
    {
        var version = Sample("V-VOID");
        var evt = await _publisher.PublishVersionVoidedAsync(
            version, "submitted in error", "user", null);

        evt.EventType.Should().Be(ClaimVersionEventType.ClaimVersionVoided);
        evt.Payload!["reason"]!.GetValue<string>().Should().Be("submitted in error");
    }

    [Fact]
    public async Task Document_id_is_tenant_scoped_for_dedup()
    {
        // The publisher sets Mongo _id = "{PartitionKey}:{EventId}" — i.e.
        // "{TenantId}:{ClaimVersionId}:{EventId}" — so a deterministic
        // EventId cannot collide across tenants/chains at the document
        // level. The application-level unique index on
        // (TenantId, ClaimVersionId, EventId) also fires; both layers
        // catch a duplicate write.
        var version = Sample("V3");
        var evt = await _publisher.PublishVersionSubmittedAsync(version, null, null);
        evt.EventId.Should().Be($"submitted:{version.Id}");
        evt.Id.Should().Be($"{Tenant}:{ClaimVersionId}:submitted:{version.Id}");
    }
}
