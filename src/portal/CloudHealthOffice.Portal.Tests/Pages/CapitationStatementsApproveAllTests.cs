using Bunit;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// Approve All used to swallow each refusal (<c>catch { /* continue */ }</c>) and then
/// report every Generated statement as approved. A refused statement is now counted
/// and named, and only the ones that were approved are reported as such.
/// </summary>
public class CapitationStatementsApproveAllTests : TestContext
{
    private readonly Mock<ICapitationService> _capitation = new();
    private readonly Mock<IDialogService> _dialogs = new();
    private readonly Mock<ISnackbar> _snackbar = new();

    public CapitationStatementsApproveAllTests()
    {
        Services.AddMudServices();
        Services.AddSingleton(_capitation.Object);
        Services.AddSingleton(_dialogs.Object);
        Services.AddSingleton(_snackbar.Object);
        Services.AddSingleton(GatingUsers.With("billing:read", "payments:read").Object);
        JSInterop.Mode = JSRuntimeMode.Loose;

        _capitation.Setup(c => c.GetStatementsAsync(It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>()))
            .ReturnsAsync(new List<CapStatementSummary>
            {
                new() { Id = "s-1", StatementNumber = "CAP-1", Status = "Generated" },
                new() { Id = "s-2", StatementNumber = "CAP-2", Status = "Generated" },
                new() { Id = "s-3", StatementNumber = "CAP-3", Status = "Generated" },
            });
        _capitation.Setup(c => c.GetHeldNachaFilesAsync()).ReturnsAsync(new List<NachaHeldFile>());
        _dialogs.Setup(d => d.ShowMessageBox(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DialogOptions>()))
            .ReturnsAsync(true);
    }

    [Fact]
    public void ApproveAll_WithARefusedStatement_ReportsOnlyTheApprovedOnes_AndNamesTheRefusal()
    {
        _capitation.Setup(c => c.ApproveStatementAsync("s-2")).ThrowsAsync(new InvalidOperationException("already held"));
        var cut = RenderComponent<CapitationStatements>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("CAP-1"));

        cut.FindAll("button").First(b => b.TextContent.Contains("Approve All")).Click();

        cut.WaitForAssertion(() => _snackbar.Verify(s => s.Add("2 statements approved", Severity.Success,
            It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()), Times.Once));
        _snackbar.Verify(s => s.Add(It.Is<string>(m => m.Contains("1 statements not approved") && m.Contains("CAP-2") && m.Contains("already held")),
            Severity.Error, It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()), Times.Once);
        _snackbar.Verify(s => s.Add("3 statements approved", It.IsAny<Severity>(),
            It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()), Times.Never);
        _capitation.Verify(c => c.ApproveStatementAsync(It.IsAny<string>()), Times.Exactly(3));
    }

    [Fact]
    public void ApproveAll_WhenEveryStatementIsApproved_ReportsThemAll_AndNoError()
    {
        var cut = RenderComponent<CapitationStatements>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("CAP-1"));

        cut.FindAll("button").First(b => b.TextContent.Contains("Approve All")).Click();

        cut.WaitForAssertion(() => _snackbar.Verify(s => s.Add("3 statements approved", Severity.Success,
            It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()), Times.Once));
        _snackbar.Verify(s => s.Add(It.IsAny<string>(), Severity.Error,
            It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()), Times.Never);
    }
}
