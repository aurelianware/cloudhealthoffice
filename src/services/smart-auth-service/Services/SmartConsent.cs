using System.Security.Cryptography;
using System.Text;
using System.Web;
using Microsoft.AspNetCore.DataProtection;
using SmartAuthService.Models;

namespace SmartAuthService.Services;

/// <summary>
/// The consent step of the SMART authorization endpoint: a page naming the
/// client and the scopes it asks for, posted back to <c>/connect/authorize</c>
/// with the original request parameters and an approve/deny decision.
///
/// The form carries a short-lived proof, protected with the shared Data
/// Protection key ring, of exactly which (identity, client, scope set) the page
/// was shown for. A decision is honoured only with a valid proof for the
/// signed-in identity and the request's client and scopes, so another site
/// cannot post an approval on the person's behalf (the session cookie is also
/// SameSite=Lax), and an approval cannot be replayed for wider scopes.
/// </summary>
public sealed class SmartConsent
{
    public const string DecisionField = "consent_decision";
    public const string TokenField = "consent_token";
    public const string Approve = "approve";
    public const string Deny = "deny";

    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly ITimeLimitedDataProtector _protector;

    public SmartConsent(IDataProtectionProvider provider)
        => _protector = provider.CreateProtector("SmartAuth.Consent.v1").ToTimeLimitedDataProtector();

    public string Issue(string identity, string clientId, IEnumerable<string> scopes)
        => _protector.Protect(Payload(identity, clientId, scopes), Lifetime);

    public bool Verify(string? token, string identity, string clientId, IEnumerable<string> scopes)
    {
        if (string.IsNullOrEmpty(token))
            return false;
        try
        {
            var expected = Encoding.UTF8.GetBytes(Payload(identity, clientId, scopes));
            var actual = Encoding.UTF8.GetBytes(_protector.Unprotect(token, out _));
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static string Payload(string identity, string clientId, IEnumerable<string> scopes)
        => string.Join('\n', identity, clientId,
            string.Join(' ', scopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));

    /// <summary>The consent page, in the style of the login page. Every value is HTML-encoded.</summary>
    public static string Page(
        string clientName, IEnumerable<string> scopes,
        IEnumerable<KeyValuePair<string, string>> parameters, string consentToken)
    {
        static string E(string? s) => HttpUtility.HtmlEncode(s ?? string.Empty);

        var display = SmartScopes.Catalog.ToDictionary(c => c.Name, c => c.Display, StringComparer.Ordinal);
        var items = new StringBuilder();
        foreach (var scope in scopes.Distinct(StringComparer.Ordinal))
        {
            var text = scope switch
            {
                "openid" => "Confirm who you are",
                SmartScopes.OfflineAccess => "Keep access when you are not using the app (a refresh token)",
                _ => display.TryGetValue(scope, out var d) ? d : scope,
            };
            items.Append($"<li>{E(text)} <code style=\"color:#666\">{E(scope)}</code></li>");
        }

        var hidden = new StringBuilder();
        foreach (var (key, value) in parameters)
            hidden.Append($"<input type=\"hidden\" name=\"{E(key)}\" value=\"{E(value)}\">");
        hidden.Append($"<input type=\"hidden\" name=\"{TokenField}\" value=\"{E(consentToken)}\">");

        var title = "Allow access?";
        return $"""
            <!DOCTYPE html>
            <html lang="en">
            <head><meta charset="utf-8"><title>{E(title)}</title></head>
            <body style="font-family:sans-serif;max-width:480px;margin:80px auto">
              <h2>{E(title)}</h2>
              <p><strong id="client-name">{E(clientName)}</strong> is asking to:</p>
              <ul id="scopes">{items}</ul>
              <form method="post" action="/connect/authorize">
                {hidden}
                <button type="submit" name="{DecisionField}" value="{Approve}" style="padding:8px 20px">Allow</button>
                <button type="submit" name="{DecisionField}" value="{Deny}" style="padding:8px 20px">Deny</button>
              </form>
              <p style="font-size:0.8em;color:#666">You will not be asked again for this app and these permissions.</p>
            </body>
            </html>
            """;
    }
}
