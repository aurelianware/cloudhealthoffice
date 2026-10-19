namespace CloudHealthOffice.Infrastructure.Logging;

/// <summary>
/// Makes caller-controlled values safe to put in a log line. This is the one
/// implementation for every CHO service: call <see cref="Sanitize(string?)"/>
/// on any value that came from a request, a message, a file or another system
/// before passing it to a logger, and <see cref="MaskId"/> on member and
/// subscriber identifiers.
/// </summary>
/// <remarks>
/// <para>
/// A newline in a logged value lets a caller forge a second log entry
/// (CodeQL <c>cs/log-forging</c>). Stripping only CR and LF is not enough:
/// NEL (U+0085), the Unicode line and paragraph separators (U+2028, U+2029),
/// vertical tab, form feed and the other control characters also split lines
/// in some sinks and viewers, and NUL truncates others.
/// </para>
/// <para>
/// The replacement is a per-character loop that rebuilds the string from
/// <see cref="char"/> values. CodeQL treats values of simple types such as
/// <see cref="char"/> as sanitized, so it recognises this method as a
/// log-forging sanitizer (a CR/LF-only <c>Replace</c> chain was not picked up
/// in this repository; see the history of
/// <c>AdjudicationController.SanitizeForLog</c>). Keep the loop shape when
/// changing this method.
/// </para>
/// </remarks>
public static class LogSanitizer
{
    /// <summary>The character that replaces each unsafe character.</summary>
    public const char Replacement = '_';

    /// <summary>What <see cref="MaskId"/> puts in front of the visible tail.</summary>
    public const string MaskPrefix = "***";

    /// <summary>How many trailing characters <see cref="MaskId"/> leaves visible.</summary>
    public const int MaskVisibleTail = 4;

    /// <summary>
    /// Returns <paramref name="value"/> with every control character
    /// (<see cref="char.IsControl(char)"/>: CR, LF, tab, NUL, NEL, ...) and
    /// every Unicode line or paragraph separator (U+2028, U+2029) replaced by
    /// <see cref="Replacement"/>. <c>null</c> and empty return
    /// <see cref="string.Empty"/>. The length is unchanged.
    /// </summary>
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var chars = new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            chars[i] = IsUnsafe(ch) ? Replacement : ch;
        }
        return new string(chars);
    }

    /// <summary>
    /// As <see cref="Sanitize(string?)"/>, and keeps at most
    /// <paramref name="maxLength"/> characters so a pathological value cannot
    /// flood the log pipeline.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLength"/> is not positive.</exception>
    public static string Sanitize(string? value, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var length = Math.Min(value.Length, maxLength);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            var ch = value[i];
            chars[i] = IsUnsafe(ch) ? Replacement : ch;
        }
        return new string(chars);
    }

    /// <summary>
    /// Masks a member or subscriber identifier for a log line: the value is
    /// sanitized as by <see cref="Sanitize(string?)"/>, then only its last
    /// <see cref="MaskVisibleTail"/> characters are kept behind
    /// <see cref="MaskPrefix"/> (<c>M00012345</c> becomes <c>***2345</c>).
    /// An identifier of <see cref="MaskVisibleTail"/> characters or fewer
    /// becomes <c>***</c>, since its tail would be the whole identifier.
    /// <c>null</c> and empty return <see cref="string.Empty"/>.
    /// </summary>
    /// <remarks>
    /// Logs are read by people triaging an incident, so the tail is kept to
    /// let them tell two members apart and match a ticket, without the log
    /// holding the identifier itself. Spans use a one-way hash instead
    /// (<c>ChoActivitySource.HashIdentifier</c>) because traces are joined by
    /// machines, not read.
    /// </remarks>
    public static string MaskId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return string.Empty;
        if (id.Length <= MaskVisibleTail) return MaskPrefix;

        return MaskPrefix + Sanitize(id[^MaskVisibleTail..]);
    }

    private static bool IsUnsafe(char ch) =>
        char.IsControl(ch) || ch == '\u2028' || ch == '\u2029';
}
