using PersonalRepresentativeService.Models;
using PersonalRepresentativeService.Services;

namespace PersonalRepresentativeService.Tests.Models;

public class PersonalRepStateMachineTests
{
    [Theory]
    [InlineData(PersonalRepStatus.Draft,  PersonalRepStatus.Active)]
    [InlineData(PersonalRepStatus.Draft,  PersonalRepStatus.Inactive)]
    [InlineData(PersonalRepStatus.Active, PersonalRepStatus.Inactive)]
    public void LegalTransitions_Allowed(PersonalRepStatus from, PersonalRepStatus to)
    {
        PersonalRepStateMachine.IsAllowed(from, to).Should().BeTrue();
        Action act = () => PersonalRepStateMachine.EnsureAllowed(from, to);
        act.Should().NotThrow();
    }

    [Theory]
    // Draft -> Draft (idempotent no-op, not a transition)
    [InlineData(PersonalRepStatus.Draft,    PersonalRepStatus.Draft)]
    // Active -> Active (idempotent no-op), Active -> Draft (backwards)
    [InlineData(PersonalRepStatus.Active,   PersonalRepStatus.Active)]
    [InlineData(PersonalRepStatus.Active,   PersonalRepStatus.Draft)]
    // Inactive is terminal — no outgoing edges
    [InlineData(PersonalRepStatus.Inactive, PersonalRepStatus.Draft)]
    [InlineData(PersonalRepStatus.Inactive, PersonalRepStatus.Active)]
    [InlineData(PersonalRepStatus.Inactive, PersonalRepStatus.Inactive)]
    public void IllegalTransitions_Throw(PersonalRepStatus from, PersonalRepStatus to)
    {
        PersonalRepStateMachine.IsAllowed(from, to).Should().BeFalse();
        Action act = () => PersonalRepStateMachine.EnsureAllowed(from, to);
        act.Should().Throw<InvalidPersonalRepTransitionException>()
            .Where(e => e.FromStatus == from && e.ToStatus == to);
    }

    /// <summary>
    /// Exhaustive guard: enumerate every (from, to) pair in the 3x3 matrix
    /// and confirm the allowed set is EXACTLY the three transitions above.
    /// Ensures a future enum addition forces an explicit decision rather
    /// than silently widening the state machine.
    /// </summary>
    [Fact]
    public void Matrix_AllowsOnlyThreeTransitions()
    {
        var expected = new HashSet<(PersonalRepStatus, PersonalRepStatus)>
        {
            (PersonalRepStatus.Draft,  PersonalRepStatus.Active),
            (PersonalRepStatus.Draft,  PersonalRepStatus.Inactive),
            (PersonalRepStatus.Active, PersonalRepStatus.Inactive)
        };

        var all = (PersonalRepStatus[])Enum.GetValues(typeof(PersonalRepStatus));
        foreach (var f in all)
        foreach (var t in all)
        {
            var allowed = PersonalRepStateMachine.IsAllowed(f, t);
            var shouldBeAllowed = expected.Contains((f, t));
            allowed.Should().Be(shouldBeAllowed,
                $"transition {f} -> {t} allowed should be {shouldBeAllowed}");
        }
    }

    [Theory]
    [InlineData(PersonalRepStatus.Active,   60, PersonalRepStatus.Active)]
    [InlineData(PersonalRepStatus.Active,   -1, PersonalRepStatus.Inactive)]
    [InlineData(PersonalRepStatus.Draft,    -1, PersonalRepStatus.Draft)]
    [InlineData(PersonalRepStatus.Inactive, -1, PersonalRepStatus.Inactive)]
    public void ObservedStatus_ProjectsExpiryForActiveOnly(
        PersonalRepStatus persisted, int minutesFromNow, PersonalRepStatus expected)
    {
        var now = new DateTime(2026, 4, 22, 12, 0, 0, DateTimeKind.Utc);
        var rep = new PersonalRepresentative
        {
            TenantId = "t",
            CredentialType = PersonalRepCredentialType.LegalGuardian,
            Status = persisted,
            ExpiresAt = now.AddMinutes(minutesFromNow)
        };

        rep.ObservedStatus(now).Should().Be(expected);
    }

    [Fact]
    public void ObservedStatus_NoExpiresAt_ReturnsPersisted()
    {
        var rep = new PersonalRepresentative
        {
            TenantId = "t",
            CredentialType = PersonalRepCredentialType.Parent,
            Status = PersonalRepStatus.Active,
            ExpiresAt = null
        };
        rep.ObservedStatus(DateTime.UtcNow).Should().Be(PersonalRepStatus.Active);
    }
}
