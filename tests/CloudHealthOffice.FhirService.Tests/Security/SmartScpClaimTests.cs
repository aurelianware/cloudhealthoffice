using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using SecurityClaim = System.Security.Claims.Claim;

namespace CloudHealthOffice.FhirService.Tests.Security;

/// <summary>
/// D-32: the <c>scope</c> and <c>scp</c> claims, through the real SMART scheme
/// and SmartScopeEnforcementMiddleware.
///
/// The rules:
/// <list type="bullet">
///   <item>A token carrying only <c>scp</c> is read exactly like one carrying
///   only <c>scope</c>. Each claim value is space-delimited; a JSON array
///   arrives as one claim per element and each element is split the same
///   way, so both a string and an array work.</item>
///   <item>A token carrying both is accepted only when the two hold the same
///   SET of scopes: split on spaces, empty entries dropped, duplicates
///   collapsed, order ignored, compared ordinally (scope tokens are
///   case-sensitive, RFC 6749 §3.3).</item>
///   <item>A token whose two claims differ is refused at authentication
///   (401), before any resource handling. Neither the union nor the
///   intersection is taken.</item>
///   <item>A token with neither claim holds no scopes, as before.</item>
/// </list>
/// </summary>
public class SmartScpClaimTests : IClassFixture<FhirTestWebAppFactory>
{
    private readonly FhirTestWebAppFactory _factory;

    public SmartScpClaimTests(FhirTestWebAppFactory factory) => _factory = factory;

    private static SecurityClaim Scp(string value) => new("scp", value);

    private string Token(string? scope, string? patient = null, params SecurityClaim[] extra)
        => _factory.IssueToken(scope, patient, extraClaims: extra);

    private async Task<HttpResponseMessage> ExportAsync(string token)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/fhir/r4/$export")
        {
            Content = new StringContent(string.Empty),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Prefer", "respond-async");
        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string token)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    // ── (a) The reviewer's case ───────────────────────────────────────────────
    //
    // System grants in `scope`, a patient-context scope only in `scp`, and no
    // patient claim. Before the fix `scp` was renamed by the handler, the
    // middleware never saw it, and the token was a system caller: it started a
    // bulk export of every member and read members through Provider Access.

    [Fact]
    public async Task SystemScopePlusPatientScp_CannotStartABulkExport()
        => (await ExportAsync(Token("system/*.read", extra: Scp("patient/*.read"))))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

    [Fact]
    public async Task SystemScopePlusPatientScp_CannotReadAMemberAsASystemCaller()
        => (await GetAsync("/fhir/r4/Patient/pat-001", Token("system/*.read", extra: Scp("patient/*.read"))))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

    [Fact]
    public async Task SystemScopePlusPatientScp_Refusal_IsAFhirChallenge_AndEchoesNoScope()
    {
        var response = await ExportAsync(Token("system/*.read", extra: Scp("patient/*.read")));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().Contain("Bearer");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("OperationOutcome");
        body.Should().NotContain("patient/*.read").And.NotContain("system/*.read");
    }

    // Authorized export calls reach the default unavailable exporter (501);
    // refusal before that boundary remains 401/403. Authentication semantics
    // are unchanged even though the fake successful export has been removed.

    // ── (b) Grants only in scp ────────────────────────────────────────────────

    [Fact]
    public async Task ScpOnly_SpaceDelimitedString_IsParsedLikeScope()
        => (await ExportAsync(Token(scope: null, extra: Scp("system/*.read system/Coverage.read"))))
            .StatusCode.Should().Be(HttpStatusCode.NotImplemented);

    [Fact]
    public async Task ScpOnly_JsonArray_IsParsedLikeScope()
        // Two scp claims are written as a JSON array.
        => (await ExportAsync(Token(scope: null, extra: [Scp("system/Coverage.read"), Scp("system/*.read")])))
            .StatusCode.Should().Be(HttpStatusCode.NotImplemented);

