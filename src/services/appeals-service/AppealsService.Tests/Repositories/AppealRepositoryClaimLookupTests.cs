using AppealsService.Models;
using AppealsService.Tests.Fakes;

namespace AppealsService.Tests.Repositories;

/// <summary>
/// Behavioral spec for <c>GetMostRecentAppealByClaimIdAsync</c>. Exercised
/// against <see cref="InMemoryAppealRepository"/>, which mirrors the
/// Cosmos + Mongo filter and ordering semantics. The production
/// implementations carry the same invariants; this suite documents and
/// guards them in a reproducible form.
/// </summary>
public class AppealRepositoryClaimLookupTests
{
    private static Appeal NewAppeal(
        string tenantId = "tenant-a",
        string claimId = "claim-1",
        AppealStatus status = AppealStatus.Submitted,
        DateTime? submittedDate = null) => new()
    {
        TenantId = tenantId,
        Id = Guid.NewGuid().ToString(),
        AppealNumber = "APL-" + Guid.NewGuid().ToString("N")[..6],
        ClaimId = claimId,
        ClaimNumber = "CLM-" + claimId,
        MemberId = "m1",
        PatientName = "enc::patient",
        ProviderNPI = "1234567890",
        AppealReason = "enc::reason",
        LineOfBusiness = LineOfBusiness.Commercial,
        AppealType = AppealType.Reconsideration,
        AppealLevel = AppealLevel.FirstLevel,
        Status = status,
        SubmittedDate = submittedDate ?? DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow
    };

    private static AppealEvent Genesis(Appeal a) => new()
    {
        TenantId = a.TenantId,
        AppealId = a.Id,
        EventId = Guid.NewGuid().ToString(),
        EventType = AppealEventType.AppealCreated,
        FromStatus = null,
        ToStatus = a.Status,
        ActorId = "test"
    };

    [Fact]
    public async Task ReturnsNull_WhenNoAppealsExist()
    {
        var repo = new InMemoryAppealRepository();

        var result = await repo.GetMostRecentAppealByClaimIdAsync("tenant-a", "claim-missing");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ReturnsNull_WhenAllAppealsForClaimAreClosed()
    {
        var repo = new InMemoryAppealRepository();
        var closed = NewAppeal(status: AppealStatus.Closed);
        await repo.CreateAsync(closed, Genesis(closed));

        var result = await repo.GetMostRecentAppealByClaimIdAsync(closed.TenantId, closed.ClaimId);

        result.Should().BeNull("a Closed appeal must not satisfy the open-appeal lookup");
    }

    [Fact]
    public async Task ReturnsMostRecentlySubmitted_AmongMultipleOpen()
    {
        var repo = new InMemoryAppealRepository();
        var older = NewAppeal(status: AppealStatus.Submitted, submittedDate: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newer = NewAppeal(status: AppealStatus.InReview, submittedDate: new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        var middle = NewAppeal(status: AppealStatus.PendingInfo, submittedDate: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        await repo.CreateAsync(older, Genesis(older));
        await repo.CreateAsync(newer, Genesis(newer));
        await repo.CreateAsync(middle, Genesis(middle));

        var result = await repo.GetMostRecentAppealByClaimIdAsync("tenant-a", "claim-1");

        result.Should().NotBeNull();
        result!.Id.Should().Be(newer.Id);
    }

    [Fact]
    public async Task DoesNotCrossTenants()
    {
        var repo = new InMemoryAppealRepository();
        var other = NewAppeal(tenantId: "tenant-b", claimId: "claim-1", status: AppealStatus.Submitted);
        await repo.CreateAsync(other, Genesis(other));

        var result = await repo.GetMostRecentAppealByClaimIdAsync("tenant-a", "claim-1");

        result.Should().BeNull("an appeal in a different tenant must not satisfy the lookup");
    }

    [Fact]
    public async Task FiltersOutClosed_AmongMixedStatus()
    {
        var repo = new InMemoryAppealRepository();
        // Closed but newer than any open appeal — must NOT win.
        var closedNewest = NewAppeal(status: AppealStatus.Closed, submittedDate: new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc));
        var openOlder = NewAppeal(status: AppealStatus.Submitted, submittedDate: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await repo.CreateAsync(closedNewest, Genesis(closedNewest));
        await repo.CreateAsync(openOlder, Genesis(openOlder));

        var result = await repo.GetMostRecentAppealByClaimIdAsync("tenant-a", "claim-1");

        result.Should().NotBeNull();
        result!.Id.Should().Be(openOlder.Id, "Closed appeals are excluded regardless of recency");
    }

    [Fact]
    public async Task DoesNotMatchDifferentClaim()
    {
        var repo = new InMemoryAppealRepository();
        var other = NewAppeal(claimId: "claim-other", status: AppealStatus.Submitted);
        await repo.CreateAsync(other, Genesis(other));

        var result = await repo.GetMostRecentAppealByClaimIdAsync(other.TenantId, "claim-1");

        result.Should().BeNull();
    }

    [Theory]
    [InlineData(AppealStatus.Draft)]
    [InlineData(AppealStatus.Submitted)]
    [InlineData(AppealStatus.InReview)]
    [InlineData(AppealStatus.PendingInfo)]
    public async Task ReturnsAppeal_ForEveryNonClosedStatus(AppealStatus status)
    {
        var repo = new InMemoryAppealRepository();
        var appeal = NewAppeal(status: status);
        await repo.CreateAsync(appeal, Genesis(appeal));

        var result = await repo.GetMostRecentAppealByClaimIdAsync(appeal.TenantId, appeal.ClaimId);

        result.Should().NotBeNull();
        result!.Id.Should().Be(appeal.Id);
        result.Status.Should().Be(status);
    }
}
