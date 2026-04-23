using AppealsService.Models;
using AppealsService.Services;

namespace AppealsService.Tests.Models;

public class AppealStateMachineTests
{
    [Theory]
    [InlineData(AppealStatus.Draft,       AppealStatus.Submitted)]
    [InlineData(AppealStatus.Draft,       AppealStatus.Closed)]
    [InlineData(AppealStatus.Submitted,   AppealStatus.InReview)]
    [InlineData(AppealStatus.Submitted,   AppealStatus.Closed)]
    [InlineData(AppealStatus.InReview,    AppealStatus.PendingInfo)]
    [InlineData(AppealStatus.InReview,    AppealStatus.Closed)]
    [InlineData(AppealStatus.PendingInfo, AppealStatus.InReview)]
    [InlineData(AppealStatus.PendingInfo, AppealStatus.Closed)]
    public void LegalTransitions_Allowed(AppealStatus from, AppealStatus to)
    {
        AppealStateMachine.IsAllowed(from, to).Should().BeTrue();
        Action act = () => AppealStateMachine.EnsureAllowed(from, to);
        act.Should().NotThrow();
    }

    [Theory]
    // Same-status → illegal at the state machine layer (idempotency is a
    // controller-layer concern, not a legal state-machine transition).
    [InlineData(AppealStatus.Draft,       AppealStatus.Draft)]
    [InlineData(AppealStatus.Submitted,   AppealStatus.Submitted)]
    [InlineData(AppealStatus.InReview,    AppealStatus.InReview)]
    [InlineData(AppealStatus.PendingInfo, AppealStatus.PendingInfo)]
    [InlineData(AppealStatus.Closed,      AppealStatus.Closed)]
    // Backwards transitions
    [InlineData(AppealStatus.Submitted,   AppealStatus.Draft)]
    [InlineData(AppealStatus.InReview,    AppealStatus.Submitted)]
    [InlineData(AppealStatus.PendingInfo, AppealStatus.Submitted)]
    // Illegal forward transitions
    [InlineData(AppealStatus.Draft,       AppealStatus.InReview)]
    [InlineData(AppealStatus.Draft,       AppealStatus.PendingInfo)]
    [InlineData(AppealStatus.Submitted,   AppealStatus.PendingInfo)]
    // Closed is terminal — no outgoing edges.
    [InlineData(AppealStatus.Closed,      AppealStatus.Draft)]
    [InlineData(AppealStatus.Closed,      AppealStatus.Submitted)]
    [InlineData(AppealStatus.Closed,      AppealStatus.InReview)]
    [InlineData(AppealStatus.Closed,      AppealStatus.PendingInfo)]
    public void IllegalTransitions_Throw(AppealStatus from, AppealStatus to)
    {
        AppealStateMachine.IsAllowed(from, to).Should().BeFalse();
        Action act = () => AppealStateMachine.EnsureAllowed(from, to);
        act.Should().Throw<InvalidAppealTransitionException>()
            .Where(e => e.FromStatus == from && e.ToStatus == to);
    }

    /// <summary>
    /// Exhaustive guard: enumerate every (from, to) pair in the 5x5 matrix
    /// and confirm the allowed set is EXACTLY the eight transitions above.
    /// Ensures a future enum addition forces an explicit decision rather
    /// than silently widening the state machine.
    /// </summary>
    [Fact]
    public void Matrix_AllowsOnlyEightTransitions()
    {
        var expected = new HashSet<(AppealStatus, AppealStatus)>
        {
            (AppealStatus.Draft,       AppealStatus.Submitted),
            (AppealStatus.Draft,       AppealStatus.Closed),
            (AppealStatus.Submitted,   AppealStatus.InReview),
            (AppealStatus.Submitted,   AppealStatus.Closed),
            (AppealStatus.InReview,    AppealStatus.PendingInfo),
            (AppealStatus.InReview,    AppealStatus.Closed),
            (AppealStatus.PendingInfo, AppealStatus.InReview),
            (AppealStatus.PendingInfo, AppealStatus.Closed),
        };

        var all = (AppealStatus[])Enum.GetValues(typeof(AppealStatus));
        foreach (var f in all)
        foreach (var t in all)
        {
            var allowed = AppealStateMachine.IsAllowed(f, t);
            var shouldBeAllowed = expected.Contains((f, t));
            allowed.Should().Be(shouldBeAllowed,
                $"transition {f} -> {t} allowed should be {shouldBeAllowed}");
        }
    }

