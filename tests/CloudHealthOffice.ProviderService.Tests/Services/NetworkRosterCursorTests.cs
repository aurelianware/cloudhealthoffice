using FluentAssertions;
using ProviderService.Models;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Services;

/// <summary>
/// Unit tests for the cursor encode/decode and the filter-hash binding.
/// Targets the static helpers on <see cref="NetworkRosterService"/> so we
/// don't have to plumb a repository.
/// </summary>
public class NetworkRosterCursorTests
{
    [Fact]
    public void FilterHash_is_stable_across_equivalent_queries()
    {
        var q1 = new NetworkRosterQuery
        {
            TenantId = "t1",
            NetworkId = "n1",
            LineOfBusiness = LineOfBusiness.Medicare,
            Specialty = "207R00000X",
            Tier = "Tier1",
            AcceptingNewPatients = true,
            PageSize = 100,
        };
        var q2 = new NetworkRosterQuery
        {
            TenantId = "t1",
            NetworkId = "n1",
            LineOfBusiness = LineOfBusiness.Medicare,
            Specialty = "207R00000X",
            Tier = "Tier1",
            AcceptingNewPatients = true,
            PageSize = 100,
        };

        NetworkRosterService.ComputeFilterHash(q1, NetworkRosterSort.NameAsc)
            .Should().Be(NetworkRosterService.ComputeFilterHash(q2, NetworkRosterSort.NameAsc));
    }

    [Fact]
    public void FilterHash_diverges_when_any_filter_changes()
    {
        var baseline = new NetworkRosterQuery { TenantId = "t1", NetworkId = "n1", PageSize = 100 };
        var hashBaseline = NetworkRosterService.ComputeFilterHash(baseline, NetworkRosterSort.NameAsc);

        var lobChanged = new NetworkRosterQuery { TenantId = "t1", NetworkId = "n1", PageSize = 100, LineOfBusiness = LineOfBusiness.Medicare };
        NetworkRosterService.ComputeFilterHash(lobChanged, NetworkRosterSort.NameAsc).Should().NotBe(hashBaseline);

        var sortChanged = NetworkRosterService.ComputeFilterHash(baseline, NetworkRosterSort.IntegrityScoreDesc);
        sortChanged.Should().NotBe(hashBaseline);

        var tierChanged = new NetworkRosterQuery { TenantId = "t1", NetworkId = "n1", PageSize = 100, Tier = "Tier2" };
        NetworkRosterService.ComputeFilterHash(tierChanged, NetworkRosterSort.NameAsc).Should().NotBe(hashBaseline);

        var pageSizeChanged = new NetworkRosterQuery { TenantId = "t1", NetworkId = "n1", PageSize = 50 };
        NetworkRosterService.ComputeFilterHash(pageSizeChanged, NetworkRosterSort.NameAsc).Should().NotBe(hashBaseline);
    }

    [Fact]
    public void FilterHash_diverges_when_AsOfDate_changes()
    {
        var d1 = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var d2 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var q1 = new NetworkRosterQuery { TenantId = "t1", NetworkId = "n1", PageSize = 100, AsOfDate = d1 };
        var q2 = new NetworkRosterQuery { TenantId = "t1", NetworkId = "n1", PageSize = 100, AsOfDate = d2 };

        NetworkRosterService.ComputeFilterHash(q1, NetworkRosterSort.NameAsc)
            .Should().NotBe(NetworkRosterService.ComputeFilterHash(q2, NetworkRosterSort.NameAsc));
    }

    [Fact]
    public void FilterHash_AsOfDate_subsecond_drift_is_ignored()
    {
        // Page 1 binds AsOfDate to "now" (defaulted). Page 2's cursor decode
        // applies the encoded AsOfDate. Sub-second drift between the two
        // calls must not invalidate the hash — we round to seconds before
        // hashing.
        var d1 = new DateTime(2025, 1, 1, 12, 0, 0, 100, DateTimeKind.Utc);
        var d2 = new DateTime(2025, 1, 1, 12, 0, 0, 900, DateTimeKind.Utc);

        var q1 = new NetworkRosterQuery { TenantId = "t1", NetworkId = "n1", PageSize = 100, AsOfDate = d1 };
        var q2 = new NetworkRosterQuery { TenantId = "t1", NetworkId = "n1", PageSize = 100, AsOfDate = d2 };

        NetworkRosterService.ComputeFilterHash(q1, NetworkRosterSort.NameAsc)
            .Should().Be(NetworkRosterService.ComputeFilterHash(q2, NetworkRosterSort.NameAsc));
    }

    [Fact]
    public void ResolveSort_defaults_to_NameAsc()
    {
        NetworkRosterService.ResolveSort(new NetworkRosterQuery())
            .Should().Be(NetworkRosterSort.NameAsc);
    }

    [Fact]
    public void ResolveSort_name_desc_is_recognised()
    {
        var sort = NetworkRosterService.ResolveSort(new NetworkRosterQuery { SortBy = "name", SortDirection = "desc" });
        sort.Should().Be(NetworkRosterSort.NameDesc);
    }

    [Fact]
    public void ResolveSort_integrityScore_always_descends_regardless_of_direction()
    {
        // ascending integrity score makes no operational sense; the
        // service folds any direction to descending and documents it.
        NetworkRosterService.ResolveSort(new NetworkRosterQuery { SortBy = "integrityScore", SortDirection = "asc" })
            .Should().Be(NetworkRosterSort.IntegrityScoreDesc);
        NetworkRosterService.ResolveSort(new NetworkRosterQuery { SortBy = "integrityScore", SortDirection = "desc" })
            .Should().Be(NetworkRosterSort.IntegrityScoreDesc);
    }

    [Fact]
    public void ResolveSort_distance_is_explicitly_unsupported()
    {
        var act = () => NetworkRosterService.ResolveSort(new NetworkRosterQuery { SortBy = "distance" });
        act.Should().Throw<NetworkRosterValidationException>()
            .Where(e => e.ErrorCode == "distance_sort_unsupported");
    }

    [Fact]
    public void ApplyNullsLastForIntegrityScore_pushes_unverified_to_tail()
    {
        var rows = new List<Provider>
        {
            new() { Id = "a", IntegrityScore = null },
            new() { Id = "b", IntegrityScore = 75 },
            new() { Id = "c", IntegrityScore = null },
            new() { Id = "d", IntegrityScore = 92 },
        };

        var ordered = NetworkRosterService.ApplyNullsLastForIntegrityScore(rows);

        ordered.Select(p => p.Id).Should().Equal("d", "b", "a", "c");
    }
}
