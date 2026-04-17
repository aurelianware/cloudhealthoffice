using System.Text;

namespace MemberService.Services;

/// <summary>
/// PII identifier normalization used before fingerprint hashing and duplicate
/// detection. Strips dashes, spaces, parentheses, dots, slashes; uppercases.
/// Intentionally lossy — callers still persist the original plaintext
/// (encrypted) in <c>MemberIdentifier.Value</c>; this is only for dedupe.
/// </summary>
public static class IdentifierNormalization
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '-':
                case ' ':
                case '(':
                case ')':
                case '.':
                case '/':
                case '_':
                    continue;
                default:
                    sb.Append(char.ToUpperInvariant(ch));
                    break;
            }
        }
        return sb.ToString();
    }
}
