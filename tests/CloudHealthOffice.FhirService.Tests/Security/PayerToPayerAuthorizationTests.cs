using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using SecurityClaim = System.Security.Claims.Claim;

namespace CloudHealthOffice.FhirService.Tests.Security;

/// <summary>
/// The fhir-service host with Payer-to-Payer data served for <c>test-tenant</c>
/// and an ACTIVE Payer-to-Payer opt-in consent on file for pat-002. pat-001 is
/// "member A" (the caller in the patient-token attack), pat-002 is "member B"
/// (the victim). Everything else is the shared test host.
/// </summary>
public sealed class PayerToPayerWebAppFactory : FhirTestWebAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // The P2P member sources serve exactly one tenant (G-30 / T-4);
                // make it the tenant the test tokens carry.
                ["FhirAdapters:TenantId"] = "test-tenant",

                // Member B has opted in to Payer-to-Payer exchange.
                ["Cms0057:PayerToPayerConsent:ConsentsByTenant:test-tenant:3:MemberId"] = "pat-002",
                ["Cms0057:PayerToPayerConsent:ConsentsByTenant:test-tenant:3:ConsentId"] = "consent-p2p-pat-002",
                ["Cms0057:PayerToPayerConsent:ConsentsByTenant:test-tenant:3:PurposeOfUse"] = "PayerToPayerExchange",
                ["Cms0057:PayerToPayerConsent:ConsentsByTenant:test-tenant:3:Status"] = "Active",
            });
        });
    }
}

/// <summary>
/// D-13 / G-02 / T-1. HTTP-level authorization of the two INBOUND
/// Payer-to-Payer operations, <c>PayerToPayer/$member-data-export</c> (P2P-01)
/// and <c>Patient/$member-match</c> (P2P-04).
///
/// Both are payer-to-payer, backend-to-backend operations: under CMS-0057-F
/// and the Da Vinci PDex IG the NEW payer's system requests the member's data
/// from the PRIOR payer, using backend (client-credentials) authorization. The
/// member's part is the opt-in, which CHO checks server-side. No patient- or
/// user-context token is a payer, so neither may invoke them — and in
/// particular a member's own patient token must never be able to name a
/// different member in the request body and pull that member's export.
/// </summary>
public class PayerToPayerAuthorizationTests : IClassFixture<PayerToPayerWebAppFactory>
{
    private const string ExportPath = "/fhir/r4/PayerToPayer/$member-data-export";
    private const string MatchPath = "/fhir/r4/Patient/$member-match";

    /// <summary>The one scope that opens both inbound payer-to-payer operations.</summary>
    private const string P2PScope = "system/PayerToPayer.read";

    private static readonly JsonSerializerOptions FhirJson =
        new JsonSerializerOptions().ForFhir(typeof(Patient).Assembly);

    private readonly PayerToPayerWebAppFactory _factory;

    public PayerToPayerAuthorizationTests(PayerToPayerWebAppFactory factory) => _factory = factory;

    private async Task<HttpResponseMessage> PostAsync(string path, string token, object body)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static object ExportBody(string memberId) => new { receivingPayerId = "NEW-PLAN", memberId };

    private static object MatchBody(string memberId) => new { receivingPayerId = "NEW-PLAN", memberId };

    private string PatientToken(string patientId, string scopes = "patient/*.read")
        => _factory.IssueToken(scopes, patientId);

    private string BackendToken(string scopes = P2PScope, string tenantId = "test-tenant")
        => _factory.IssueToken(scopes, patientId: null, tenantId: tenantId,
            extraClaims: [new SecurityClaim("client_id", "new-plan-backend")]);

    private static async Task<string> DiagnosticsAsync(HttpResponseMessage response)
    {
        var outcome = JsonSerializer.Deserialize<OperationOutcome>(
            await response.Content.ReadAsStringAsync(), FhirJson)!;
        return outcome.Issue.Single().Diagnostics;
    }

    // ── $member-data-export ───────────────────────────────────────────────────

