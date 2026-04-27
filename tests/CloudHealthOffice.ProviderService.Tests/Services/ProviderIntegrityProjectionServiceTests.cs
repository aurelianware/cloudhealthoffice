using CloudHealthOffice.ProviderService.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProviderService.Models;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Services;

/// <summary>
/// Service-layer coverage for the integrity-projection write-back
/// pipeline (capability 5.4.5): RefreshProviderAsync (on-demand path),
/// RefreshTenantAsync (worker / backfill path), idempotency, schedule-
/// aware skip, and verification-source outage handling.
/// </summary>
public class ProviderIntegrityProjectionServiceTests
{
    private const string Tenant = "tenant-a";

    private readonly InMemoryProviderRepository _repo;
    private readonly FakeProviderVerificationClient _client;
    private readonly FakeProviderVerificationEventPublisher _events;
    private readonly ProviderIntegrityProjectionService _svc;

    public ProviderIntegrityProjectionServiceTests()
    {
        _repo = new InMemoryProviderRepository { TenantId = Tenant };
        _client = new FakeProviderVerificationClient();
        _events = new FakeProviderVerificationEventPublisher();
        var opts = Options.Create(new IntegrityProjectionOptions());
        _svc = new ProviderIntegrityProjectionService(
            _repo, _client, _events, opts,
            NullLogger<ProviderIntegrityProjectionService>.Instance);
    }

    private Provider Seed(string providerId, string npi)
    {
        var p = new Provider
        {
            Id = providerId,
            ProviderId = providerId,
            TenantId = Tenant,
            NPI = npi,
            ProviderType = ProviderType.Individual,
            FirstName = "Test",
            LastName = "Provider",
            PrimarySpecialty = "Internal Medicine",
            TaxonomyCode = "207R00000X",
            VersionId = providerId,
            VersionNumber = 1,
            VersionState = ProviderVersionState.Active,
        };
        _repo.CreateAsync(p).GetAwaiter().GetResult();
        return p;
    }

    [Fact]
    public async Task RefreshProviderAsync_writes_score_and_emits_event()
    {
        Seed("p1", "1234567890");
        _client.Seed("1234567890", score: 88, rating: "Clear");

        var result = await _svc.RefreshProviderAsync(
            Tenant, "p1", forceRefresh: true,
            actorId: "user", correlationId: "corr-1");

        result.Should().NotBeNull();
        result!.IntegrityScore.Should().Be(88);
        result.IntegrityRating.Should().Be("Clear");

        var head = await _repo.GetLatestActiveAsync("p1", DateTime.UtcNow);
        head!.IntegrityScore.Should().Be(88);
        head.IntegrityRating.Should().Be("Clear");
        head.LastVerifiedAt.Should().NotBeNull();
        head.NextVerificationDue.Should().NotBeNull();

        _events.Events.Should().ContainSingle(e =>
            e.ProviderId == "p1"
            && e.EventType == ProviderVerificationEventType.ProviderVerificationRefreshed
            && e.IntegrityScore == 88);
    }

    [Fact]
    public async Task RefreshProviderAsync_returns_null_when_no_active_head()
    {
        // No provider seeded.
        var result = await _svc.RefreshProviderAsync(
            Tenant, "missing", forceRefresh: true, actorId: null, correlationId: null);
        result.Should().BeNull();
    }

    [Fact]
    public async Task RefreshProviderAsync_skips_when_not_due_and_not_forced()
    {
        var p = Seed("p2", "1234567891");
        _client.Seed("1234567891", score: 70, rating: "Advisory");

        // First call writes and sets NextVerificationDue ~24h out.
        var first = await _svc.RefreshProviderAsync(
            Tenant, "p2", forceRefresh: true, actorId: null, correlationId: null);
        first!.IntegrityScore.Should().Be(70);

        // Second call without force — should skip.
        _client.Calls.Clear();
        var second = await _svc.RefreshProviderAsync(
            Tenant, "p2", forceRefresh: false, actorId: null, correlationId: null);

        second!.Skipped.Should().BeTrue();
        _client.Calls.Should().BeEmpty(); // no extra HTTP call
    }

    [Fact]
    public async Task RefreshProviderAsync_idempotent_event_on_repeat_at_same_verifiedAt()
    {
        Seed("p3", "1234567892");
        // Pin verifiedAt so two calls produce the same EventId.
        var pinned = DateTimeOffset.UtcNow;
        _client.Canned["1234567892"] = new VerificationResult
        {
            Npi = "1234567892",
            Status = VerificationOutcome.Verified,
            IntegrityScore = new CompositeIntegrityScore { CompositeScore = 90, Rating = "Clear" },
            LastVerifiedAt = pinned,
        };

        await _svc.RefreshProviderAsync(Tenant, "p3", forceRefresh: true, null, null);
        await _svc.RefreshProviderAsync(Tenant, "p3", forceRefresh: true, null, null);

        // Idempotent EventId per (providerId, verifiedAt) — exactly one event.
        _events.Events.Should().ContainSingle(e => e.ProviderId == "p3");
    }

