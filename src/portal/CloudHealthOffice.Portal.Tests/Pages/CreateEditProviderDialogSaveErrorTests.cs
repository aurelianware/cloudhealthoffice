using System.Reflection;
using Bunit;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// A save that failed with anything other than ServiceUnavailableException was swallowed
/// (<c>catch (Exception) { }</c>): the dialog stayed open with no error. The failure is now
/// put in the dialog's error alert. The dialog body is not rendered outside a dialog host,
/// so the save is driven and the error read directly.
/// </summary>
public class CreateEditProviderDialogSaveErrorTests : TestContext
{
    [Fact]
    public async Task SaveFailure_IsShownInTheErrorAlert()
    {
        var providers = new Mock<IProviderService>();
        providers.Setup(p => p.CreateProviderAsync(It.IsAny<CreateProviderRequest>()))
            .ThrowsAsync(new HttpRequestException("NPI already registered"));
        Services.AddMudServices();
        Services.AddSingleton(providers.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = RenderComponent<CreateEditProviderDialog>();
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(null));

        var save = typeof(CreateEditProviderDialog).GetMethod("SaveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await cut.InvokeAsync(() => (Task)save.Invoke(cut.Instance, null)!);

        var error = (string?)typeof(CreateEditProviderDialog)
            .GetField("_serviceError", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cut.Instance);
        error.Should().Contain("NPI already registered");
    }
}
