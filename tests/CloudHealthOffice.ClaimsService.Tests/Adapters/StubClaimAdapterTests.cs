using ClaimsService.Adapters;
using ClaimsService.Models;
using FluentAssertions;

namespace CloudHealthOffice.ClaimsService.Tests.Adapters;

public class StubClaimAdapterTests
{
    public static IEnumerable<object[]> Stubs() => new[]
    {
        new object[] { new QnxtClaimAdapter(), "qnxt", "qnxt-claims" },
        new object[] { new FacetsClaimAdapter(), "facets", "facets-claims" },
        new object[] { new HealthEdgeClaimAdapter(), "healthedge", "healthedge-claims" },
    };

    [Theory]
    [MemberData(nameof(Stubs))]
    public void Platform_returns_expected_string(IClaimAdapter adapter, string expected, string _)
    {
        adapter.Platform.Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(Stubs))]
    public async Task GetClaimAsync_throws_with_migration_TODO(IClaimAdapter adapter, string _, string todoMarker)
    {
        var request = new ClaimAdapterRequest { TenantId = "t", ClaimId = "c" };
        var act = () => adapter.GetClaimAsync(request);

        var ex = await act.Should().ThrowAsync<NotImplementedException>();
        ex.Which.Message.Should().Contain("TODO");
        ex.Which.Message.Should().Contain(todoMarker);
        ex.Which.Message.Should().Contain("docs/architecture/claim-adapter-pattern.md");
    }

    [Theory]
    [MemberData(nameof(Stubs))]
    public async Task GetClaimByNumberAsync_throws_with_migration_TODO(IClaimAdapter adapter, string _, string todoMarker)
    {
        var request = new ClaimAdapterRequest { TenantId = "t", ClaimNumber = "CN-1" };
        var act = () => adapter.GetClaimByNumberAsync(request);

        var ex = await act.Should().ThrowAsync<NotImplementedException>();
        ex.Which.Message.Should().Contain(todoMarker);
    }

    [Theory]
    [MemberData(nameof(Stubs))]
    public async Task GetClaimVersionAsync_throws_with_migration_TODO(IClaimAdapter adapter, string _, string todoMarker)
    {
        var request = new ClaimAdapterRequest { TenantId = "t", ClaimVersionId = "cv", VersionId = "v" };
        var act = () => adapter.GetClaimVersionAsync(request);

        var ex = await act.Should().ThrowAsync<NotImplementedException>();
        ex.Which.Message.Should().Contain(todoMarker);
    }

    [Theory]
    [MemberData(nameof(Stubs))]
    public async Task ListClaimVersionsAsync_throws_with_migration_TODO(IClaimAdapter adapter, string _, string todoMarker)
    {
        var request = new ClaimAdapterRequest { TenantId = "t", ClaimVersionId = "cv" };
        var act = () => adapter.ListClaimVersionsAsync(request);

        var ex = await act.Should().ThrowAsync<NotImplementedException>();
        ex.Which.Message.Should().Contain(todoMarker);
    }

    [Theory]
    [MemberData(nameof(Stubs))]
    public async Task SubmitClaimAsync_throws_with_migration_TODO(IClaimAdapter adapter, string _, string todoMarker)
    {
        var request = new ClaimSubmissionAdapterRequest { TenantId = "t", Claim = new AdapterClaim() };
        var act = () => adapter.SubmitClaimAsync(request);

        var ex = await act.Should().ThrowAsync<NotImplementedException>();
        ex.Which.Message.Should().Contain(todoMarker);
    }

    [Theory]
    [MemberData(nameof(Stubs))]
    public async Task SearchClaimsAsync_throws_with_migration_TODO(IClaimAdapter adapter, string _, string todoMarker)
    {
        var request = new ClaimSearchAdapterRequest { TenantId = "t" };
        var act = () => adapter.SearchClaimsAsync(request);

        var ex = await act.Should().ThrowAsync<NotImplementedException>();
        ex.Which.Message.Should().Contain(todoMarker);
    }

    [Theory]
    [MemberData(nameof(Stubs))]
    public async Task SearchClaimsForMemberAsync_throws_with_migration_TODO(IClaimAdapter adapter, string _, string todoMarker)
    {
        var request = new ClaimMemberSearchAdapterRequest { TenantId = "t", MemberId = "M1" };
        var act = () => adapter.SearchClaimsForMemberAsync(request);

        var ex = await act.Should().ThrowAsync<NotImplementedException>();
        ex.Which.Message.Should().Contain(todoMarker);
    }
}
