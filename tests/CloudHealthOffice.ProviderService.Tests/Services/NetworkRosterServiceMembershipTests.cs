using CloudHealthOffice.ProviderService.Tests.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ProviderService.Models;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Services;

/// <summary>
/// Capability 5.6 — single-membership lookup behaviour for
/// <see cref="NetworkRosterService.GetMembershipAsync"/>. Drives the
/// in-memory provider repo so the tests cover effective-window matching
/// without the storage backends.
/// </summary>
public class NetworkRosterServiceMembershipTests
{
    private const string TenantA = "tenant-a";
    private const string Network1 = "net-aetna-ppo-fl-2025";
    private const string Network2 = "net-bcbs-hmo-fl-2025";
    private const string Npi = "1234567890";

    [Fact]
    public async Task Returns_active_when_asOf_within_effective_window()
    {
        var repo = new InMemoryProviderRepository { TenantId = TenantA };
        var svc = NewService(repo);
        await SeedProviderAsync(
            repo, "p1", Network1,
            effective: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            termination: null);

        var asOf = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var result = await svc.GetMembershipAsync(TenantA, Network1, Npi, asOf);

        result.Should().NotBeNull();
        result!.IsActiveMember.Should().BeTrue();
        result.ProviderId.Should().Be("p1");
        result.NetworkId.Should().Be(Network1);
        result.ParticipationStatus.Should().Be("active");
    }

    [Fact]
    public async Task Returns_terminated_for_asOf_after_termination()
    {
        var repo = new InMemoryProviderRepository { TenantId = TenantA };
        var svc = NewService(repo);
        await SeedProviderAsync(
            repo, "p1", Network1,
            effective: new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            termination: new DateTime(2024, 12, 31, 0, 0, 0, DateTimeKind.Utc));

        var asOf = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var result = await svc.GetMembershipAsync(TenantA, Network1, Npi, asOf);

        result.Should().NotBeNull();
        result!.IsActiveMember.Should().BeFalse();
        result.ParticipationStatus.Should().Be("terminated");
    }

    [Fact]
    public async Task Returns_future_for_asOf_before_effective_date()
    {
        var repo = new InMemoryProviderRepository { TenantId = TenantA };
        var svc = NewService(repo);
        await SeedProviderAsync(
            repo, "p1", Network1,
            effective: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            termination: null);

        var asOf = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var result = await svc.GetMembershipAsync(TenantA, Network1, Npi, asOf);

        result.Should().NotBeNull();
        result!.IsActiveMember.Should().BeFalse();
        result.ParticipationStatus.Should().Be("future");
    }

    [Fact]
    public async Task Returns_null_when_npi_absent_from_tenant()
    {
        var repo = new InMemoryProviderRepository { TenantId = TenantA };
        var svc = NewService(repo);

        var result = await svc.GetMembershipAsync(TenantA, Network1, "9999999999", DateTime.UtcNow);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Returns_null_when_provider_has_no_participation_in_requested_network()
    {
        var repo = new InMemoryProviderRepository { TenantId = TenantA };
        var svc = NewService(repo);
        await SeedProviderAsync(
            repo, "p1", Network2,
            effective: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            termination: null);

        var result = await svc.GetMembershipAsync(TenantA, Network1, Npi, DateTime.UtcNow);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Honors_termination_boundary_exactly_at_asOf()
    {
        // Window is [Effective, Termination) — termination day is the
        // first day of inactivity. Document the boundary semantics so a
        // future change is detected by a failing test rather than
        // cross-service drift.
        var repo = new InMemoryProviderRepository { TenantId = TenantA };
        var svc = NewService(repo);
        var termination = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedProviderAsync(
            repo, "p1", Network1,
            effective: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            termination: termination);

        var atTermination = await svc.GetMembershipAsync(TenantA, Network1, Npi, termination);
        atTermination!.IsActiveMember.Should().BeFalse();

        var dayBefore = await svc.GetMembershipAsync(TenantA, Network1, Npi, termination.AddDays(-1));
        dayBefore!.IsActiveMember.Should().BeTrue();
    }

    private static INetworkRosterService NewService(InMemoryProviderRepository repo)
        => new NetworkRosterService(repo, NullLogger<NetworkRosterService>.Instance);

    private static async Task SeedProviderAsync(
        InMemoryProviderRepository repo,
        string providerId,
        string networkId,
        DateTime effective,
        DateTime? termination)
    {
        var p = new Provider
        {
            Id = providerId,
            ProviderId = providerId,
            VersionId = providerId + "-v1",
            VersionNumber = 1,
            VersionState = ProviderVersionState.Active,
            TenantId = TenantA,
            NPI = Npi,
            ProviderType = ProviderType.Individual,
            FirstName = "Test",
            LastName = "Adams",
            PrimarySpecialty = "207R00000X",
            TaxonomyCode = "207R00000X",
            Status = ProviderStatus.Active,
            AcceptingNewPatients = true,
        };
        p.NetworkParticipations.Add(new NetworkParticipation
        {
            NetworkId = networkId,
            LineOfBusiness = LineOfBusiness.Commercial,
            NetworkTier = "Tier1",
            EffectiveDate = effective,
            TerminationDate = termination,
            AcceptingNewPatients = true,
        });
        await repo.CreateAsync(p);
    }
}
