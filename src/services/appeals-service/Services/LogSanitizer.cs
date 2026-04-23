namespace AppealsService.Services;

/// <summary>
/// Strips control characters (CR/LF/tab/NUL/etc.) from user-supplied
/// strings before they go into log messages. CodeQL's
/// <c>cs/log-forging</c> rule flags any logged value that originates from
/// request data because a newline in a tenant id, correlation id, or
/// appeal id lets an attacker inject synthetic log entries that corrupt
/// parsing and triage workflows. All CHO repositories, controllers, and
/// publishers that log user-originated fields (<c>TenantId</c>,
/// <c>AppealId</c>, <c>ClaimId</c>, <c>EventId</c>, <c>CorrelationId</c>,
/// external error reasons) MUST route them through
/// <see cref="SafeForLog"/> first.
///
/// Kept local to appeals-service; mirrors the shape personal-rep-service
/// established. Promotes to shared infra when a third service needs it.
/// </summary>
internal static class LogSanitizer
{
    /// <summary>
    /// Returns <paramref name="value"/> with all control characters replaced
    /// by <c>?</c>. Truncates to <paramref name="maxLength"/> (default 256)
    /// so a pathological value cannot blow up log storage or flood the
    /// downstream log pipeline.
    /// </summary>
    public static string SafeForLog(string? value, int maxLength = 256)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var capped = value.Length > maxLength ? value[..maxLength] : value;
        var chars = capped.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsControl(chars[i])) chars[i] = '?';
        }
        return new string(chars);
    }
}
