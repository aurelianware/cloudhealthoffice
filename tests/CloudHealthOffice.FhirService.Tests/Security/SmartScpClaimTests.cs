using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
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

    // ── (b) Grants only in scp ────────────────────────────────────────────────

    [Fact]
    public async Task ScpOnly_SpaceDelimitedString_IsParsedLikeScope()
        => (await ExportAsync(Token(scope: null, extra: Scp("system/*.read system/Coverage.read"))))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);

    [Fact]
    public async Task ScpOnly_JsonArray_IsParsedLikeScope()
        // Two scp claims are written as a JSON array.
        => (await ExportAsync(Token(scope: null, extra: [Scp("system/Coverage.read"), Scp("system/*.read")])))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);

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
            .StatusCode.Should().Be(HttpStatusCode.Accepted);

    [Fact]
    public async Task BothClaims_SameSet_ScpAsAnArray_IsAccepted()
        => (await ExportAsync(Token("system/*.read system/Coverage.read",
                extra: [Scp("system/Coverage.read"), Scp("system/*.read")])))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);

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
    public async Task BothClaims_DifferentSets_AreRefused(string scope, string scp)
        => (await ExportAsync(Token(scope, extra: Scp(scp))))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

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
        (await ExportAsync(Token("system/*.read"))).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await GetAsync("/fhir/r4/Patient/pat-001", Token("patient/*.read", patient: "pat-001")))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync("/fhir/r4/Patient/pat-002", Token("patient/*.read", patient: "pat-001")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Claims the SMART scheme now sees under their JWT names ───────────────

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
