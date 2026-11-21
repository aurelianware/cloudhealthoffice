using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// Plays the user and the EHR for the SMART App Launch kit
/// (INFERNO-SMART-SERVER-001), whose app waits on a browser.
///
/// <para>
/// The kit is the app. Twice it stops and waits for a person: once for an EHR
/// to launch it (EHR launch), and once per launch for the user to sign in and
/// approve it at CHO's authorize endpoint, after which CHO redirects the
/// browser back to the kit. The harness does exactly what that person and
/// EHR would, through CHO's own pages and APIs: it signs in with the
/// Development login, redeems an enrolment code that binds the identity to
/// the seeded member (or provider), approves the consent page, and follows
/// CHO's redirect to the kit. Nothing in CHO is bypassed; a CHO that refused
/// any step fails the run with CHO's own answer.
/// </para>
///
/// <para>
/// The kit runs on the interop network, so the URLs it hands out name
/// in-network hosts. The harness runs on the host and maps each to its
/// published port; that is the only rewriting it does.
/// </para>
/// </summary>
public static class InfernoSmartApp
{
    /// <summary>The kit's SMART App Launch 2.2 suite, whose routes and keys the apps use.</summary>
    public const string Suite = "smart_stu2_2";

    /// <summary>The kit's address on the interop network, as INFERNO_HOST names it.</summary>
    public const string ContainerKitRoot = "http://inferno-smart:4567";

    private static readonly Regex MarkdownLink = new(@"\]\((?<url>https?://[^)\s]+)\)", RegexOptions.Compiled);

    /// <summary>Registers the standalone (patient) and EHR (provider) apps, and the provider user.</summary>
    public static async Task<string> RegisterAsync(ChoSeedFixture fixture, CancellationToken cancellationToken)
    {
        var app = fixture.SmartApp;
        using var admin = Admin(fixture);
        var smart = ChoUnderTest.HostSmartAuthUrl.TrimEnd('/');

        await PostAsync(admin, $"{smart}/api/admin/smart/clients", Registration(
            app.ClientId, "patient-app", app.DisplayName, ScopesOf(app.StandaloneScopes), app.RedirectUri),
            "the standalone app registration",
            cancellationToken, conflictIsOk: true);
        await PostAsync(admin, $"{smart}/api/admin/smart/clients", Registration(
            app.EhrClientId, "provider-app", $"{app.DisplayName}, EHR launch",
            ScopesOf(app.EhrScopes), app.RedirectUri), "the EHR launch app registration", cancellationToken, conflictIsOk: true);

        return $"SMART apps '{app.ClientId}' (patient app, standalone launch) and '{app.EhrClientId}' (provider app, " +
               $"EHR launch) registered with redirect URI {app.RedirectUri}";
    }