    [Fact]
    public async Task Export_PatientTokenForMemberA_CannotExportMemberB()
    {
        // The attack D-13 describes: member A's own patient token, member B's
        // id in the body, member B has opted in. On main this returned 200 with
        // member B's package.
        var response = await PostAsync(ExportPath, PatientToken("pat-001"), ExportBody("pat-002"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("pat-002");
    }

    [Fact]
    public async Task Export_PatientToken_IsRefusedUniformly_WhateverMemberTheBodyNames()
    {
        // Patient tokens are not admitted at all, so the answer for the token's
        // own member, another opted-in member, and a member who does not exist
        // is the same refusal — nothing about existence or consent leaks.
        var own = await PostAsync(ExportPath, PatientToken("pat-001"), ExportBody("pat-001"));
        var other = await PostAsync(ExportPath, PatientToken("pat-001"), ExportBody("pat-002"));
        var missing = await PostAsync(ExportPath, PatientToken("pat-001"), ExportBody("pat-404"));

        own.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        other.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        missing.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var diagnostics = await DiagnosticsAsync(own);
        (await DiagnosticsAsync(other)).Should().Be(diagnostics);
        (await DiagnosticsAsync(missing)).Should().Be(diagnostics);
    }

    [Fact]
    public async Task Export_PatientTokenThatAlsoHoldsSystemScopes_IsStillRefused()
    {
        // A token bound to a patient is a patient token, however else it is
        // scoped (CallerIdentityResolver.ClassifyCaller takes the narrow reading).
        var response = await PostAsync(ExportPath,
            PatientToken("pat-001", "patient/*.read " + P2PScope), ExportBody("pat-002"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("pat-002");
    }

    [Theory]
    [InlineData("user/*.read")]
    [InlineData("user/PayerToPayer.read")]
    public async Task Export_UserContextToken_IsRefused(string scopes)
    {
        // A provider app is not a payer either.
        var response = await PostAsync(ExportPath, _factory.IssueToken(scopes), ExportBody("pat-002"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Export_BackendTokenWithTheExplicitP2PScope_ExportsTheOptedInMember()
    {
        // Positive control: the legitimate backend flow still works.
        var response = await PostAsync(ExportPath, BackendToken(P2PScope), ExportBody("pat-002"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("resourceType").GetString().Should().Be("Bundle");
        doc.RootElement.GetProperty("entry").GetArrayLength().Should().BeGreaterThan(0);
        doc.RootElement.ToString().Should().Contain("pat-002");
    }

    [Fact]
    public async Task Export_BackendToken_MemberWithoutOptIn_IsRefused()
        // pat-001 has only a ProviderAccess consent, which does not open P2P.
        => (await PostAsync(ExportPath, BackendToken(), ExportBody("pat-001")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task Export_BackendTokenForAnotherTenant_CannotReachThisTenantsMember()
        => (await PostAsync(ExportPath, BackendToken(tenantId: "other-tenant"), ExportBody("pat-002")))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

    [Fact]
    public async Task Export_BackendTokenWithoutTheScope_IsRefused()
        => (await PostAsync(ExportPath, BackendToken("system/Coverage.read"), ExportBody("pat-002")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

    // ── Explicit scope only (review of #1294) ────────────────────────────────
    //
    // A wildcard such as system/*.read is what a Provider Access bulk client,
    // or any backend client of any trusted issuer, typically holds. It must not
    // open the payer-to-payer operations: only the dedicated scope does.

    [Theory]
    [InlineData("system/*.read")]
    [InlineData("system/*.*")]
    [InlineData("system/Patient.read")]
    public async Task Export_BackendTokenWithoutTheExplicitP2PScope_IsRefused(string scopes)
        => (await PostAsync(ExportPath, BackendToken(scopes), ExportBody("pat-002")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Theory]
    [InlineData("system/*.read")]
    [InlineData("system/*.*")]
    [InlineData("system/Patient.read")]
    public async Task MemberMatch_BackendTokenWithoutTheExplicitP2PScope_IsRefused(string scopes)
        => (await PostAsync(MatchPath, BackendToken(scopes), MatchBody("pat-002")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task Export_P2PScopePlusAPatientScopeInScp_IsRefused()
    {
        // `scp` carries a patient-context scope and there is no patient claim.
        // The caller is a patient-context caller (CallerIdentityResolver reads
        // scope AND scp), so it is not a payer backend.
        var token = _factory.IssueToken(P2PScope, patientId: null,
            extraClaims: [new SecurityClaim("scp", "patient/*.read"), new SecurityClaim("client_id", "mixed-client")]);

        (await PostAsync(ExportPath, token, ExportBody("pat-002")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task MemberMatch_P2PScopePlusAUserScopeInScp_IsRefused()
    {
        var token = _factory.IssueToken(P2PScope, patientId: null,
            extraClaims: [new SecurityClaim("scp", "user/*.read")]);

        (await PostAsync(MatchPath, token, MatchBody("pat-002")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SmartConfiguration_AdvertisesTheP2PScope()
    {
        using var client = _factory.CreateClient();
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/fhir/r4/.well-known/smart-configuration"));
        doc.RootElement.GetProperty("scopes_supported").EnumerateArray()
            .Select(s => s.GetString()).Should().Contain(P2PScope);
    }

    // ── $member-match ─────────────────────────────────────────────────────────

    [Fact]
    public async Task MemberMatch_PatientToken_IsRefused()
    {
        // A match resolves a member's identity and coverage; a patient token
        // must not be able to resolve somebody else's.
        var response = await PostAsync(MatchPath, PatientToken("pat-001"), MatchBody("pat-002"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("pat-002");
    }

    [Fact]
    public async Task MemberMatch_PatientTokenThatAlsoHoldsSystemScopes_IsRefused()
        => (await PostAsync(MatchPath, PatientToken("pat-001", "patient/*.read " + P2PScope), MatchBody("pat-002")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task MemberMatch_UserContextToken_IsRefused()
        => (await PostAsync(MatchPath, _factory.IssueToken("user/*.read"), MatchBody("pat-002")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task MemberMatch_BackendToken_Matches()
        => (await PostAsync(MatchPath, BackendToken(), MatchBody("pat-002")))
            .StatusCode.Should().Be(HttpStatusCode.OK);

    [Fact]
    public async Task MemberMatch_BackendTokenForAnotherTenant_DoesNotMatch()
        => (await PostAsync(MatchPath, BackendToken(tenantId: "other-tenant"), MatchBody("pat-002")))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
}
