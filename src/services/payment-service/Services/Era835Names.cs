namespace PaymentService.Services;

/// <summary>
/// Name elements of the 835 1000A/1000B N1 segments, shared by
/// <see cref="EraGeneratorService"/> and <see cref="BatchEraGeneratorService"/>
/// (payments, denials and reversals).
/// </summary>
public static class Era835Names
{
    /// <summary>005010X221A1 N102 (name) maximum length.</summary>
    public const int MaxNameLength = 60;

    /// <summary>
    /// The N102 value for <paramref name="name"/>. Truncation policy: X12
    /// delimiters are replaced by spaces, the name is trimmed, cut at
    /// <see cref="MaxNameLength"/> characters and trimmed again (no trailing
    /// space left by the cut). A longer name (claims-service accepts 300) is
    /// shortened, never a reason to fail the 835; the payee is identified by
    /// its NPI in N104 regardless.
    /// </summary>
    public static string N102(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return string.Empty;
        var escaped = name.Replace("*", " ").Replace("~", " ").Replace(":", " ").Replace("\\", " ").Trim();
        return escaped.Length <= MaxNameLength ? escaped : escaped[..MaxNameLength].TrimEnd();
    }
}
