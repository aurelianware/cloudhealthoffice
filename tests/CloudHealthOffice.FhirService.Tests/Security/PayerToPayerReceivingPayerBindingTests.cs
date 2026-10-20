using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FhirService.Models.PayerToPayer;
using FhirService.Services.PayerToPayer;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SecurityClaim = System.Security.Claims.Claim;

namespace CloudHealthOffice.FhirService.Tests.Security;

/// <summary>
/// The Payer-to-Payer host with the two inbound P2P services wrapped so a test
/// can see the request each one was handed — and so the receiving payer that
/// went into the audit entry and the log line.
/// </summary>
public sealed class PayerToPayerBindingWebAppFactory : PayerToPayerWebAppFactory
{
    public ConcurrentQueue<string> ExportReceivingPayers { get; } = new();
    public ConcurrentQueue<string> MatchReceivingPayers { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddScoped<PayerToPayerExchangeService>();
            services.AddScoped<IPayerToPayerExchangeService>(sp => new RecordingExchange(
                sp.GetRequiredService<PayerToPayerExchangeService>(), ExportReceivingPayers));
            services.AddScoped<PayerToPayerMemberMatchService>();
            services.AddScoped<IPayerToPayerMemberMatchService>(sp => new RecordingMatch(
                sp.GetRequiredService<PayerToPayerMemberMatchService>(), MatchReceivingPayers));
        });
    }

    private sealed class RecordingExchange(IPayerToPayerExchangeService inner, ConcurrentQueue<string> seen)
        : IPayerToPayerExchangeService
    {
        public async Task<PayerToPayerExportResult> RespondAsync(PayerToPayerExchangeRequest request, CancellationToken ct = default)
        {
            var result = await inner.RespondAsync(request, ct);
            seen.Enqueue(result.Audit?.ReceivingPayerId ?? request.ReceivingPayerId);
            return result;
        }
    }

    private sealed class RecordingMatch(IPayerToPayerMemberMatchService inner, ConcurrentQueue<string> seen)
        : IPayerToPayerMemberMatchService
    {
        public async Task<MemberMatchResult> MatchAsync(MemberMatchRequest request, CancellationToken ct = default)
        {
            var result = await inner.MatchAsync(request, ct);
            seen.Enqueue(result.Audit?.ReceivingPayerId ?? request.ReceivingPayerId);
            return result;
        }
    }
}

/// <summary>
/// D-13 / T-1 remainder: the receiving payer of an inbound Payer-to-Payer
/// request is the payer the authenticated backend client is REGISTERED to —
/// carried in the access token's <c>payer_id</c> claim, which smart-auth-service
/// mints from the client registration — never the payer the request body
/// names. A body naming a different payer is refused; a client with no payer
/// mapping is refused; the audit records the token's payer.
/// </summary>
public class PayerToPayerReceivingPayerBindingTests : IClassFixture<PayerToPayerBindingWebAppFactory>
{
    private const string ExportPath = "/fhir/r4/PayerToPayer/$member-data-export";
    private const string MatchPath = "/fhir/r4/Patient/$member-match";
    private const string P2PScope = "system/PayerToPayer.read";
    private const string PayerA = "PAYER-A";
    private const string PayerB = "PAYER-B";

    private static readonly JsonSerializerOptions FhirJson =
        new JsonSerializerOptions().ForFhir(typeof(Patient).Assembly);

    private readonly PayerToPayerBindingWebAppFactory _factory;

    public PayerToPayerReceivingPayerBindingTests(PayerToPayerBindingWebAppFactory factory) => _factory = factory;

    private string BackendToken(string? payerId, string clientId = "payer-a-backend")
    {
        var claims = new List<SecurityClaim> { new("client_id", clientId) };
        if (payerId != null) claims.Add(new SecurityClaim("payer_id", payerId));
        return _factory.IssueToken(P2PScope, patientId: null, extraClaims: claims);
    }

