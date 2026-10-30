using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// A member-bound SMART access token for a kit that needs one, obtained the
/// way a member's app would obtain it: a standalone patient launch against
/// cho-smart-auth-service.
///
/// <para>
/// The CARIN BB kit searches ExplanationOfBenefit by type, identifier and date
/// without naming a patient. CMS-0057-F Patient Access answers those searches
/// only for a token bound to one member, so the development token the other
/// suites use, which is bound to nobody, gets a 400 for every one of them
/// (nightly 2026-10-30). Nothing here mints a token: smart-auth-service issues
/// it, through the same consent and binding rules as any member's app.
/// </para>
///
/// <para>
/// The steps, each through a public smart-auth-service endpoint:
/// register the public client (admin API, development token); issue a member
/// enrolment for the seeded member (admin API); sign in with the Development
/// login, which smart-auth-service honours only on a Development host; redeem
/// the enrolment code, which binds that identity to the member; authorise with
/// PKCE and approve the consent page; exchange the code. The redirect URI is
/// never fetched: the code is read off the redirect.
/// </para>
/// </summary>
public static class InfernoPatientApp
{
    /// <summary>The admin API body that registers <paramref name="app"/>.</summary>
    public static JsonObject RegistrationBody(ChoSeedPatientApp app) => new()
    {
        ["clientId"] = app.ClientId,
        ["kind"] = "patient-app",
        ["displayName"] = app.DisplayName,
        ["scopes"] = new JsonArray(app.Scopes.Select(scope => (JsonNode)JsonValue.Create(scope)!).ToArray()),
        ["redirectUris"] = new JsonArray(JsonValue.Create(app.RedirectUri)),
        ["confidential"] = false,
    };

    /// <summary>The PKCE S256 challenge for <paramref name="verifier"/> (RFC 7636 §4.2).</summary>
    public static string CodeChallenge(string verifier)
        => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static readonly Regex HiddenInput = new(
        "<input type=\"hidden\" name=\"(?<name>[^\"]*)\" value=\"(?<value>[^\"]*)\">",
        RegexOptions.Compiled);

    /// <summary>
    /// The hidden fields of smart-auth-service's consent page: the original
    /// authorization request and the consent token that proves the page was
    /// shown to this signed-in identity.
    /// </summary>
    public static List<KeyValuePair<string, string>> ConsentFields(string html)
        => HiddenInput.Matches(html)
            .Select(m => new KeyValuePair<string, string>(
                WebUtility.HtmlDecode(m.Groups["name"].Value), WebUtility.HtmlDecode(m.Groups["value"].Value)))
            .ToList();

    /// <summary>The authorization code from a redirect to the app, checking its state.</summary>
    public static string CodeFromRedirect(Uri location, string redirectUri, string state)
    {
        if (!location.GetLeftPart(UriPartial.Path).Equals(redirectUri, StringComparison.Ordinal))
        {
            throw new InteropEnvironmentException(
                $"smart-auth-service redirected to {Redaction.Url(location.ToString())} rather than to the app.");
        }

        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(location.Query);
        if (query.TryGetValue("error", out var error))
        {
            throw new InteropEnvironmentException(
                $"smart-auth-service refused the patient launch: {error} " +
                (query.TryGetValue("error_description", out var description) ? description.ToString() : ""));
        }

        if (!query.TryGetValue("state", out var returned) || returned != state)
        {
            throw new InteropEnvironmentException("smart-auth-service returned a different state than the harness sent.");
        }

        return query.TryGetValue("code", out var code) && !string.IsNullOrEmpty(code)
            ? code.ToString()
            : throw new InteropEnvironmentException("smart-auth-service redirected to the app without a code.");
    }

    /// <summary>Runs the launch and returns the access token and the patient it is bound to.</summary>
    public static async Task<(string AccessToken, string Patient)> ObtainAsync(
        ChoSeedFixture fixture, CancellationToken cancellationToken)
    {
        var app = fixture.PatientApp;
        var smart = ChoUnderTest.HostSmartAuthUrl.TrimEnd('/');

        using var admin = new HttpClient { Timeout = InteropSettings.Timeouts.ProtocolCall };
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.DevelopmentToken());

        await RegisterAsync(admin, smart, app, cancellationToken);
        var enrolmentCode = await EnrolAsync(admin, smart, fixture.Member.MemberId, cancellationToken);