    [Fact]
    public async Task RefreshTenantAsync_iterates_only_due_providers()
    {
        // p-due: NextVerificationDue in the past
        var due = Seed("p-due", "1111111111");
        due.NextVerificationDue = DateTimeOffset.UtcNow.AddDays(-1);
        _repo.Docs.First(d => d.Id == "p-due").NextVerificationDue = due.NextVerificationDue;

        // p-fresh: NextVerificationDue in the future
        var fresh = Seed("p-fresh", "2222222222");
        fresh.NextVerificationDue = DateTimeOffset.UtcNow.AddDays(7);
        _repo.Docs.First(d => d.Id == "p-fresh").NextVerificationDue = fresh.NextVerificationDue;

        // p-never: never verified (NextVerificationDue null) — included
        Seed("p-never", "3333333333");

        _client.Seed("1111111111", 80, "Clear");
        _client.Seed("3333333333", 50, "Caution");

        var result = await _svc.RefreshTenantAsync(Tenant,
            new IntegrityProjectionTenantSweepRequest { IncludeNeverVerified = true });

        result.Patched.Should().Be(2);
        result.Failed.Should().Be(0);

        // p-fresh should still have its old verification window untouched.
        var freshAfter = _repo.Docs.First(d => d.Id == "p-fresh");
        freshAfter.IntegrityScore.Should().BeNull();
    }

    [Fact]
    public async Task RefreshTenantAsync_outage_preserves_cached_score()
    {
        var p = Seed("p-cached", "4444444444");
        // Pre-populate cached score.
        await _repo.UpdateIntegrityProjectionAsync(
            Tenant, "p-cached", 75, "Advisory",
            DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1));

        _client.SimulateOutage = true;

        var result = await _svc.RefreshTenantAsync(Tenant,
            new IntegrityProjectionTenantSweepRequest { IncludeNeverVerified = true });

        result.Failed.Should().Be(1);
        result.Patched.Should().Be(0);

        var head = _repo.Docs.First(d => d.Id == "p-cached");
        head.IntegrityScore.Should().Be(75); // cached score preserved
        head.IntegrityRating.Should().Be("Advisory");
    }

    [Fact]
    public async Task RefreshTenantAsync_respects_max_providers_cap()
    {
        for (var i = 0; i < 5; i++)
        {
            var npi = $"500000000{i}";
            Seed($"cap-{i}", npi);
            _client.Seed(npi, 80, "Clear");
        }

        var result = await _svc.RefreshTenantAsync(Tenant,
            new IntegrityProjectionTenantSweepRequest
            {
                IncludeNeverVerified = true,
                MaxProviders = 3,
                PageSize = 10,
            });

        result.Patched.Should().Be(3);
    }

    [Fact]
    public async Task RefreshProviderAsync_cross_tenant_returns_null()
    {
        Seed("p-other", "9999999990");

        var result = await _svc.RefreshProviderAsync(
            "wrong-tenant", "p-other", forceRefresh: true, null, null);
        result.Should().BeNull();
    }

    [Fact]
    public async Task RefreshTenantAsync_tolerates_duplicate_NPIs_in_a_page()
    {
        // Two distinct provider rows sharing an NPI is a real shape:
        // (TenantId, NPI) is not a unique index. Pre-fix this would
        // crash in ToDictionary on the duplicate key and abort the
        // sweep for the entire tenant.
        Seed("p-a", "5550000000");
        Seed("p-b", "5550000000");
        _client.Seed("5550000000", 88, "Clear");

        var result = await _svc.RefreshTenantAsync(Tenant,
            new IntegrityProjectionTenantSweepRequest { IncludeNeverVerified = true });

        // Both rows pick up the same score (last-write-wins on the
        // chain key); the worker doesn't crash.
        result.Patched.Should().Be(2);
        _repo.Docs.First(d => d.Id == "p-a").IntegrityScore.Should().Be(88);
        _repo.Docs.First(d => d.Id == "p-b").IntegrityScore.Should().Be(88);

        // De-duped before the HTTP call: one NPI in the batch, not two.
        _client.Calls.Should().ContainSingle();
        _client.Calls[0].Should().BeEquivalentTo(new[] { "5550000000" });
    }
}