    [Fact]
    public async Task ScpOnly_PatientScope_ReadsItsOwnPatient()
        => (await GetAsync("/fhir/r4/Patient/pat-001", Token(scope: null, patient: "pat-001", Scp("patient/*.read"))))
            .StatusCode.Should().Be(HttpStatusCode.OK);

    [Fact]
    public async Task ScpOnly_PatientScope_IsStillBoundToItsPatient()
        => (await GetAsync("/fhir/r4/Patient/pat-002", Token(scope: null, patient: "pat-001", Scp("patient/*.read"))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task ScpOnly_PatientScope_WithoutAPatientClaim_CannotStartABulkExport()
        => (await ExportAsync(Token(scope: null, extra: Scp("patient/*.read"))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task ScpOnly_ReadScope_DoesNotGrantAWrite()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/fhir/r4/$submit-attachment")
        {
            Content = new StringContent("{\"resourceType\":\"Parameters\"}", Encoding.UTF8, "application/fhir+json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            Token(scope: null, extra: Scp("system/*.read")));

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── (c) Both claims, same set ─────────────────────────────────────────────

    [Theory]
    [InlineData("system/*.read system/Coverage.read", "system/*.read system/Coverage.read")]
    [InlineData("system/*.read system/Coverage.read", "system/Coverage.read system/*.read")]
    [InlineData("system/*.read  system/Coverage.read ", " system/Coverage.read system/*.read")]
    [InlineData("system/*.read system/*.read", "system/*.read")]
    public async Task BothClaims_SameSet_AreAccepted(string scope, string scp)
        => (await ExportAsync(Token(scope, extra: Scp(scp))))
            .StatusCode.Should().Be(HttpStatusCode.NotImplemented);

    [Fact]
    public async Task BothClaims_SameSet_ScpAsAnArray_IsAccepted()
        => (await ExportAsync(Token("system/*.read system/Coverage.read",
                extra: [Scp("system/Coverage.read"), Scp("system/*.read")])))
            .StatusCode.Should().Be(HttpStatusCode.NotImplemented);

    // ── (d) Both claims, different sets ───────────────────────────────────────

    [Theory]
    // scp WIDENS scope.
    [InlineData("system/Coverage.read", "system/Coverage.read system/*.read")]
    // scp NARROWS scope.
    [InlineData("system/*.read user/*.read", "system/*.read")]
    // Disjoint.
    [InlineData("system/*.read", "user/*.read")]
    // Case differs: scope tokens are case-sensitive.
    [InlineData("system/*.read", "System/*.read")]
    // An empty claim against a non-empty one.
    [InlineData("", "system/*.read")]
    [InlineData("system/*.read", "")]
    // An EMPTY JSON ARRAY against a non-empty one, both orderings. The JWT
    // handler creates no principal claim for `[]`, so the claim's presence is
    // read from the token payload.
    [InlineData(EmptyArray, "system/*.read")]
    [InlineData("system/*.read", EmptyArray)]
    public async Task BothClaims_DifferentSets_AreRefused(string scope, string scp)
        => (await ExportAsync(TokenWithBoth(scope, scp)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

    [Fact]
    public async Task BothClaims_EmptyArrays_HoldNoScopes()
        => (await ExportAsync(TokenWithBoth(EmptyArray, EmptyArray)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task EmptyArrayScope_Only_HoldsNoScopes()
        => (await ExportAsync(_factory.IssueToken(null,
                rawPayload: new Dictionary<string, object> { ["scope"] = Array.Empty<string>() })))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

    /// <summary>Marks a claim that is written as an empty JSON array.</summary>
    private const string EmptyArray = "[]";

    private string TokenWithBoth(string scope, string scp)
    {
        var raw = new Dictionary<string, object>();
        if (scope == EmptyArray) raw["scope"] = Array.Empty<string>();
        if (scp == EmptyArray) raw["scp"] = Array.Empty<string>();
        return _factory.IssueToken(scope == EmptyArray ? null : scope,
            extraClaims: scp == EmptyArray ? [] : [Scp(scp)], rawPayload: raw);
    }

    // ── /cds-services is outside /fhir/r4: refused at authentication too ─────

    [Fact]
    public async Task BothClaims_DifferentSets_AreRefused_AtTheCrdHook()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/cds-services/cho-order-sign")
        {
            Content = new StringContent(
                "{\"hookInstance\":\"h-1\",\"hook\":\"order-sign\",\"context\":{\"userId\":\"Practitioner/p1\","
                + "\"patientId\":\"pat-002\",\"draftOrders\":{\"resourceType\":\"Bundle\",\"entry\":[]}}}",
                Encoding.UTF8, "application/json"),
        };
        // Either set alone is a provider grant the hook serves (SmartGapTests).
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            Token("user/Coverage.read", extra: Scp("user/Coverage.read user/*.read")));

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task BothClaims_DifferentSets_RefusedEvenWhereEitherAloneWouldBeAllowed()
        // Each set on its own opens Patient/pat-001 for its bound patient.
        => (await GetAsync("/fhir/r4/Patient/pat-001",
                Token("patient/*.read", patient: "pat-001", Scp("patient/Patient.read"))))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

    // ── (e) Neither claim ─────────────────────────────────────────────────────

    [Fact]
    public async Task NeitherClaim_HoldsNoScopes_AsBefore()
    {
        (await GetAsync("/fhir/r4/Patient/pat-001", Token(scope: null, patient: "pat-001")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ExportAsync(Token(scope: null))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ScopeOnly_IsUnchanged()
    {
        (await ExportAsync(Token("system/*.read"))).StatusCode.Should().Be(HttpStatusCode.NotImplemented);
        (await GetAsync("/fhir/r4/Patient/pat-001", Token("patient/*.read", patient: "pat-001")))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync("/fhir/r4/Patient/pat-002", Token("patient/*.read", patient: "pat-001")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task BothClaims_DifferentSets_RefusalLogsClaimNamesOnly()
    {
        var capture = new CapturingLoggerProvider();
        using var host = _factory.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(capture)));
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/fhir/r4/Patient/pat-001");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            Token("system/SECRET-SCOPE-A.read", extra: Scp("system/SECRET-SCOPE-B.read")));

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var lines = capture.Messages.ToList();
        lines.Should().Contain(m => m.Contains("scope") && m.Contains("scp") && m.Contains("different scopes"));
        lines.Should().NotContain(m => m.Contains("SECRET-SCOPE"));
    }

    private sealed class CapturingLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(Messages);

        public void Dispose() { }

        private sealed class Logger(System.Collections.Concurrent.ConcurrentQueue<string> messages)
            : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
                Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => messages.Enqueue(formatter(state, exception) + (exception?.ToString() ?? string.Empty));
        }
    }

    // ── Claims the SMART scheme now sees under their JWT names ───────────────

    [Fact]
    public async Task SmartSubject_IsReadFromSub_ForProviderAccessAttribution()
        // test-user (the token's `sub`) is attributed to pat-001 with consent.
        // On main the handler renamed `sub` to NameIdentifier; now readers use `sub`.
        => (await GetAsync("/fhir/r4/Patient/pat-001", Token("system/*.read")))
            .StatusCode.Should().Be(HttpStatusCode.OK);

    [Fact]
    public async Task SmartToken_WritingChoServiceClaims_StillCannotUseAChoOnlyEndpoint()
    {
        // With inbound mapping off a SMART token's `roles` arrives as `roles`,
        // the CHO role claim name. A token that also writes the CHO service
        // issuer marker and explicit permissions is still a SMART token: the
        // CHO-only bulk-export refusal and CHO-only endpoints refuse it.
        var token = Token("system/*.read", extra:
        [
            new SecurityClaim("roles", "cho.service"),
            new SecurityClaim("roles", "TenantAdmin"),
            new SecurityClaim("cho_service_issuer", "true"),
            new SecurityClaim("permissions", "authorizations:read"),
        ]);

        (await GetAsync("/api/v1/crd/code-classification", token))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
