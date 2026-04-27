using CloudHealthOffice.ProviderService.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProviderService.Models;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Migrations;

/// <summary>
/// Coverage for the admin backfill path (capability 5.4.5). Backfill
/// reuses <see cref="ProviderIntegrityProjectionService.RefreshTenantAsync"/>
/// with a far-future <c>DueBefore</c> so every Provider in the named
/// tenant is refreshed regardless of <c>NextVerificationDue</c>.
///
/// Verifies: idempotent reruns, tenant isolation, max-providers cap.
/// </summary>
public class IntegrityProjectionBackfillTests
{
    private readonly InMemoryProviderRepository _repo;
    private readonly FakeProviderVerificationClient _client;
    private readonly FakeProviderVerificationEventPublisher _events;
    private readonly ProviderIntegrityProjectionService _svc;

    public IntegrityProjectionBackfillTests()
    {
        _repo = new InMemoryProviderRepository();
        _client = new FakeProviderVerificationClient();
        _events = new FakeProviderVerificationEventPublisher();
        var opts = Options.Create(new IntegrityProjectionOptions());
        _svc = new ProviderIntegrityProjectionService(
            _repo, _client, _events, opts,
            NullLogger<ProviderIntegrityProjectionService>.Instance);
    }

    private void Seed(string tenant, string providerId, string npi, int? cachedScore = null)
    {
        var p = new Provider
        {
            Id = providerId,
            ProviderId = providerId,
            TenantId = tenant,
            NPI = npi,
            ProviderType = ProviderType.Individual,
            FirstName = "Backfill",
            LastName = "Target",
            PrimarySpecialty = "Internal Medicine",
            TaxonomyCode = "207R00000X",
            VersionId = providerId,
            VersionNumber = 1,
            VersionState = ProviderVersionState.Active,
            IntegrityScore = cachedScore,
            // NextVerificationDue intentionally null — simulates legacy
            // never-verified rows that backfill needs to populate.
        };
        _repo.CreateAsync(p).GetAwaiter().GetResult();
    }

    private static IntegrityProjectionTenantSweepRequest BackfillRequest() => new()
    {
        DueBefore = DateTimeOffset.UtcNow.AddYears(100), // every row is "due"
        IncludeNeverVerified = true,
        ActorId = "admin:backfill-integrity-projection",
    };

    [Fact]
    public async Task Backfill_populates_legacy_null_scores()
    {
        Seed("tenant-a", "p1", "1234567890");
        Seed("tenant-a", "p2", "1234567891");
        _client.Seed("1234567890", 88, "Clear");
        _client.Seed("1234567891", 60, "Advisory");

        var result = await _svc.RefreshTenantAsync("tenant-a", BackfillRequest());

        result.Patched.Should().Be(2);
        result.Failed.Should().Be(0);

        _repo.Docs.First(d => d.Id == "p1").IntegrityScore.Should().Be(88);
        _repo.Docs.First(d => d.Id == "p2").IntegrityScore.Should().Be(60);
    }

    [Fact]
    public async Task Backfill_is_idempotent_on_rerun()
    {
        Seed("tenant-a", "p1", "1234567890");
        _client.Seed("1234567890", 88, "Clear");

        var first = await _svc.RefreshTenantAsync("tenant-a", BackfillRequest());
        var second = await _svc.RefreshTenantAsync("tenant-a", BackfillRequest());

        first.Patched.Should().Be(1);
        second.Patched.Should().Be(1); // same row patched again — same payload

        _repo.Docs.First(d => d.Id == "p1").IntegrityScore.Should().Be(88);
    }

    [Fact]
    public async Task Backfill_respects_tenant_isolation()
    {
        Seed("tenant-a", "p1", "1234567890");
        Seed("tenant-b", "p1", "1234567891");
        _client.Seed("1234567890", 88, "Clear");
        _client.Seed("1234567891", 50, "Caution");

        var result = await _svc.RefreshTenantAsync("tenant-a", BackfillRequest());
        result.Patched.Should().Be(1);

        // tenant-b row untouched.
        _repo.Docs.First(d => d.TenantId == "tenant-b").IntegrityScore.Should().BeNull();
        _repo.Docs.First(d => d.TenantId == "tenant-a").IntegrityScore.Should().Be(88);
    }

    [Fact]
    public async Task Backfill_honours_max_providers_override()
    {
        for (var i = 0; i < 10; i++)
        {
            var npi = $"700000000{i}";
            Seed("tenant-a", $"cap-{i}", npi);
            _client.Seed(npi, 80, "Clear");
        }

        var request = BackfillRequest();
        request.MaxProviders = 4;
        request.PageSize = 10;

        var result = await _svc.RefreshTenantAsync("tenant-a", request);
        result.Patched.Should().Be(4);
    }

    [Fact]
    public async Task Backfill_preserves_cached_score_when_verification_returns_no_record()
    {
        Seed("tenant-a", "p1", "1234567890", cachedScore: 75);
        // Don't seed verification client → no result for this NPI.

        var result = await _svc.RefreshTenantAsync("tenant-a", BackfillRequest());

        result.Failed.Should().Be(1);
        result.Patched.Should().Be(0);
        _repo.Docs.First(d => d.Id == "p1").IntegrityScore.Should().Be(75); // cached preserved
    }
}
