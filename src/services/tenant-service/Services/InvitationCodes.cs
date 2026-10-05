using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace TenantService.Services;

/// <summary>
/// Invitation codes: 256 random bits, base64url (43 characters). Only the
/// SHA-256 of a code is stored; the code itself is returned once.
/// </summary>
public static class InvitationCodes
{
    public const int CodeLength = 43;

    public static string NewCode() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string code)
        => WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    /// <summary>Shape check only, so obviously wrong input is refused without a lookup.</summary>
    public static bool IsWellFormed(string? code)
        => code is { Length: CodeLength }
           && code.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    public static string NormalizeEmail(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// <c>pat@acme.com</c> → <c>p***@acme.com</c>: enough for the invited person to
    /// pick the right account, without disclosing the whole address.
    /// </summary>
    public static string MaskEmail(string? email)
    {
        var value = (email ?? string.Empty).Trim();
        var at = value.LastIndexOf('@');
        if (at <= 0)
            return "***";
        return value[0] + "***" + value[at..];
    }
}

/// <summary>Bound from the <c>Invitations</c> section.</summary>
public sealed class InvitationOptions
{
    public const string SectionName = "Invitations";

    /// <summary>How long a code works after it is issued or resent.</summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>The portal origin used to build redemption links (<c>{PortalBaseUrl}/invite/{code}</c>).</summary>
    public string PortalBaseUrl { get; set; } = "https://portal.cloudhealthoffice.com";

    public void Validate()
    {
        if (Lifetime < TimeSpan.FromHours(1) || Lifetime > TimeSpan.FromDays(30))
            throw new InvalidOperationException($"{SectionName}:Lifetime must be between 1 hour and 30 days.");
        if (!Uri.TryCreate(PortalBaseUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException($"{SectionName}:PortalBaseUrl must be an absolute URL.");
    }

    public string RedemptionUrl(string code) => PortalBaseUrl.TrimEnd('/') + "/invite/" + code;
}