    /// <summary>
    /// The scopes one app requests, which are the ones it is registered for:
    /// a patient app may not hold <c>launch</c>, nor a provider app request
    /// what it was not registered for.
    /// </summary>
    private static IReadOnlyList<string> ScopesOf(string requested)
        => requested.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The registration body for one of the kit's apps.</summary>
    public static JsonObject Registration(
        string clientId, string kind, string displayName, IEnumerable<string> scopes, string redirectUri) => new()
    {
        ["clientId"] = clientId,
        ["kind"] = kind,
        ["displayName"] = displayName,
        ["scopes"] = new JsonArray(scopes.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
        ["redirectUris"] = new JsonArray(JsonValue.Create(redirectUri)),
        ["confidential"] = false,
    };

    /// <summary>The suite inputs for the standalone and EHR launch credentials.</summary>
    public static InfernoInput AuthInfo(string name, string clientId, string scopes) => new()
    {
        Name = name,
        Type = "auth_info",
        Value = JsonSerializer.Serialize(new
        {
            auth_type = "public",
            use_discovery = "true",
            client_id = clientId,
            requested_scopes = scopes,
            pkce_support = "enabled",
            pkce_code_challenge_method = "S256",
        }),
    };

    /// <summary>
    /// Answers one test that is waiting: the EHR launch, or the user's
    /// authorization. Any other wait is not the harness's to answer, and is
    /// left to time out where the kit reports it.
    /// </summary>
    public static async Task AnswerAsync(
        InfernoResult waiting, ChoSeedFixture fixture, string kitApiBaseUrl, CancellationToken cancellationToken)
    {
        var testId = waiting.TestId ?? "";
        var ehr = testId.Contains("ehr_launch", StringComparison.Ordinal);

        if (testId.EndsWith("smart_app_launch", StringComparison.Ordinal))
        {
            await LaunchAsync(fixture, kitApiBaseUrl, cancellationToken);
            return;
        }

        if (testId.EndsWith("app_redirect", StringComparison.Ordinal))
        {
            var authorizationUrl = AuthorizationUrl(waiting)
                ?? throw new InteropEnvironmentException(
                    $"The kit's redirect test {testId} is waiting without an authorization URL in its message.");
            await AuthorizeAsync(fixture, authorizationUrl, ehr, kitApiBaseUrl, cancellationToken);
        }
    }

    /// <summary>The authorization URL the kit's redirect test is waiting on, from its message.</summary>
    public static string? AuthorizationUrl(InfernoResult waiting)
    {
        var text = string.Join('\n',
            new[] { waiting.ResultMessage ?? "" }.Concat(waiting.Messages.Select(m => m.Message)));
        var link = MarkdownLink.Match(text);
        if (link.Success) return link.Groups["url"].Value;

        var info = Regex.Match(text, @"redirecting browser to (?<url>\S+?)\.?(\s|$)");
        return info.Success ? info.Groups["url"].Value : null;
    }

    /// <summary>Maps an in-network URL (CHO's or the kit's) to the host port the harness reaches it on.</summary>
    public static string ToHost(string url, string kitApiBaseUrl)
    {
        var kitRoot = kitApiBaseUrl.TrimEnd('/');
        if (kitRoot.EndsWith("/api", StringComparison.Ordinal)) kitRoot = kitRoot[..^4];
        return url
            .Replace(ChoUnderTest.ContainerSmartAuthUrl, ChoUnderTest.HostSmartAuthUrl.TrimEnd('/'), StringComparison.Ordinal)
            .Replace(ChoUnderTest.ContainerRootUrl, ChoUnderTest.HostRootUrl.TrimEnd('/'), StringComparison.Ordinal)
            .Replace(ContainerKitRoot, kitRoot, StringComparison.Ordinal);
    }

    /// <summary>The EHR: creates the launch context for the member, then opens the kit's launch URI.</summary>
    private static async Task LaunchAsync(ChoSeedFixture fixture, string kitApiBaseUrl, CancellationToken ct)
    {
        var app = fixture.SmartApp;
        using var admin = Admin(fixture);
        var smart = ChoUnderTest.HostSmartAuthUrl.TrimEnd('/');

        using var response = await admin.PostAsync($"{smart}/launch", JsonContent.Create(new
        {
            patientId = fixture.Member.MemberId,
            clientId = app.EhrClientId,
            practitionerId = app.EhrProviderId,
        }), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InteropEnvironmentException(
                $"smart-auth-service refused the EHR launch context with HTTP {(int)response.StatusCode}: {Redaction.Body(body)}");
        }

        var launch = JsonNode.Parse(body)?["launch"]?.GetValue<string>()
            ?? throw new InteropEnvironmentException("smart-auth-service registered a launch without a launch token.");

        var launchUri = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
            ToHost(app.LaunchUri, kitApiBaseUrl),
            new Dictionary<string, string?> { ["iss"] = ChoUnderTest.ContainerFhirBaseUrl, ["launch"] = launch });

        using var browser = Browser(out _);
        using var opened = await browser.GetAsync(launchUri, ct);
        if ((int)opened.StatusCode >= 400)
        {
            throw new InteropEnvironmentException(
                $"The kit answered its own launch URI with HTTP {(int)opened.StatusCode}.");
        }
    }

