using Microsoft.JSInterop;

namespace CloudHealthOffice.Portal.Theming;

/// <summary>
/// The signed-in user's current theme for this circuit. Seeded from the <c>cho-theme</c>
/// cookie by _Host.cshtml (so the first paint is already right), changed from the user
/// menu, and persisted back to the cookie through wwwroot/js/theme.js.
/// </summary>
public sealed class ThemeState
{
    public string Current { get; private set; } = ChoThemeNames.Enterprise;

    public bool IsSentinel => Current == ChoThemeNames.Sentinel;

    public event Action? Changed;

    public void Initialize(string? themeName) => Current = ChoThemeNames.Normalize(themeName);

    public async Task SetAsync(string themeName, IJSRuntime js)
    {
        var next = ChoThemeNames.Normalize(themeName);
        if (next == Current)
            return;

        Current = next;
        await js.InvokeVoidAsync("choTheme.set", next);
        Changed?.Invoke();
    }
}
