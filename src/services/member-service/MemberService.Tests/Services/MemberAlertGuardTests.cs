using MemberService.Models;
using MemberService.Services;
using MemberService.Tests.Fakes;

namespace MemberService.Tests.Services;

public class MemberAlertGuardTests
{
    private const string Tenant = "t";
    private const string MemberId = "M-1";

    private static (MemberAlertGuard guard, InMemoryMemberAlertRepository repo) Build()
    {
        var repo = new InMemoryMemberAlertRepository();
        return (new MemberAlertGuard(repo), repo);
    }

    private static MemberAlert Active(MemberAlertType type) => new()
    {
        TenantId = Tenant, MemberId = MemberId, Id = Guid.NewGuid().ToString(),
        AlertType = type, Severity = MemberAlertSeverity.Critical,
        StartDate = DateTime.UtcNow.AddDays(-1), EndDate = null,
        Reason = "test", CreatedBy = "csr"
    };

    [Fact]
    public async Task Terminate_BlockedBy_LitigationHold()
    {
        var (guard, repo) = Build();
        repo.Alerts.Add(Active(MemberAlertType.LitigationHold));

        var block = await guard.EvaluateAsync(Tenant, MemberId, MemberAlertAction.Terminate);
        block.Should().NotBeNull();
        block!.Action.Should().Be(MemberAlertAction.Terminate);
        block.Alert.AlertType.Should().Be(MemberAlertType.LitigationHold);
    }

    [Fact]
    public async Task Terminate_BlockedBy_EligibilityDispute()
    {
        var (guard, repo) = Build();
        repo.Alerts.Add(Active(MemberAlertType.EligibilityDispute));

        var block = await guard.EvaluateAsync(Tenant, MemberId, MemberAlertAction.Terminate);
        block!.Alert.AlertType.Should().Be(MemberAlertType.EligibilityDispute);
    }

    [Fact]
    public async Task Terminate_NotBlockedBy_VIP()
    {
        var (guard, repo) = Build();
        repo.Alerts.Add(Active(MemberAlertType.VIP));

        var block = await guard.EvaluateAsync(Tenant, MemberId, MemberAlertAction.Terminate);
        block.Should().BeNull();
    }

    [Fact]
    public async Task Terminate_EndDatedLitigationHold_DoesNotBlock()
    {
        var (guard, repo) = Build();
        var ended = Active(MemberAlertType.LitigationHold);
        ended.EndDate = DateTime.UtcNow.AddMinutes(-1);
        repo.Alerts.Add(ended);

        var block = await guard.EvaluateAsync(Tenant, MemberId, MemberAlertAction.Terminate);
        block.Should().BeNull();
    }

    [Fact]
    public async Task UpdatePii_BlockedBy_SecurityFreeze()
    {
        var (guard, repo) = Build();
        repo.Alerts.Add(Active(MemberAlertType.SecurityFreeze));

        var block = await guard.EvaluateAsync(Tenant, MemberId, MemberAlertAction.UpdatePii);
        block!.Alert.AlertType.Should().Be(MemberAlertType.SecurityFreeze);
    }

    [Fact]
    public async Task NewEnrollment_BlockedBy_KnownFraudRisk()
    {
        var (guard, repo) = Build();
        repo.Alerts.Add(Active(MemberAlertType.KnownFraudRisk));

        var block = await guard.EvaluateAsync(Tenant, MemberId, MemberAlertAction.NewEnrollment);
        block!.Alert.AlertType.Should().Be(MemberAlertType.KnownFraudRisk);
    }

    [Fact]
    public async Task OutboundCommunication_BlockedBy_DoNotContact()
    {
        var (guard, repo) = Build();
        repo.Alerts.Add(Active(MemberAlertType.DoNotContact));

        var block = await guard.EvaluateAsync(Tenant, MemberId, MemberAlertAction.OutboundCommunication);
        block!.Alert.AlertType.Should().Be(MemberAlertType.DoNotContact);
    }

    [Fact]
    public async Task Terminate_LitigationHoldAtWarning_DoesNotBlock()
    {
        // LitigationHold's minimum severity for Terminate is Critical; a
        // Warning-severity hold is informational and must not block.
        var (guard, repo) = Build();
        var warn = Active(MemberAlertType.LitigationHold);
        warn.Severity = MemberAlertSeverity.Warning;
        repo.Alerts.Add(warn);

        var block = await guard.EvaluateAsync(Tenant, MemberId, MemberAlertAction.Terminate);
        block.Should().BeNull();
    }

    [Fact]
    public async Task Terminate_EligibilityDisputeAtInfo_DoesNotBlock()
    {
        // EligibilityDispute's minimum for Terminate is Warning; Info is too low.
        var (guard, repo) = Build();
        var info = Active(MemberAlertType.EligibilityDispute);
        info.Severity = MemberAlertSeverity.Info;
        repo.Alerts.Add(info);

        var block = await guard.EvaluateAsync(Tenant, MemberId, MemberAlertAction.Terminate);
        block.Should().BeNull();
    }

    [Fact]
    public async Task Terminate_EligibilityDisputeAtWarning_Blocks()
    {
        var (guard, repo) = Build();
        var warn = Active(MemberAlertType.EligibilityDispute);
        warn.Severity = MemberAlertSeverity.Warning;
        repo.Alerts.Add(warn);

        var block = await guard.EvaluateAsync(Tenant, MemberId, MemberAlertAction.Terminate);
        block!.Alert.AlertType.Should().Be(MemberAlertType.EligibilityDispute);
    }

    [Fact]
    public async Task Terminate_EligibilityDisputeAtCritical_AlsoBlocks()
    {
        // Critical >= Warning, so higher severity also triggers.
        var (guard, repo) = Build();
        var crit = Active(MemberAlertType.EligibilityDispute);
        crit.Severity = MemberAlertSeverity.Critical;
        repo.Alerts.Add(crit);

        var block = await guard.EvaluateAsync(Tenant, MemberId, MemberAlertAction.Terminate);
        block.Should().NotBeNull();
    }

    [Fact]
    public async Task EvaluateAsync_CancelledToken_Throws()
    {
        var (guard, repo) = Build();
        repo.Alerts.Add(Active(MemberAlertType.LitigationHold));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await guard.EvaluateAsync(
            Tenant, MemberId, MemberAlertAction.Terminate, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