    private async Task<HttpResponseMessage> PostAsync(string path, string token, object body)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(CloudHealthOffice.FhirService.Tests.PayerToPayer.P2PTestConsent.Serialize(body, token), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<string> DiagnosticsAsync(HttpResponseMessage response)
    {
        var outcome = JsonSerializer.Deserialize<OperationOutcome>(
            await response.Content.ReadAsStringAsync(), FhirJson)!;
        return outcome.Issue.Single().Diagnostics;
    }

    // ── $member-data-export ───────────────────────────────────────────────────

    [Fact]
    public async Task Export_ClientRegisteredForPayerA_NamingPayerB_IsRefused()
    {
        var response = await PostAsync(ExportPath, BackendToken(PayerA),
            new { receivingPayerId = PayerB, memberId = "pat-002" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("pat-002");
        body.Should().NotContain(PayerB);
    }

    [Fact]
    public async Task Export_ClientWithNoPayerMapping_IsRefused()
    {
        var response = await PostAsync(ExportPath, BackendToken(payerId: null),
            new { receivingPayerId = PayerA, memberId = "pat-002" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("pat-002");
    }

    [Fact]
    public async Task Export_Refusals_AreUniform_WhateverMemberTheBodyNames()
    {
        // Mismatch and missing-mapping refusals happen before the member is
        // looked at, so an opted-in member, a member without opt-in and an
        // unknown member all get the same answer.
        var answers = new List<HttpResponseMessage>();
        foreach (var member in new[] { "pat-002", "pat-001", "pat-404" })
        {
            answers.Add(await PostAsync(ExportPath, BackendToken(PayerA),
                new { receivingPayerId = PayerB, memberId = member }));
            answers.Add(await PostAsync(ExportPath, BackendToken(payerId: null),
                new { receivingPayerId = PayerA, memberId = member }));
        }

        answers.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Forbidden);
        var diagnostics = await DiagnosticsAsync(answers[0]);
        foreach (var answer in answers)
            (await DiagnosticsAsync(answer)).Should().Be(diagnostics);
    }

    [Fact]
    public async Task Export_ClientRegisteredForPayerA_NamingPayerA_Exports_AndAuditsPayerA()
    {
        var response = await PostAsync(ExportPath, BackendToken(PayerA, clientId: "audit-export-same"),
            new { receivingPayerId = PayerA, memberId = "pat-002" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.ExportReceivingPayers.Should().Contain(PayerA);
    }

    [Fact]
    public async Task Export_SendsTheResourcesInFull_NotJustTheirBaseFields()
    {
        var response = await PostAsync(ExportPath, BackendToken(PayerA, clientId: "export-content"),
            new { receivingPayerId = PayerA, memberId = "pat-002" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var resources = doc.RootElement.GetProperty("entry").EnumerateArray()
            .Select(e => e.GetProperty("resource")).ToList();
        var patient = resources.Single(r => r.GetProperty("resourceType").GetString() == "Patient");
        patient.TryGetProperty("birthDate", out _).Should().BeTrue("the receiving payer needs the member's demographics");
        patient.TryGetProperty("name", out _).Should().BeTrue();
        var coverage = resources.Single(r => r.GetProperty("resourceType").GetString() == "Coverage");
        coverage.TryGetProperty("beneficiary", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Export_BodyOmitsReceivingPayer_UsesTheTokensPayer()
    {
        // The body field is optional now: the token says who the receiving
        // payer is, and that is what the audit records.
        var before = _factory.ExportReceivingPayers.Count;
        var response = await PostAsync(ExportPath, BackendToken("PAYER-OMIT"),
            new { memberId = "pat-002" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.ExportReceivingPayers.Skip(before).Should().Contain("PAYER-OMIT");
        _factory.ExportReceivingPayers.Should().NotContain(string.Empty);
    }

    [Fact]
    public async Task Export_PayerIdFromAnUnmarkedIssuer_IsIgnored_AndTheClientRefused()
    {
        // A trusted external IdP whose token carries payer_id — even the very
        // payer the body names. Only CHO's registration authority is believed
        // about a client's payer, so this client has no mapping.
        var token = _factory.IssueToken(P2PScope, patientId: null,
            extraClaims: [new SecurityClaim("client_id", "external-client"), new SecurityClaim("payer_id", PayerA)],
            issuer: PayerToPayerWebAppFactory.ExternalIssuer);

        var export = await PostAsync(ExportPath, token, new { receivingPayerId = PayerA, memberId = "pat-002" });
        var match = await PostAsync(MatchPath, token, new { receivingPayerId = PayerA, memberId = "pat-002" });

        export.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        match.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await export.Content.ReadAsStringAsync()).Should().NotContain("pat-002");

        // The same refusal as a registered client naming another payer.
        var mismatch = await PostAsync(ExportPath, BackendToken(PayerA), new { receivingPayerId = PayerB, memberId = "pat-002" });
        (await DiagnosticsAsync(export)).Should().Be(await DiagnosticsAsync(mismatch));
        (await DiagnosticsAsync(match)).Should().Be(await DiagnosticsAsync(mismatch));
    }

    [Fact]
    public async Task Export_ExternalIssuerToken_IsStillAuthenticated_OnlyThePayerIsWithheld()
    {
        // Control for the test above: the external issuer's token is valid and
        // admitted as a backend caller elsewhere (a body-less request reaches
        // the binding check, not a 401).
        var token = _factory.IssueToken(P2PScope, patientId: null,
            extraClaims: [new SecurityClaim("client_id", "external-client")],
            issuer: PayerToPayerWebAppFactory.ExternalIssuer);
        var response = await PostAsync(ExportPath, token, new { memberId = "pat-002" });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await DiagnosticsAsync(response)).Should().Contain("registered with Cloud Health Office",
            "it got past authentication and the backend-only check, and stopped at the payer binding");
    }

    [Fact]
    public async Task Export_TwoDifferentPayerIdClaims_AreNoMapping()
    {
        var token = _factory.IssueToken(P2PScope, patientId: null,
            extraClaims: [new SecurityClaim("client_id", "c"), new SecurityClaim("payer_id", PayerA), new SecurityClaim("payer_id", PayerB)]);
        (await PostAsync(ExportPath, token, new { receivingPayerId = PayerA, memberId = "pat-002" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Export_TwoIdenticalPayerIdClaims_AreNoMapping()
    {
        // Exactly one payer_id claim, not one distinct value.
        var token = _factory.IssueToken(P2PScope, patientId: null,
            extraClaims: [new SecurityClaim("client_id", "c"), new SecurityClaim("payer_id", PayerA), new SecurityClaim("payer_id", PayerA)]);
        (await PostAsync(ExportPath, token, new { receivingPayerId = PayerA, memberId = "pat-002" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── $member-match ─────────────────────────────────────────────────────────

    [Fact]
    public async Task MemberMatch_ClientRegisteredForPayerA_NamingPayerB_IsRefused()
    {
        var response = await PostAsync(MatchPath, BackendToken(PayerA),
            new { receivingPayerId = PayerB, memberId = "pat-002" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("pat-002");
        body.Should().NotContain(PayerB);
    }

    [Fact]
    public async Task MemberMatch_ClientWithNoPayerMapping_IsRefused()
    {
        var response = await PostAsync(MatchPath, BackendToken(payerId: null),
            new { receivingPayerId = PayerA, memberId = "pat-002" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("pat-002");
    }

    [Fact]
    public async Task MemberMatch_ClientRegisteredForPayerA_NamingPayerA_Matches_AndAuditsPayerA()
    {
        var response = await PostAsync(MatchPath, BackendToken("PAYER-MATCH"),
            new { receivingPayerId = "PAYER-MATCH", memberId = "pat-002" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.MatchReceivingPayers.Should().Contain("PAYER-MATCH");
    }

    [Fact]
    public async Task MemberMatch_BodyOmitsReceivingPayer_UsesTheTokensPayer()
    {
        // HRex/PDex $member-match carries no receiving-payer parameter; the
        // token's payer is the one recorded.
        var response = await PostAsync(MatchPath, BackendToken("PAYER-MATCH-OMIT"),
            new { memberId = "pat-002" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.MatchReceivingPayers.Should().Contain("PAYER-MATCH-OMIT");
    }
}