        using var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = false,
        };
        using var browser = new HttpClient(handler) { Timeout = InteropSettings.Timeouts.ProtocolCall };

        await ExpectAsync(
            browser.PostAsync($"{smart}/account/login", Form(
                ("username", app.Username), ("password", "Password123!")), cancellationToken),
            "the Development login", HttpStatusCode.Redirect, HttpStatusCode.Found);

        using (var link = await browser.PostAsync($"{smart}/account/link", Form(("code", enrolmentCode)), cancellationToken))
        {
            // 409: this identity is already bound, by an earlier run against a kept stack.
            if (!link.IsSuccessStatusCode && link.StatusCode != HttpStatusCode.Conflict)
            {
                throw new InteropEnvironmentException(
                    $"smart-auth-service refused to link the member enrolment with HTTP {(int)link.StatusCode}: " +
                    Redaction.Body(await link.Content.ReadAsStringAsync(cancellationToken)));
            }
        }

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var authorize = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = app.ClientId,
            ["redirect_uri"] = app.RedirectUri,
            ["scope"] = string.Join(' ', app.Scopes),
            ["state"] = state,
            ["aud"] = ChoUnderTest.ContainerFhirBaseUrl,
            ["code_challenge"] = CodeChallenge(verifier),
            ["code_challenge_method"] = "S256",
        };

        using var first = await browser.GetAsync(
            Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString($"{smart}/connect/authorize", authorize),
            cancellationToken);

        Uri location;
        if (first.StatusCode == HttpStatusCode.OK)
        {
            // The consent page. Approving it posts back the request it carries.
            var fields = ConsentFields(await first.Content.ReadAsStringAsync(cancellationToken));
            if (!fields.Any(f => f.Key == "consent_token"))
            {
                throw new InteropEnvironmentException(
                    "smart-auth-service answered the authorization request with a page that is not its consent page.");
            }
            fields.Add(new("consent_decision", "approve"));

            using var approved = await browser.PostAsync(
                $"{smart}/connect/authorize", new FormUrlEncodedContent(fields), cancellationToken);
            location = RedirectLocation(approved, "the consent approval");
        }
        else
        {
            // Already approved by an earlier run against a kept stack.
            location = RedirectLocation(first, "the authorization request");
        }

        var code = CodeFromRedirect(location, app.RedirectUri, state);

        using var token = await browser.PostAsync($"{smart}/connect/token", Form(
            ("grant_type", "authorization_code"),
            ("code", code),
            ("redirect_uri", app.RedirectUri),
            ("client_id", app.ClientId),
            ("code_verifier", verifier)), cancellationToken);
        var body = await token.Content.ReadAsStringAsync(cancellationToken);
        if (!token.IsSuccessStatusCode)
        {
            throw new InteropEnvironmentException(
                $"smart-auth-service refused the code exchange with HTTP {(int)token.StatusCode}: {Redaction.Body(body)}");
        }

        using var json = JsonDocument.Parse(body);
        var accessToken = json.RootElement.GetProperty("access_token").GetString()!;
        var patient = json.RootElement.TryGetProperty("patient", out var p) ? p.GetString() : null;
        if (patient != fixture.Member.MemberId)
        {
            throw new InteropEnvironmentException(
                $"smart-auth-service bound the token to patient '{patient}', not to the seeded member.");
        }

        return (accessToken, patient);
    }

    private static async Task RegisterAsync(HttpClient admin, string smart, ChoSeedPatientApp app, CancellationToken ct)
    {
        using var response = await admin.PostAsync(
            $"{smart}/api/admin/smart/clients", JsonContent.Create(RegistrationBody(app)), ct);
        if (response.StatusCode == HttpStatusCode.Conflict) return;
        if (!response.IsSuccessStatusCode)
        {
            throw new InteropEnvironmentException(
                $"smart-auth-service refused the patient app registration with HTTP {(int)response.StatusCode}: " +
                Redaction.Body(await response.Content.ReadAsStringAsync(ct)));
        }
    }

    private static async Task<string> EnrolAsync(HttpClient admin, string smart, string memberId, CancellationToken ct)
    {
        using var response = await admin.PostAsync(
            $"{smart}/api/admin/smart/member-enrolments", JsonContent.Create(new { memberId }), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InteropEnvironmentException(
                $"smart-auth-service refused the member enrolment with HTTP {(int)response.StatusCode}: {Redaction.Body(body)}");
        }

        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("code", out var code) && code.GetString() is { Length: > 0 } value
            ? value
            : throw new InteropEnvironmentException("smart-auth-service issued a member enrolment without a code.");
    }

    private static Uri RedirectLocation(HttpResponseMessage response, string step)
    {
        if (response.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.SeeOther)
            || response.Headers.Location is not { } location)
        {
            throw new InteropEnvironmentException(
                $"smart-auth-service answered {step} with HTTP {(int)response.StatusCode} rather than a redirect to the app.");
        }

        return location;
    }

    private static async Task ExpectAsync(Task<HttpResponseMessage> call, string step, params HttpStatusCode[] expected)
    {
        using var response = await call;
        if (!expected.Contains(response.StatusCode)
            || response.Headers.Location?.ToString().Contains("error=", StringComparison.Ordinal) == true)
        {
            throw new InteropEnvironmentException(
                $"smart-auth-service answered {step} with HTTP {(int)response.StatusCode}" +
                (response.Headers.Location is { } l ? $" to {Redaction.Url(l.ToString())}" : "") + ".");
        }
    }

    private static FormUrlEncodedContent Form(params (string Name, string Value)[] fields)
        => new(fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
