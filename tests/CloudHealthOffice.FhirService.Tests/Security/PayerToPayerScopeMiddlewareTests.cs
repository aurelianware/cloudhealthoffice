using System.Security.Claims;
using System.Text.Json;
using FhirService.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.FhirService.Tests.Security;

/// <summary>
/// D-13, at the SMART scope middleware alone (no controller). The controllers
/// repeat the backend-only check, which would hide a regression here, so the
/// middleware's own rule is pinned separately: the inbound payer-to-payer
/// operations are system-context only and need the explicit
/// <c>system/PayerToPayer.read</c> scope — no wildcard satisfies it.
/// </summary>
public class PayerToPayerScopeMiddlewareTests
{
    private const string ExportPath = "/fhir/r4/PayerToPayer/$member-data-export";
    private const string MatchPath = "/fhir/r4/Patient/$member-match";

    /// <summary>Runs the real middleware; returns 200 when it let the request through.</summary>
    private static async Task<(int Status, string Body)> RunAsync(string path, string scope, string? patient = null)
    {
        var reached = false;
        var middleware = new SmartScopeEnforcementMiddleware(
            _ => { reached = true; return Task.CompletedTask; },
            NullLogger<SmartScopeEnforcementMiddleware>.Instance);

        var claims = new List<Claim> { new("sub", "client-1"), new("scope", scope) };
        if (patient != null) claims.Add(new Claim("patient", patient));

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = "POST";
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return (reached ? 200 : context.Response.StatusCode, body);
    }

    private static string Diagnostics(string body)
        => JsonDocument.Parse(body).RootElement.GetProperty("issue")[0].GetProperty("diagnostics").GetString()!;

    [Theory]
    [InlineData(ExportPath, "patient/*.read", "pat-001")]
    [InlineData(ExportPath, "patient/PayerToPayer.read", "pat-001")]
    [InlineData(MatchPath, "patient/*.read", "pat-001")]
    [InlineData(MatchPath, "patient/Patient.read", "pat-001")]
    [InlineData(ExportPath, "user/*.read", null)]
    [InlineData(ExportPath, "user/PayerToPayer.read", null)]
    [InlineData(MatchPath, "user/*.read", null)]
    [InlineData(MatchPath, "user/PayerToPayer.read", null)]
    public async Task PatientAndUserTokens_GetTheMiddlewaresInsufficientScopeOutcome(string path, string scope, string? patient)
    {
        var (status, body) = await RunAsync(path, scope, patient);

        status.Should().Be(403);
        Diagnostics(body).Should().StartWith("Insufficient scope for PayerToPayer")
            .And.Contain("system/PayerToPayer.read");
    }

    [Theory]
    [InlineData(ExportPath, "system/*.read")]
    [InlineData(ExportPath, "system/*.*")]
    [InlineData(MatchPath, "system/*.read")]
    [InlineData(MatchPath, "system/*.*")]
    [InlineData(MatchPath, "system/Patient.read")]
    [InlineData(ExportPath, "system/PayerToPayer.*")]
    public async Task WildcardOrOtherSystemScopes_DoNotSatisfyTheExplicitP2PScope(string path, string scope)
        => (await RunAsync(path, scope)).Status.Should().Be(403);

    [Theory]
    [InlineData(ExportPath)]
    [InlineData(MatchPath)]
    [InlineData("/fhir/r4/payertopayer/$member-data-export")]
    [InlineData("/fhir/r4/patient/$MEMBER-MATCH")]
    public async Task TheExplicitP2PScope_IsLetThrough(string path)
        => (await RunAsync(path, "system/PayerToPayer.read")).Status.Should().Be(200);
}
