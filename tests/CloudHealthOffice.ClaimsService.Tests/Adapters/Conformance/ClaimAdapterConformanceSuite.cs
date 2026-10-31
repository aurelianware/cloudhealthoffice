using System.Text.RegularExpressions;
using ClaimsService.Adapters;
using ClaimsService.Models;
using FluentAssertions;

namespace CloudHealthOffice.ClaimsService.Tests.Adapters.Conformance;

/// <summary>
/// Records a connector's test environment must contain for the claim
/// conformance suite. A vendor connector points these at known records in the
/// payer's sandbox. The reference run seeds them in memory.
/// </summary>
public sealed record ClaimAdapterConformanceFixture(
    IClaimAdapter Adapter,
    string TenantId,
    string ExpectedPlatform,
    string KnownClaimId,
    string KnownClaimNumber,
    string KnownClaimVersionId,
    string KnownMemberId,
    string MissingClaimId,
    string MissingClaimNumber,
    Dictionary<string, string>? PlatformSettings = null);

/// <summary>
/// Behavior every <see cref="IClaimAdapter"/> must show, whatever the backend.
/// Derive from this class, return a fixture from <see cref="ArrangeAsync"/>,
/// and every fact below runs against your connector. See
/// docs/integration/CORE-ADAPTER-IMPLEMENTER-GUIDE.md.
/// </summary>
/// <remarks>
/// Covers the read operations. <see cref="IClaimAdapter.SubmitClaimAsync"/> is
/// a write path whose ownership is agreed per engagement, so it is not here.
/// </remarks>
public abstract class ClaimAdapterConformanceSuite
{
    private static readonly Regex PlatformKey = new("^[a-z0-9-]+$", RegexOptions.CultureInvariant);

    protected abstract Task<ClaimAdapterConformanceFixture> ArrangeAsync();

    private static ClaimAdapterRequest Request(ClaimAdapterConformanceFixture f) => new()
    {
        TenantId = f.TenantId,
        PlatformSettings = new Dictionary<string, string>(f.PlatformSettings ?? new()),
    };

    [Fact]
    public async Task Platform_is_the_lowercase_key_tenants_configure()
    {
        var f = await ArrangeAsync();

        f.Adapter.Platform.Should().Be(f.ExpectedPlatform);
        PlatformKey.IsMatch(f.Adapter.Platform).Should().BeTrue(
            "the factory matches configuration.claimsPlatform.platform against this key");
    }

    [Fact]
    public async Task GetClaimAsync_returns_the_known_claim_in_the_callers_tenant()
    {
        var f = await ArrangeAsync();
        var request = Request(f);
        request.ClaimId = f.KnownClaimId;

        var response = await f.Adapter.GetClaimAsync(request);

        response.Platform.Should().Be(f.Adapter.Platform);
        response.Claim.Should().NotBeNull();
        response.Claim!.Id.Should().Be(f.KnownClaimId);
        response.Claim.ClaimNumber.Should().Be(f.KnownClaimNumber);
        response.Claim.TenantId.Should().Be(f.TenantId);
    }

    [Fact]
    public async Task GetClaimAsync_returns_no_claim_rather_than_throwing_when_not_found()
    {
        var f = await ArrangeAsync();
        var request = Request(f);
        request.ClaimId = f.MissingClaimId;

        var response = await f.Adapter.GetClaimAsync(request);

        response.Claim.Should().BeNull();
    }

    [Fact]
    public async Task GetClaimAsync_rejects_a_request_without_an_identifier()
    {
        var f = await ArrangeAsync();

        var act = () => f.Adapter.GetClaimAsync(Request(f));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetClaimByNumberAsync_finds_the_known_claim_and_misses_cleanly()
    {
        var f = await ArrangeAsync();
        var known = Request(f);
        known.ClaimNumber = f.KnownClaimNumber;
        var missing = Request(f);
        missing.ClaimNumber = f.MissingClaimNumber;

        var found = await f.Adapter.GetClaimByNumberAsync(known);
        var notFound = await f.Adapter.GetClaimByNumberAsync(missing);

        found.Claim.Should().NotBeNull();
        found.Claim!.ClaimNumber.Should().Be(f.KnownClaimNumber);
        found.Claim.TenantId.Should().Be(f.TenantId);
        notFound.Claim.Should().BeNull();
    }

    [Fact]
    public async Task GetClaimByNumberAsync_rejects_a_request_without_a_claim_number()
    {
        var f = await ArrangeAsync();

        var act = () => f.Adapter.GetClaimByNumberAsync(Request(f));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ListClaimVersionsAsync_returns_the_chain_newest_first()
    {
        var f = await ArrangeAsync();
        var request = Request(f);
        request.ClaimVersionId = f.KnownClaimVersionId;

        var response = await f.Adapter.ListClaimVersionsAsync(request);

        response.Platform.Should().Be(f.Adapter.Platform);
        response.Versions.Should().NotBeEmpty();
        response.Versions.Should().OnlyContain(v => v.ClaimVersionId == f.KnownClaimVersionId);
        response.Versions.Select(v => v.VersionNumber).Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task ListClaimVersionsAsync_rejects_a_request_without_a_chain_id()
    {
        var f = await ArrangeAsync();

        var act = () => f.Adapter.ListClaimVersionsAsync(Request(f));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SearchClaimsForMemberAsync_returns_only_that_members_claims_in_the_tenant()
    {
        var f = await ArrangeAsync();

        var response = await f.Adapter.SearchClaimsForMemberAsync(new ClaimMemberSearchAdapterRequest
        {
            TenantId = f.TenantId,
            MemberId = f.KnownMemberId,
            PlatformSettings = new Dictionary<string, string>(f.PlatformSettings ?? new()),
        });

        response.Platform.Should().Be(f.Adapter.Platform);
        response.Claims.Should().Contain(c => c.Id == f.KnownClaimId);
        response.Claims.Should().OnlyContain(c => c.MemberId == f.KnownMemberId && c.TenantId == f.TenantId);
        if (response.TotalCount is { } total)
            total.Should().BeGreaterThanOrEqualTo(response.Claims.Count);
    }

    [Fact]
    public async Task SearchClaimsForMemberAsync_honors_the_page_size()
    {
        var f = await ArrangeAsync();

        var response = await f.Adapter.SearchClaimsForMemberAsync(new ClaimMemberSearchAdapterRequest
        {
            TenantId = f.TenantId,
            MemberId = f.KnownMemberId,
            Page = 1,
            PageSize = 1,
            PlatformSettings = new Dictionary<string, string>(f.PlatformSettings ?? new()),
        });

        response.Claims.Count.Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public async Task SearchClaimsForMemberAsync_rejects_a_request_without_a_member()
    {
        var f = await ArrangeAsync();

        var act = () => f.Adapter.SearchClaimsForMemberAsync(new ClaimMemberSearchAdapterRequest
        {
            TenantId = f.TenantId,
        });

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SearchClaimsAsync_honors_the_member_filter_and_page_size()
    {
        var f = await ArrangeAsync();

        var response = await f.Adapter.SearchClaimsAsync(new ClaimSearchAdapterRequest
        {
            TenantId = f.TenantId,
            MemberId = f.KnownMemberId,
            Page = 1,
            PageSize = 1,
            PlatformSettings = new Dictionary<string, string>(f.PlatformSettings ?? new()),
        });

        response.Platform.Should().Be(f.Adapter.Platform);
        response.Claims.Count.Should().BeLessThanOrEqualTo(1);
        response.Claims.Should().OnlyContain(c => c.MemberId == f.KnownMemberId && c.TenantId == f.TenantId);
    }
}
