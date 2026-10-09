using Bunit;
using CloudHealthOffice.Portal.Dialogs;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// PR #1278 follow-up 4: a claim routed to another queue (NCCI, say) that
/// also carries a COB pend as an additional reason. claims-service refuses
/// its approval without a payer order, and the portal only asked for one
/// when the routing pend code was COB — so it could not be approved from
/// the portal. Both approval paths now ask whenever any pend is COB and
/// send <c>payerSequence</c>.
/// </summary>
public class CobAdditionalPendApprovalTests : TestContext
{
    private readonly Mock<IWorkQueueService> _workQueue = new();
    private readonly Mock<IClaimsService> _claims = new();
    private readonly Mock<IUserContextService> _users = new();
    private readonly Mock<IDialogService> _dialogs = new();

    public CobAdditionalPendApprovalTests()
    {
        Services.AddMudServices();
        Services.AddSingleton(_workQueue.Object);
        Services.AddSingleton(_claims.Object);
        Services.AddSingleton(_users.Object);
        Services.AddSingleton(_dialogs.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;

        _users.Setup(u => u.GetCurrentUserAsync()).ReturnsAsync(new UserContext
        {
            UserId = "examiner-1", Email = "examiner@test", TenantId = "t1",
            Roles = ["ClaimsExaminer"], Permissions = new HashSet<string>(["claims:read", "claims:work"]),
        });
        _users.Setup(u => u.HasPermission(It.IsAny<string>())).Returns(true);

        // The examiner confirms this plan is secondary.
        var reference = new Mock<IDialogReference>();
        reference.Setup(r => r.Result).ReturnsAsync(
            DialogResult.Ok(new CobPayerOrderDialog.CobPayerOrderChoice(2, "secondary per EOB")));
        _dialogs.Setup(d => d.ShowAsync<CobPayerOrderDialog>(It.IsAny<string>())).ReturnsAsync(reference.Object);

        _workQueue.Setup(w => w.ResolvePendedClaimAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>()))
            .ReturnsAsync(ClaimResolutionResult.Resolved);
    }

    private void QueueHolds(WorkQueueItem item)
    {
        _workQueue.Setup(w => w.GetQueueSummaryAsync()).ReturnsAsync(new WorkQueueSummary { NcciEditFailures = 1 });
        _workQueue.Setup(w => w.GetQueueItemsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>()))
            .ReturnsAsync([item]);
    }

    private static WorkQueueItem NcciItem(params string[] pendReasons) => new()
    {
        ClaimId = "CLM-7",
        QueueReason = "NCCI Edit Failure",
        QueueReasonCode = "NCCI",
        PendReasons = pendReasons.ToList(),
        PendFingerprint = "v2-abc",
    };

    [Fact]
    public void WorkQueueOverride_WithAnAdditionalCobPend_AsksForThePayerOrder_AndSendsIt()
    {
        QueueHolds(NcciItem("NCCI: bundled pair 99214/99213", "COB: cob-payer-order-mismatch"));
        var cut = RenderComponent<WorkQueues>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("CLM-7"));

        cut.FindAll("button").First(b => b.TextContent.Contains("Override")).Click();

        cut.WaitForAssertion(() => _workQueue.Verify(w => w.ResolvePendedClaimAsync(
            "CLM-7", "Approved", "secondary per EOB", It.IsAny<string?>(), "examiner-1", 2, "v2-abc"), Times.Once));
        _dialogs.Verify(d => d.ShowAsync<CobPayerOrderDialog>(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void WorkQueueOverride_WithoutACobPend_DoesNotAsk()
    {
        QueueHolds(NcciItem("NCCI: bundled pair 99214/99213", "MEDREVIEW: manual review"));
        var cut = RenderComponent<WorkQueues>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("CLM-7"));

        cut.FindAll("button").First(b => b.TextContent.Contains("Override")).Click();

        cut.WaitForAssertion(() => _workQueue.Verify(w => w.ResolvePendedClaimAsync(
            "CLM-7", "Approved", It.IsAny<string>(), It.IsAny<string?>(), "examiner-1", null, "v2-abc"), Times.Once));
        _dialogs.Verify(d => d.ShowAsync<CobPayerOrderDialog>(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void ClaimDetailsApprove_WithAnAdditionalCobPend_AsksForThePayerOrder_AndSendsIt()
    {
        _claims.Setup(c => c.GetClaimByIdAsync("CLM-8")).ReturnsAsync(new ClaimDetails
        {
            ClaimId = "CLM-8",
            ClaimNumber = "CN-8",
            Status = "Pended",
            PendDetails = new ClaimPendDetails
            {
                PendCode = "NCCI",
                PendReason = "bundled pair 99214/99213",
                AdditionalPendReasons = ["COB: cob-payer-order-mismatch"],
                Fingerprint = "v2-def",
            },
        });
        _claims.Setup(c => c.GetDuplicateMatchesAsync(It.IsAny<string>())).ReturnsAsync(new List<ClaimDuplicateMatch>());
        var cut = RenderComponent<ClaimDetailsNew>(p => p.Add(x => x.ClaimId, "CLM-8"));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("CN-8"));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Approve").Click();

        cut.WaitForAssertion(() => _workQueue.Verify(w => w.ResolvePendedClaimAsync(
            "CLM-8", "Approved", "secondary per EOB", It.IsAny<string?>(), "examiner-1", 2, "v2-def"), Times.Once));
    }

    [Theory]
    [InlineData("COB", new string[0], true)]
    [InlineData("cob", new string[0], true)]
    [InlineData("NCCI", new[] { "NCCI: bundled", "COB: cob-payer-order-mismatch" }, true)]
    [InlineData("NCCI", new[] { " cob : lower-case code" }, true)]
    [InlineData("NCCI", new[] { "NCCI: bundled", "MEDREVIEW: COB documents attached" }, false)]
    [InlineData("NCCI", new[] { "Coordination of benefits: no code" }, false)]
    [InlineData("NCCI", new string[0], false)]
    public void CobPend_DetectsTheRoutingOrAnAdditionalCobPend(string routing, string[] reasons, bool expected)
    {
        CobPend.Any(routing, reasons).Should().Be(expected);
        CobPend.On(new WorkQueueItem { QueueReasonCode = routing, PendReasons = reasons.ToList() }).Should().Be(expected);
        CobPend.On(new ClaimPendDetails { PendCode = routing, AdditionalPendReasons = reasons.ToList() }).Should().Be(expected);
    }

    [Fact]
    public void WorkQueueItem_ReadsTheServicesPendReasons()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var item = System.Text.Json.JsonSerializer.Deserialize<WorkQueueItem>(
            "{\"claimId\":\"C\",\"queueReasonCode\":\"NCCI\",\"pendReasons\":[\"NCCI: x\",\"COB: y\"]}", options)!;

        item.PendReasons.Should().Equal("NCCI: x", "COB: y");
        CobPend.On(item).Should().BeTrue();
    }
}