    [Fact]
    public void Closed_IsTerminal_NeverHasLegalOutgoingEdge()
    {
        foreach (AppealStatus to in Enum.GetValues(typeof(AppealStatus)))
        {
            AppealStateMachine.IsAllowed(AppealStatus.Closed, to).Should().BeFalse(
                $"Closed is terminal; Closed -> {to} must be illegal");
        }
    }

    // ── Closure reason × from-status validity ───────────────────────────

    [Theory]
    // From Draft: only Withdrawn is valid (no decision yet).
    [InlineData(AppealStatus.Draft,       AppealClosureReasonCode.Withdrawn)]
    // From Submitted: only Withdrawn (not yet under review).
    [InlineData(AppealStatus.Submitted,   AppealClosureReasonCode.Withdrawn)]
    // From InReview: decision-bearing + admin + other + withdrawn.
    [InlineData(AppealStatus.InReview,    AppealClosureReasonCode.Approved)]
    [InlineData(AppealStatus.InReview,    AppealClosureReasonCode.Denied)]
    [InlineData(AppealStatus.InReview,    AppealClosureReasonCode.PartialApproval)]
    [InlineData(AppealStatus.InReview,    AppealClosureReasonCode.Withdrawn)]
    [InlineData(AppealStatus.InReview,    AppealClosureReasonCode.AdminError)]
    [InlineData(AppealStatus.InReview,    AppealClosureReasonCode.Other)]
    // From PendingInfo: all of the above + Expired (no-response timeout).
    [InlineData(AppealStatus.PendingInfo, AppealClosureReasonCode.Approved)]
    [InlineData(AppealStatus.PendingInfo, AppealClosureReasonCode.Denied)]
    [InlineData(AppealStatus.PendingInfo, AppealClosureReasonCode.PartialApproval)]
    [InlineData(AppealStatus.PendingInfo, AppealClosureReasonCode.Withdrawn)]
    [InlineData(AppealStatus.PendingInfo, AppealClosureReasonCode.Expired)]
    [InlineData(AppealStatus.PendingInfo, AppealClosureReasonCode.AdminError)]
    [InlineData(AppealStatus.PendingInfo, AppealClosureReasonCode.Other)]
    public void ClosureReason_LegalFromStatus(AppealStatus from, AppealClosureReasonCode reason)
    {
        AppealStateMachine.IsClosureReasonAllowed(from, reason).Should().BeTrue();
    }

    [Theory]
    // Expired is NOT valid from InReview (only from PendingInfo — no-response timeout requires info-pending first).
    [InlineData(AppealStatus.InReview,    AppealClosureReasonCode.Expired)]
    // Decision reasons NOT valid from Draft / Submitted (appeal hasn't been reviewed).
    [InlineData(AppealStatus.Draft,       AppealClosureReasonCode.Approved)]
    [InlineData(AppealStatus.Draft,       AppealClosureReasonCode.Denied)]
    [InlineData(AppealStatus.Draft,       AppealClosureReasonCode.PartialApproval)]
    [InlineData(AppealStatus.Draft,       AppealClosureReasonCode.Expired)]
    [InlineData(AppealStatus.Submitted,   AppealClosureReasonCode.Approved)]
    [InlineData(AppealStatus.Submitted,   AppealClosureReasonCode.Denied)]
    [InlineData(AppealStatus.Submitted,   AppealClosureReasonCode.PartialApproval)]
    [InlineData(AppealStatus.Submitted,   AppealClosureReasonCode.Expired)]
    // Closed → any close reason: illegal (already closed).
    [InlineData(AppealStatus.Closed,      AppealClosureReasonCode.Approved)]
    [InlineData(AppealStatus.Closed,      AppealClosureReasonCode.Withdrawn)]
    public void ClosureReason_IllegalFromStatus(AppealStatus from, AppealClosureReasonCode reason)
    {
        AppealStateMachine.IsClosureReasonAllowed(from, reason).Should().BeFalse();
    }
}