    /// <summary>
    /// The user: signs in, binds the identity, approves the app, and follows
    /// CHO's redirect back to the kit.
    /// </summary>
    private static async Task AuthorizeAsync(
        ChoSeedFixture fixture, string authorizationUrl, bool ehr, string kitApiBaseUrl, CancellationToken ct)
    {
        var app = fixture.SmartApp;
        var smart = ChoUnderTest.HostSmartAuthUrl.TrimEnd('/');
        using var admin = Admin(fixture);
        using var browser = Browser(out _);

        var username = ehr ? app.EhrUsername : app.Username;
        await ExpectRedirectAsync(
            browser.PostAsync($"{smart}/account/login", Form(("username", username), ("password", "Password123!")), ct),
            "the Development login");

        var code = ehr
            ? await EnrolAsync(admin, $"{smart}/api/admin/smart/provider-enrolments",
                new { providerId = app.EhrProviderId, npi = app.EhrProviderNpi }, "provider enrolment", ct)
            : await EnrolAsync(admin, $"{smart}/api/admin/smart/member-enrolments",
                new { memberId = fixture.Member.MemberId }, "member enrolment", ct);

        using (var link = await browser.PostAsync($"{smart}/account/link", Form(("code", code)), ct))
        {
            if (!link.IsSuccessStatusCode && link.StatusCode != HttpStatusCode.Conflict)
            {
                throw new InteropEnvironmentException(
                    $"smart-auth-service refused to link the {(ehr ? "provider" : "member")} enrolment with HTTP " +
                    $"{(int)link.StatusCode}: {Redaction.Body(await link.Content.ReadAsStringAsync(ct))}");
            }
        }

        using var first = await browser.GetAsync(ToHost(authorizationUrl, kitApiBaseUrl), ct);
        Uri location;
        if (first.StatusCode == HttpStatusCode.OK)
        {
            var html = await first.Content.ReadAsStringAsync(ct);
            var fields = InfernoPatientApp.ConsentFields(html);
            if (!fields.Any(f => f.Key == "consent_token"))
            {
                throw new InteropEnvironmentException(
                    "smart-auth-service answered the kit's authorization request with a page that is not its " +
                    $"consent page: {Redaction.Body(html)}");
            }
            fields.Add(new("consent_decision", "approve"));
            using var approved = await browser.PostAsync($"{smart}/connect/authorize", new FormUrlEncodedContent(fields), ct);
            location = RedirectLocation(approved, "the consent approval");
        }
        else
        {
            location = RedirectLocation(first, "the kit's authorization request");
        }

        // Back to the kit, carrying the code (or the error) CHO issued. The
        // kit judges what it receives; the harness only delivers it. A
        // redirect anywhere else (CHO's own login, an error page) would leave
        // the kit waiting until the run timed out, so it is reported here.
        var redirectPath = new Uri(app.RedirectUri).AbsolutePath;
        if (!string.Equals(location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString.Split('?')[0],
                redirectPath, StringComparison.Ordinal))
        {
            throw new InteropEnvironmentException(
                $"smart-auth-service sent the {(ehr ? "EHR" : "standalone")} authorization to {Redaction.Url(location.ToString())} " +
                $"rather than back to the app's redirect URI {app.RedirectUri}.");
        }

        using var back = await browser.GetAsync(ToHost(location.ToString(), kitApiBaseUrl), ct);
        if ((int)back.StatusCode >= 400)
        {
            throw new InteropEnvironmentException(
                $"The kit answered its own redirect URI with HTTP {(int)back.StatusCode}.");
        }
    }

    private static async Task<string> EnrolAsync(HttpClient admin, string url, object body, string what, CancellationToken ct)
    {
        using var response = await admin.PostAsync(url, JsonContent.Create(body), ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InteropEnvironmentException(
                $"smart-auth-service refused the {what} with HTTP {(int)response.StatusCode}: {Redaction.Body(text)}");
        }

        return JsonNode.Parse(text)?["code"]?.GetValue<string>() is { Length: > 0 } code
            ? code
            : throw new InteropEnvironmentException($"smart-auth-service issued a {what} without a code.");
    }

    private static async Task PostAsync(
        HttpClient http, string url, JsonObject body, string what, CancellationToken ct, bool conflictIsOk = false)
    {
        using var response = await http.PostAsync(url, JsonContent.Create(body), ct);
        if (conflictIsOk && response.StatusCode == HttpStatusCode.Conflict) return;
        if (!response.IsSuccessStatusCode)
        {
            throw new InteropEnvironmentException(
                $"smart-auth-service refused {what} with HTTP {(int)response.StatusCode}: " +
                Redaction.Body(await response.Content.ReadAsStringAsync(ct)));
        }
    }

    private static HttpClient Admin(ChoSeedFixture fixture)
    {
        var admin = new HttpClient { Timeout = InteropSettings.Timeouts.ProtocolCall };
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.DevelopmentToken());
        return admin;
    }

    private static HttpClient Browser(out CookieContainer cookies)
    {
        cookies = new CookieContainer();
        var handler = new HttpClientHandler { CookieContainer = cookies, UseCookies = true, AllowAutoRedirect = false, UseProxy = false };
        return new HttpClient(handler, disposeHandler: true) { Timeout = InteropSettings.Timeouts.ProtocolCall };
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

    private static async Task ExpectRedirectAsync(Task<HttpResponseMessage> call, string step)
    {
        using var response = await call;
        if (response.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.Found)
            || response.Headers.Location?.ToString().Contains("error=", StringComparison.Ordinal) == true)
        {
            throw new InteropEnvironmentException(
                $"smart-auth-service answered {step} with HTTP {(int)response.StatusCode}.");
        }
    }

    private static FormUrlEncodedContent Form(params (string Name, string Value)[] fields)
        => new(fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)));
}
