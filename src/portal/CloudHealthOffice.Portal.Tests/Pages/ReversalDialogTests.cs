using Bunit;
using CloudHealthOffice.Portal.Dialogs;
using CloudHealthOffice.Portal.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// The Reverse claim dialog voids the claim through the claims service and
/// only reports success when the service did it.
/// </summary>
public class ReversalDialogTests : TestContext
{
    private readonly Mock<IClaimsService> _claims = new();

    public ReversalDialogTests()
    {
        Services.AddMudServices();
        Services.AddSingleton(_claims.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private async Task<(IRenderedFragment Provider, Task<DialogResult?> Result)> Open()
    {
        var provider = Render(builder =>
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
        });
        var dialogs = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<ReversalDialog> { { x => x.ClaimId, "claim-1" } };
        var reference = await provider.InvokeAsync(() => dialogs.ShowAsync<ReversalDialog>("Reverse claim", parameters));
        return (provider, reference.Result);
    }

    [Fact]
    public async Task Reversing_voids_the_claim_with_the_reason()
    {
        _claims.Setup(c => c.VoidClaimAsync("claim-1", "Duplicate of CLM-9")).ReturnsAsync((string?)null);
        var (provider, result) = await Open();

        provider.WaitForAssertion(() => provider.Find("textarea[data-testid=reversal-reason]"));
        provider.Find("textarea[data-testid=reversal-reason]").Change("  Duplicate of CLM-9 ");
        provider.Find("[data-testid=reversal-submit]").Click();

        provider.WaitForAssertion(() => result.IsCompleted.Should().BeTrue());
        (await result)!.Canceled.Should().BeFalse();
        _claims.Verify(c => c.VoidClaimAsync("claim-1", "Duplicate of CLM-9"), Times.Once);
    }

    [Fact]
    public async Task A_refusal_keeps_the_dialog_open_and_says_why()
    {
        _claims.Setup(c => c.VoidClaimAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync("Claim must be Paid, PartiallyPaid or Adjusted to void; current status is Pended");
        var (provider, result) = await Open();

        provider.WaitForAssertion(() => provider.Find("textarea[data-testid=reversal-reason]"));
        provider.Find("textarea[data-testid=reversal-reason]").Change("paid in error");
        provider.Find("[data-testid=reversal-submit]").Click();

        provider.WaitForAssertion(() => provider.Find("[data-testid=reversal-error]").TextContent.Should().Contain("current status is Pended"));
        result.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task No_reason_no_reversal()
    {
        var (provider, _) = await Open();

        provider.WaitForAssertion(() => provider.Find("[data-testid=reversal-submit]"));
        provider.Find("[data-testid=reversal-submit]").HasAttribute("disabled").Should().BeTrue();
    }
}
