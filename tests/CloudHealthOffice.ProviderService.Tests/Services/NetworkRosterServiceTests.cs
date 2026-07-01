using CloudHealthOffice.ProviderService.Tests.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ProviderService.Models;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Services;

/// <summary>
/// Behaviour tests for <see cref="NetworkRosterService"/>. Drives the
/// in-memory repository so tests cover the full filter/sort/cursor path
/// without the storage backends. Cosmos- and Mongo-specific filter
/// shapes are covered indirectly by the repository contract.
/// </summary>
public class NetworkRosterServiceTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string Network1 = "net-aetna-ppo-fl-2025";
    private const string Network2 = "net-bcbs-hmo-fl-2025";

    [Fact]
    public async Task Roster_returns_only_providers_in_the_requested_network()
    {
        var repo = new InMemoryProviderRepository { TenantId = TenantA };
        var svc = NewService(repo);

        await SeedProviderAsync(repo, "p1", "Adams", networkId: Network1);
        await SeedProviderAsync(repo, "p2", "Baker", networkId: Network2);
        await SeedProviderAsync(repo, "p3", "Carter", networkId: Network1);

        var resp = await svc.GetRosterAsync(NewQuery(Network1));

        resp.Items.Should().HaveCount(2);
        resp.Items.Select(e => e.ProviderId).Should().BeEquivalentTo(new[] { "p1", "p3" });
    }

    [Fact]
    public async Task Roster_excludes_legacy_participations_without_NetworkId()
    {
        var repo = new InMemoryProviderRepository { TenantId = TenantA };
        var svc = NewService(repo);

        // p1 has a legacy participation (NetworkId = null) — invisible by design.
        await SeedProviderAsync(repo, "p1", "Adams", networkId: null);
        await SeedProviderAsync(repo, "p2", "Baker", networkId: Network1);

        var resp = await svc.GetRosterAsync(NewQuery(Network1));

        resp.Items.Should().HaveCount(1);
        resp.Items[0].ProviderId.Should().Be("p2");
    }

    [Fact]
    public async Task Roster_isolates_tenants()
    {
        var repo = new InMemoryProviderRepository { TenantId = TenantA };
        var svc = NewService(repo);

        await SeedProviderAsync(repo, "p1", "Adams", networkId: Network1, tenantId: TenantA);
        await SeedProviderAsync(repo, "p2", "Baker", networkId: Network1, tenantId: TenantB);

        var resp = await svc.GetRosterAsync(NewQuery(Network1, tenant: TenantA));
        resp.Items.Select(e => e.ProviderId).Should().BeEquivalentTo(new[] { "p1" });
    }

    [Fact]
    public async Task Roster_combines_LOB_specialty_and_acceptingNewPatients_filters()
    {
        var repo = new InMemoryProviderRepository { TenantId = TenantA };
        var svc = NewService(repo);

        await SeedProviderAsync(repo, "p1", "Adams", networkId: Network1,
            lob: LineOfBusiness.Medicare, specialty: "208000000X", accepting: true);
        await SeedProviderAsync(repo, "p2", "Baker", networkId: Network1,
            lob: LineOfBusiness.Commercial, specialty: "208000000X", accepting: true);
        await SeedProviderAsync(repo, "p3", "Carter", networkId: Network1,
            lob: LineOfBusiness.Medicare, specialty: "207R00000X", accepting: true);
        await SeedProviderAsync(repo, "p4", "Davis", networkId: Network1,
            lob: LineOfBusiness.Medicare, specialty: "208000000X", accepting: false);

        var query = NewQuery(Network1);
        query.LineOfBusiness = LineOfBusiness.Medicare;
        query.Specialty = "208000000X";
        query.AcceptingNewPatients = true;

        var resp = await svc.GetRosterAsync(query);

        resp.Items.Select(e => e.ProviderId).Should().BeEquivalentTo(new[] { "p1" });
    }

    [Fact]
    public async Task Roster_asOfDate_excludes_participations_terminated_before_asOf()
    {
        var repo = new InMemoryProviderRepository { TenantId = TenantA };
        var svc = NewService(repo);

        var asOf = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedProviderAsync(repo, "p1", "Adams", networkId: Network1,
            effectiveDate: asOf.AddYears(-2),
            participationTerminationDate: asOf.AddDays(-1));
        await SeedProviderAsync(repo, "p2", "Baker", networkId: Network1,
            effectiveDate: asOf.AddYears(-2),
            participationTerminationDate: null);

        var query = NewQuery(Network1);
        query.AsOfDate = asOf;

        var resp = await svc.GetRosterAsync(query);

        resp.Items.Select(e => e.ProviderId).Should().BeEquivalentTo(new[] { "p2" });
    }

    [Fact]
    public async Task Roster_asOfDate_excludes_participations_effective_after_asOf()
    {
        var repo = new InMemoryProviderRepository { TenantId = TenantA };
        var svc = NewService(repo);

        var asOf = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedProviderAsync(repo, "p1", "Adams", networkId: Network1,
            effectiveDate: asOf.AddDays(1));
        await SeedProviderAsync(repo, "p2", "Baker", networkId: Network1,
            effectiveDate: asOf.AddDays(-1));

        var query = NewQuery(Network1);
        query.AsOfDate = asOf;

        var resp = await svc.GetRosterAsync(query);

        resp.Items.Select(e => e.ProviderId).Should().BeEquivalentTo(new[] { "p2" });
    }

    [Fact]
    public async Task Roster_paginates_disjoint_pages_via_cursor()
    {
        var repo = new InMemoryProviderRepository { TenantId = TenantA };
        var svc = NewService(repo);

        for (var i = 0; i < 250; i++)
        {
            var idx = i.ToString("D3");
            await SeedProviderAsync(repo, $"p-{idx}", $"Last-{idx}", networkId: Network1);
        }

        var query = NewQuery(Network1);
        query.PageSize = 100;

        var page1 = await svc.GetRosterAsync(query);
        page1.Items.Should().HaveCount(100);
        page1.NextCursor.Should().NotBeNull();

        var query2 = NewQuery(Network1);
        query2.PageSize = 100;
        query2.Cursor = page1.NextCursor;
        var page2 = await svc.GetRosterAsync(query2);
        page2.Items.Should().HaveCount(100);
        page2.NextCursor.Should().NotBeNull();

        var query3 = NewQuery(Network1);
        query3.PageSize = 100;
        query3.Cursor = page2.NextCursor;
        var page3 = await svc.GetRosterAsync(query3);
        page3.Items.Should().HaveCount(50);
        page3.NextCursor.Should().BeNull();

        var ids = page1.Items.Concat(page2.Items).Concat(page3.Items)
            .Select(e => e.ProviderId).ToList();
        ids.Distinct().Should().HaveCount(250);
    }

    [Fact]
    public async Task Roster_cursor_with_mismatched_filters_is_rejected()
    {
        var repo = new InMemoryProviderRepository { TenantId = TenantA };
        var svc = NewService(repo);

        await SeedProviderAsync(repo, "p1", "Adams", networkId: Network1, lob: LineOfBusiness.Medicare);

        var page1 = await svc.GetRosterAsync(NewQuery(Network1));
        // Force a NextCursor by using a tiny page size.
        for (var i = 0; i < 5; i++)
            await SeedProviderAsync(repo, $"p-extra-{i}", $"Last-{i}", networkId: Network1);

        var p1Query = NewQuery(Network1);
        p1Query.PageSize = 2;
        var first = await svc.GetRosterAsync(p1Query);
        first.NextCursor.Should().NotBeNull();

        var tampered = NewQuery(Network1);
        tampered.PageSize = 2;
        tampered.Cursor = first.NextCursor;
        tampered.LineOfBusiness = LineOfBusiness.Commercial; // didn't match page 1

        var act = async () => await svc.GetRosterAsync(tampered);
        await act.Should().ThrowAsync<NetworkRosterValidationException>()
            .Where(e => e.ErrorCode == "cursor_filter_mismatch");
    }

    [Fact]
    public async Task Roster_invalid_cursor_token_returns_validation_error()
    {
        var repo = new InMemoryProviderRepository { TenantId = TenantA };
        var svc = NewService(repo);

        var query = NewQuery(Network1);
        query.Cursor = "not-a-real-token";

        var act = async () => await svc.GetRosterAsync(query);
        await act.Should().ThrowAsync<NetworkRosterValidationException>()
            .Where(e => e.ErrorCode == "cursor_invalid");
    }

    [Fact]
    public async Task Roster_sort_by_name_asc_default()
    {
        var repo = new InMemoryProviderRepository { TenantId = TenantA };
        var svc = NewService(repo);

        await SeedProviderAsync(repo, "p1", "Carter", networkId: Network1);
        await SeedProviderAsync(repo, "p2", "Adams", networkId: Network1);
        await SeedProviderAsync(repo, "p3", "Baker", networkId: Network1);

        var resp = await svc.GetRosterAsync(NewQuery(Network1));

        resp.Items.Select(e => e.Provider.DisplayName.Split(' ').Last())
            .Should().Equal("Adams", "Baker", "Carter");
    }

    [Fact]
    public async Task Roster_sort_by_name_desc()
    {
        var repo = new InMemoryProviderRepository { TenantId = TenantA };
        var svc = NewService(repo);

        await SeedProviderAsync(repo, "p1", "Carter", networkId: Network1);
        await SeedProviderAsync(repo, "p2", "Adams", networkId: Network1);
        await SeedProviderAsync(repo, "p3", "Baker", networkId: Network1);

        var query = NewQuery(Network1);
        query.SortBy = "name";
        query.SortDirection = "desc";
        var resp = await svc.GetRosterAsync(query);

        resp.Items.Select(e => e.Provider.DisplayName.Split(' ').Last())
            .Should().Equal("Carter", "Baker", "Adams");
    }

    [Fact]
    public async Task Roster_sort_by_integrityScore_desc_with_nulls_last()
    {
        var repo = new InMemoryProviderRepository { TenantId = TenantA };
        var svc = NewService(repo);

        await SeedProviderAsync(repo, "p1", "Adams", networkId: Network1, integrityScore: 75);
        await SeedProviderAsync(repo, "p2", "Baker", networkId: Network1, integrityScore: null);
        await SeedProviderAsync(repo, "p3", "Carter", networkId: Network1, integrityScore: 92);
        await SeedProviderAsync(repo, "p4", "Davis", networkId: Network1, integrityScore: null);

        var query = NewQuery(Network1);
        query.SortBy = "integrityScore";
        var resp = await svc.GetRosterAsync(query);

        resp.Items.Select(e => e.ProviderId).Take(2).Should().Equal("p3", "p1");
        resp.Items.Skip(2).Select(e => e.IntegrityScore).Should().AllSatisfy(i =>
            (i?.Score ?? null).Should().BeNull());
    }

    [Fact]
    public async Task Roster_sort_distance_returns_validation_error()
    {
        var repo = new InMemoryProviderRepository { TenantId = TenantA };
        var svc = NewService(repo);

        var query = NewQuery(Network1);
        query.SortBy = "distance";

        var act = async () => await svc.GetRosterAsync(query);
        await act.Should().ThrowAsync<NetworkRosterValidationException>()
            .Where(e => e.ErrorCode == "distance_sort_unsupported");
    }

    [Fact]
    public async Task Roster_unknown_sortBy_returns_validation_error()
    {
        var repo = new InMemoryProviderRepository { TenantId = TenantA };
        var svc = NewService(repo);

        var query = NewQuery(Network1);
        query.SortBy = "bogus";

        var act = async () => await svc.GetRosterAsync(query);
        await act.Should().ThrowAsync<NetworkRosterValidationException>()
            .Where(e => e.ErrorCode == "unsupported_sort");
    }

    [Fact]
    public async Task Roster_pageSize_clamps_to_max_1000()
    {
        var repo = new InMemoryProviderRepository { TenantId = TenantA };
        var svc = NewService(repo);

        await SeedProviderAsync(repo, "p1", "Adams", networkId: Network1);

        var query = NewQuery(Network1);
        query.PageSize = 5000; // exceeds cap

        var resp = await svc.GetRosterAsync(query);
        resp.PageSize.Should().Be(NetworkRosterDefaults.MaxPageSize);
    }

    [Fact]
    public async Task Roster_default_pageSize_is_100()
    {
        var repo = new InMemoryProviderRepository { TenantId = TenantA };
        var svc = NewService(repo);

        var query = NewQuery(Network1);
        query.PageSize = 0; // unset → default

        var resp = await svc.GetRosterAsync(query);
        resp.PageSize.Should().Be(NetworkRosterDefaults.DefaultPageSize);
    }

    [Fact]
    public async Task Roster_emits_panel_gating_when_populated()
    {
        var repo = new InMemoryProviderRepository { TenantId = TenantA };
        var svc = NewService(repo);

        await SeedProviderAsync(repo, "p1", "Adams", networkId: Network1,
            panelLimit: 1500, panelAccepted: false, minAge: 18, maxAge: 64);

        var resp = await svc.GetRosterAsync(NewQuery(Network1));

        var entry = resp.Items.Single();
        entry.Participation.PanelGating.Should().NotBeNull();
        entry.Participation.PanelGating!.PanelLimit.Should().Be(1500);
        entry.Participation.PanelGating.PanelAccepted.Should().BeFalse();
        entry.Participation.PanelGating.MinAcceptedAgeYears.Should().Be(18);
        entry.Participation.PanelGating.MaxAcceptedAgeYears.Should().Be(64);
    }

    [Fact]
    public async Task Roster_omits_panel_gating_when_all_fields_null()
    {
        var repo = new InMemoryProviderRepository { TenantId = TenantA };
        var svc = NewService(repo);

        await SeedProviderAsync(repo, "p1", "Adams", networkId: Network1);

        var resp = await svc.GetRosterAsync(NewQuery(Network1));

        resp.Items.Single().Participation.PanelGating.Should().BeNull();
    }

    [Fact]
    public async Task Roster_emits_integrity_envelope_when_field_set()
    {
        var repo = new InMemoryProviderRepository { TenantId = TenantA };
        var svc = NewService(repo);

        await SeedProviderAsync(repo, "p1", "Adams", networkId: Network1,
            integrityScore: 88, integrityRating: "Clear",
            lastVerifiedAt: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var resp = await svc.GetRosterAsync(NewQuery(Network1));

        var i = resp.Items.Single().IntegrityScore;
        i.Should().NotBeNull();
        i!.Score.Should().Be(88);
        i.Rating.Should().Be("Clear");
        i.LastVerifiedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Roster_includes_legacy_rows_with_empty_VersionId_and_Status_Active()
    {
        // Legacy shape: VersionId / VersionState absent on disk, Status=Active.
        // The fake's HydratedView normalizes these to VersionState=Active —
        // exercising the same read-path semantics that the Mongo/Cosmos
        // queries now accept after the legacy-state-filter fix.
        var repo = new InMemoryProviderRepository { TenantId = TenantA };
        var svc = NewService(repo);

        var legacy = new Provider
        {
            Id = "legacy-1",
            // Note: ProviderId / VersionId / VersionState all defaulted (empty).
            TenantId = TenantA,
            NPI = "1234567890",
            ProviderType = ProviderType.Individual,
            FirstName = "Test",
            LastName = "Adams",
            PrimarySpecialty = "207R00000X",
            TaxonomyCode = "207R00000X",
            Status = ProviderStatus.Active,
            AcceptingNewPatients = true,
        };
        legacy.NetworkParticipations.Add(new NetworkParticipation
        {
            NetworkId = Network1,
            LineOfBusiness = LineOfBusiness.Commercial,
            NetworkTier = "Tier1",
            EffectiveDate = DateTime.UtcNow.AddYears(-1),
            AcceptingNewPatients = true,
        });
        await repo.CreateAsync(legacy);

        var resp = await svc.GetRosterAsync(NewQuery(Network1));

        resp.Items.Should().HaveCount(1);
        resp.Items[0].ProviderId.Should().Be("legacy-1");
    }

    [Fact]
    public async Task Roster_cursor_AsOfDate_tamper_rejected()
    {
        // Tampering with the cursor's AsOfDate must produce a hash
        // mismatch even when other fields are preserved — fix #5.
        var repo = new InMemoryProviderRepository { TenantId = TenantA };
        var svc = NewService(repo);
        var asOf = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 6; i++)
            await SeedProviderAsync(repo, $"p-{i}", $"Last-{i}", networkId: Network1,
                effectiveDate: asOf.AddYears(-1));

        var query = NewQuery(Network1);
        query.PageSize = 2;
        query.AsOfDate = asOf;
        var page1 = await svc.GetRosterAsync(query);
        page1.NextCursor.Should().NotBeNull();

        // Forge a new cursor with a different AsOfDate — naive client mistake.
        var decoded = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(
            System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(PadBase64(page1.NextCursor!.Replace('-', '+').Replace('_', '/')))))!;
        decoded["AsOfDate"] = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var tamperedJson = System.Text.Json.JsonSerializer.Serialize(decoded);
        var tamperedToken = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(tamperedJson))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var query2 = NewQuery(Network1);
        query2.PageSize = 2;
        query2.AsOfDate = query.AsOfDate;
        query2.Cursor = tamperedToken;

        var act = async () => await svc.GetRosterAsync(query2);
        await act.Should().ThrowAsync<NetworkRosterValidationException>()
            .Where(e => e.ErrorCode == "cursor_filter_mismatch");
    }

    private static string PadBase64(string s) => (s.Length % 4) switch { 2 => s + "==", 3 => s + "=", _ => s };

    [Fact]
    public async Task Roster_terminated_provider_chain_excluded()
    {
        var repo = new InMemoryProviderRepository { TenantId = TenantA };
        var svc = NewService(repo);

        var asOf = DateTime.UtcNow;
        await SeedProviderAsync(repo, "p1", "Adams", networkId: Network1,
            providerTerminationDate: asOf.AddDays(-1));
        await SeedProviderAsync(repo, "p2", "Baker", networkId: Network1);

        var resp = await svc.GetRosterAsync(NewQuery(Network1));
        resp.Items.Select(e => e.ProviderId).Should().BeEquivalentTo(new[] { "p2" });
    }

    [Fact]
    public async Task Roster_picks_participation_matching_query()
    {
        var repo = new InMemoryProviderRepository { TenantId = TenantA };
        var svc = NewService(repo);

        // Provider has two participations on the same network — different LOBs.
        // Query asks for Medicare; the response must surface the Medicare row.
        var p = NewActiveProvider("p1", "Adams", TenantA);
        p.NetworkParticipations.Add(NewParticipation(Network1, LineOfBusiness.Commercial, "Tier2"));
        p.NetworkParticipations.Add(NewParticipation(Network1, LineOfBusiness.Medicare, "Tier1"));
        await repo.CreateAsync(p);

        var query = NewQuery(Network1);
        query.LineOfBusiness = LineOfBusiness.Medicare;

        var resp = await svc.GetRosterAsync(query);

        resp.Items.Should().HaveCount(1);
        resp.Items[0].Participation.LineOfBusiness.Should().Be(LineOfBusiness.Medicare);
        resp.Items[0].Participation.NetworkTier.Should().Be("Tier1");
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static INetworkRosterService NewService(InMemoryProviderRepository repo)
        => new NetworkRosterService(repo, NullLogger<NetworkRosterService>.Instance);

    private static NetworkRosterQuery NewQuery(string networkId, string tenant = TenantA)
        => new()
        {
            TenantId = tenant,
            NetworkId = networkId,
        };

    private static async Task SeedProviderAsync(
        InMemoryProviderRepository repo,
        string providerId,
        string lastName,
        string? networkId,
        string tenantId = TenantA,
        LineOfBusiness lob = LineOfBusiness.Commercial,
        string tier = "Tier1",
        bool accepting = true,
        string? specialty = null,
        DateTime? effectiveDate = null,
        DateTime? participationTerminationDate = null,
        DateTime? providerTerminationDate = null,
        int? integrityScore = null,
        string? integrityRating = null,
        DateTimeOffset? lastVerifiedAt = null,
        int? panelLimit = null,
        bool? panelAccepted = null,
        int? minAge = null,
        int? maxAge = null)
    {
        var p = NewActiveProvider(providerId, lastName, tenantId);
        p.PrimarySpecialty = specialty ?? "207R00000X";
        p.TaxonomyCode = specialty ?? "207R00000X";
        p.AcceptingNewPatients = accepting;
        p.IntegrityScore = integrityScore;
        p.IntegrityRating = integrityRating;
        p.LastVerifiedAt = lastVerifiedAt;
        p.TerminationDate = providerTerminationDate;

        var participation = NewParticipation(networkId, lob, tier);
        participation.AcceptingNewPatients = accepting;
        participation.EffectiveDate = effectiveDate ?? DateTime.UtcNow.AddYears(-1);
        participation.TerminationDate = participationTerminationDate;
        participation.PanelLimit = panelLimit;
        participation.PanelAccepted = panelAccepted;
        participation.MinAcceptedAgeYears = minAge;
        participation.MaxAcceptedAgeYears = maxAge;
        p.NetworkParticipations.Add(participation);

        await repo.CreateAsync(p);
    }

    private static Provider NewActiveProvider(string providerId, string lastName, string tenantId)
        => new()
        {
            Id = providerId,
            ProviderId = providerId,
            VersionId = providerId + "-v1",
            VersionNumber = 1,
            VersionState = ProviderVersionState.Active,
            TenantId = tenantId,
            NPI = "1234567890",
            ProviderType = ProviderType.Individual,
            FirstName = "Test",
            LastName = lastName,
            PrimarySpecialty = "207R00000X",
            TaxonomyCode = "207R00000X",
            Status = ProviderStatus.Active,
            AcceptingNewPatients = true,
        };

    private static NetworkParticipation NewParticipation(string? networkId, LineOfBusiness lob, string tier)
        => new()
        {
            NetworkId = networkId,
            LineOfBusiness = lob,
            NetworkTier = tier,
            EffectiveDate = DateTime.UtcNow.AddYears(-1),
            AcceptingNewPatients = true,
        };
}
