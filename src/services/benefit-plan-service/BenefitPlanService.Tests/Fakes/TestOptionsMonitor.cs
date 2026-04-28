using Microsoft.Extensions.Options;

namespace BenefitPlanService.Tests.Fakes;

/// <summary>
/// Mutable <see cref="IOptionsMonitor{T}"/> implementation for tests that
/// configure capability BP 5.6 options at-construction. Production code
/// uses <c>OptionsMonitor</c> from the configuration system.
/// </summary>
internal sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
{
    public TestOptionsMonitor(T value) { CurrentValue = value; }
    public T CurrentValue { get; set; }
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
