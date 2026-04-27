namespace CloudHealthOffice.ProviderService.Tests.TestHelpers;

/// <summary>
/// Deterministic polling helper for hosted-service / async-state tests.
/// <para>
/// Replaces fixed <c>Task.Delay(N)</c> sleeps that "give the work
/// time to drain" — those are timing-dependent and become flaky under
/// CI load. Polling against the actual condition with a bounded
/// timeout is both faster on the happy path (returns as soon as the
/// condition is true) and robust when the host is slow.
/// </para>
/// </summary>
public static class PollUntil
{
    /// <summary>
    /// Polls <paramref name="condition"/> until it returns true or
    /// <paramref name="timeout"/> elapses. Throws <see cref="TimeoutException"/>
    /// with <paramref name="description"/> if the condition never
    /// holds.
    /// </summary>
    public static async Task UntilAsync(
        Func<bool> condition,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        string? description = null,
        CancellationToken ct = default)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(5);
        var effectiveInterval = pollInterval ?? TimeSpan.FromMilliseconds(50);
        var deadline = DateTime.UtcNow + effectiveTimeout;

        while (!ct.IsCancellationRequested)
        {
            if (condition()) return;
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Condition '{description ?? "(unnamed)"}' did not hold within {effectiveTimeout}.");
            }
            try { await Task.Delay(effectiveInterval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Polls <paramref name="getValue"/> until it returns a non-null
    /// (or, for value types, non-default) value or the timeout
    /// elapses. Returns the resolved value.
    /// </summary>
    public static async Task<T> ForValueAsync<T>(
        Func<T?> getValue,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        string? description = null,
        CancellationToken ct = default)
        where T : class
    {
        T? observed = null;
        await UntilAsync(
            () =>
            {
                observed = getValue();
                return observed != null;
            },
            timeout,
            pollInterval,
            description,
            ct);
        return observed!;
    }
}
