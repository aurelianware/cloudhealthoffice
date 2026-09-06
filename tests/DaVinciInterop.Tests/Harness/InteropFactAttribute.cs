namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// A test that crosses a real process/container boundary into third-party code.
///
/// Skipped unless CHO_INTEROP_ENABLED=1, so `dotnet test` on the solution never
/// downloads or starts an external reference implementation by accident. CI turns
/// it on explicitly in the Da Vinci Interoperability workflow.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class InteropFactAttribute : FactAttribute
{
    public InteropFactAttribute()
    {
        if (!InteropSettings.IsEnabled)
        {
            Skip = $"External interoperability scenarios are opt-in. Set {InteropSettings.EnabledVariable}=1 " +
                   "(or run scripts/interop/run.sh) to start the pinned external implementations.";
        }
    }
}
