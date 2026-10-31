using ClaimsService.Adapters;
using ClaimsService.Models;
using ClaimsService.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CloudHealthOffice.ClaimsService.Tests.Adapters.Conformance;

/// <summary>
/// Runs the claim conformance suite against the CHO reference adapter, which
/// proves the suite itself. A vendor connector adds its own subclass.
/// </summary>
public sealed class ChoClaimAdapterConformanceTests : ClaimAdapterConformanceSuite
{
    private const string Tenant = "tenant-a";
    private const string Member = "M-100";

    protected override Task<ClaimAdapterConformanceFixture> ArrangeAsync()
    {
        var v1 = Claim("c-1", "CN-001", Member, chain: "c-1", version: 1, ClaimVersionState.Adjusted);
        var v2 = Claim("c-1-v2", "CN-001", Member, chain: "c-1", version: 2, ClaimVersionState.Paid);
        var other = Claim("c-2", "CN-002", Member, chain: "c-2", version: 1, ClaimVersionState.Submitted);
        var otherMember = Claim("c-3", "CN-003", "M-200", chain: "c-3", version: 1, ClaimVersionState.Submitted);
        var store = new List<Claim> { v1, v2, other, otherMember };

        var repo = Substitute.For<IClaimRepository>();
        repo.GetByIdAsync(Arg.Any<string>())
            .Returns(ci => store.FirstOrDefault(c => c.Id == ci.Arg<string>()));
        repo.GetByClaimNumberAsync(Arg.Any<string>())
            .Returns(ci => store
                .Where(c => c.ClaimNumber == ci.Arg<string>())
                .OrderByDescending(c => c.VersionNumber)
                .FirstOrDefault());
        repo.ListVersionsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>())
            .Returns(ci => ((IReadOnlyList<Claim>)store
                .Where(c => c.ClaimVersionId == ci.ArgAt<string>(0))
                .OrderByDescending(c => c.VersionNumber)
                .Take(ci.ArgAt<int>(1))
                .ToList(), (string?)null));
        repo.SearchAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(),
                Arg.Any<ClaimStatus?>(), Arg.Any<LineOfBusiness?>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(ci => Page(store.Where(c => ci.ArgAt<string?>(0) is null || c.MemberId == ci.ArgAt<string?>(0)),
                ci.ArgAt<int>(6), ci.ArgAt<int>(7)).AsEnumerable());
        repo.SearchForMemberAsync(
                Arg.Any<string>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<ClaimStatus?>(),
                Arg.Any<string?>(), Arg.Any<ClaimType?>(), Arg.Any<decimal?>(), Arg.Any<decimal?>(),
                Arg.Any<int>(), Arg.Any<int>())
            .Returns(ci =>
            {
                var matches = store.Where(c => c.MemberId == ci.ArgAt<string>(0)).ToList();
                return (Page(matches, ci.ArgAt<int>(8), ci.ArgAt<int>(9)), matches.Count);
            });

        return Task.FromResult(new ClaimAdapterConformanceFixture(
            Adapter: new ChoClaimAdapter(repo, NullLogger<ChoClaimAdapter>.Instance),
            TenantId: Tenant,
            ExpectedPlatform: "cho",
            KnownClaimId: "c-2",
            KnownClaimNumber: "CN-002",
            KnownClaimVersionId: "c-1",
            KnownMemberId: Member,
            MissingClaimId: "does-not-exist",
            MissingClaimNumber: "CN-404"));
    }

    private static IReadOnlyList<Claim> Page(IEnumerable<Claim> claims, int page, int pageSize)
        => claims.Skip((Math.Max(page, 1) - 1) * pageSize).Take(pageSize).ToList();

    private static Claim Claim(
        string id, string number, string member, string chain, int version, ClaimVersionState state)
        => new()
        {
            Id = id,
            TenantId = Tenant,
            ClaimNumber = number,
            MemberId = member,
            BillingProviderNPI = "1234567890",
            ClaimType = ClaimType.Professional,
            Status = ClaimStatus.Submitted,
            TotalChargeAmount = 100m,
            ServiceDateFrom = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
            ServiceDateTo = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
            SubmittedDate = new DateTime(2026, 1, 6, 0, 0, 0, DateTimeKind.Utc),
            ClaimVersionId = chain,
            VersionNumber = version,
            VersionState = state,
        };
}
