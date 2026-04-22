using ConsentService.Models;
using ConsentService.Services;

namespace ConsentService.Tests.Models;

public class ConsentStateMachineTests
{
    [Theory]
    [InlineData(ConsentStatus.Draft,  ConsentStatus.Active)]
    [InlineData(ConsentStatus.Draft,  ConsentStatus.Revoked)]
    [InlineData(ConsentStatus.Active, ConsentStatus.Revoked)]
    [InlineData(ConsentStatus.Active, ConsentStatus.Expired)]
    public void LegalTransitions_Allowed(ConsentStatus from, ConsentStatus to)
    {
        ConsentStateMachine.IsAllowed(from, to).Should().BeTrue();
        Action act = () => ConsentStateMachine.EnsureAllowed(from, to);
        act.Should().NotThrow();
    }

    [Theory]
    // Draft -> Draft, Draft -> Expired
    [InlineData(ConsentStatus.Draft,   ConsentStatus.Draft)]
    [InlineData(ConsentStatus.Draft,   ConsentStatus.Expired)]
    // Active -> self, Active -> Draft
    [InlineData(ConsentStatus.Active,  ConsentStatus.Active)]
    [InlineData(ConsentStatus.Active,  ConsentStatus.Draft)]
    // Revoked is terminal — no outgoing edges
    [InlineData(ConsentStatus.Revoked, ConsentStatus.Draft)]
    [InlineData(ConsentStatus.Revoked, ConsentStatus.Active)]
    [InlineData(ConsentStatus.Revoked, ConsentStatus.Revoked)]
    [InlineData(ConsentStatus.Revoked, ConsentStatus.Expired)]
    // Expired is terminal — no outgoing edges
    [InlineData(ConsentStatus.Expired, ConsentStatus.Draft)]
    [InlineData(ConsentStatus.Expired, ConsentStatus.Active)]
    [InlineData(ConsentStatus.Expired, ConsentStatus.Revoked)]
    [InlineData(ConsentStatus.Expired, ConsentStatus.Expired)]
    public void IllegalTransitions_Throw(ConsentStatus from, ConsentStatus to)
    {
        ConsentStateMachine.IsAllowed(from, to).Should().BeFalse();
        Action act = () => ConsentStateMachine.EnsureAllowed(from, to);
        act.Should().Throw<InvalidConsentTransitionException>()
            .Where(e => e.FromStatus == from && e.ToStatus == to);
    }

    /// <summary>
    /// Exhaustive guard: enumerate every (from, to) pair in the 4x4 matrix
    /// and confirm the allowed set is EXACTLY the four transitions above.
    /// Ensures a future enum addition forces an explicit decision rather
    /// than silently widening the state machine.
    /// </summary>
    [Fact]
    public void Matrix_AllowsOnlyFourTransitions()
    {
        var expected = new HashSet<(ConsentStatus, ConsentStatus)>
        {
            (ConsentStatus.Draft,  ConsentStatus.Active),
            (ConsentStatus.Draft,  ConsentStatus.Revoked),
            (ConsentStatus.Active, ConsentStatus.Revoked),
            (ConsentStatus.Active, ConsentStatus.Expired)
        };

        var all = (ConsentStatus[])Enum.GetValues(typeof(ConsentStatus));
        foreach (var f in all)
        foreach (var t in all)
        {
            var allowed = ConsentStateMachine.IsAllowed(f, t);
            var shouldBeAllowed = expected.Contains((f, t));
            allowed.Should().Be(shouldBeAllowed,
                $"transition {f} -> {t} allowed should be {shouldBeAllowed}");
        }
    }

    [Theory]
    [InlineData(ConsentStatus.Active, 60, ConsentStatus.Active)]
    [InlineData(ConsentStatus.Active, -1, ConsentStatus.Expired)]
    [InlineData(ConsentStatus.Draft,  -1, ConsentStatus.Draft)]
    [InlineData(ConsentStatus.Revoked, -1, ConsentStatus.Revoked)]
    public void ObservedStatus_ProjectsExpiryForActiveOnly(
        ConsentStatus persisted, int minutesFromNow, ConsentStatus expected)
    {
        var now = new DateTime(2026, 4, 22, 12, 0, 0, DateTimeKind.Utc);
        var consent = new Consent
        {
            TenantId = "t",
            MemberId = "m",
            GrantedBy = "g",
            Status = persisted,
            ExpiresAt = now.AddMinutes(minutesFromNow)
        };

        consent.ObservedStatus(now).Should().Be(expected);
    }

    [Fact]
    public void ObservedStatus_NoExpiresAt_ReturnsPersisted()
    {
        var consent = new Consent
        {
            TenantId = "t",
            MemberId = "m",
            GrantedBy = "g",
            Status = ConsentStatus.Active,
            ExpiresAt = null
        };
        consent.ObservedStatus(DateTime.UtcNow).Should().Be(ConsentStatus.Active);
    }
}
