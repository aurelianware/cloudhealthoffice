namespace CloudHealthOffice.FeeScheduleEngine.Services;

/// <summary>
/// Strips control characters (CR/LF/tab/NUL/etc.) from request-derived
/// strings (procedure codes, schedule ids, resolution reasons) before they go
/// into log messages, so a crafted value cannot inject synthetic log entries
/// (CodeQL <c>cs/log-forging</c>). Mirrors the local helper in
/// personal-representative-service; kept local because the shared
/// infrastructure project would pull ASP.NET dependencies into this engine.
/// </summary>
internal static class LogSanitizer
{
    /// <summary>
    /// Returns <paramref name="value"/> with all control characters replaced
    /// by <c>?</c>, truncated to <paramref name="maxLength"/> (default 256).
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
