using System.Security.Claims;
using System.Text.Json;
using FhirService.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Cms0057Acceptance.Tests.Scenarios;

/// <summary>
/// SEC-01 (SMART on FHIR / OAuth), CONSENT-01 (single consent registry), and
/// METRICS-01 (CMS public PA metric set), executed against the REAL
/// SmartConfigurationController, consent-service registry, and
/// authorization-service metric calculator in Demo/Cho mode.
///
/// Traceability:
///   smart    src/services/fhir-service/Controllers/SmartConfigurationController.cs
///   consent  src/services/consent-service/Services/ConsentStateMachine.cs, Models/Consent.cs
///   metrics  src/services/authorization-service/Models/AuthorizationsSummaryCalculator.cs, Authorization.cs
/// </summary>
public class SecurityConsentMetricsTests
{
    // ── SEC-01 SMART on FHIR / OAuth ────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "SEC-01")]
    public void SEC01_SmartConfiguration_AdvertisesOAuthEndpointsAndScopes()
    {
        var controller = new SmartConfigurationController(AcceptanceContext.DemoConfig())
            .WithTenant();

        var ok = controller.GetSmartConfiguration().Should().BeOfType<OkObjectResult>().Subject;

        // The anonymous well-known document is serialized and inspected as JSON.
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        var root = doc.RootElement;

        // IdP is per-customer (PARTIAL): the issuer is whatever SmartAuth:Issuer
        // is configured to — here the Demo issuer.
        root.GetProperty("issuer").GetString().Should().Be("https://auth.cloudhealthoffice.com");
        root.GetProperty("authorization_endpoint").GetString().Should().Contain("/connect/authorize");
        root.GetProperty("token_endpoint").GetString().Should().Contain("/connect/token");

        var scopes = root.GetProperty("scopes_supported").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        scopes.Should().Contain("launch/patient");
        scopes.Should().Contain("patient/ExplanationOfBenefit.read");

        root.GetProperty("code_challenge_methods_supported").EnumerateArray()
            .Select(e => e.GetString()).Should().Contain("S256");
    }

    // ── CONSENT-01 single registry ──────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    public void CONSENT01_SingleRegistry_ModelsOptInAndOptOutLifecycle()
    {
        // One consent registry keyed by tenant + member carries the opt-in
        // (Active) / opt-out (Revoked) lifecycle used for Provider Access and
        // Payer-to-Payer alike.
        var sm = typeof(global::ConsentService.Services.ConsentStateMachine);
        sm.Should().NotBeNull();

        global::ConsentService.Services.ConsentStateMachine
            .IsAllowed(global::ConsentService.Models.ConsentStatus.Draft,
                       global::ConsentService.Models.ConsentStatus.Active).Should().BeTrue();
        global::ConsentService.Services.ConsentStateMachine
            .IsAllowed(global::ConsentService.Models.ConsentStatus.Active,
                       global::ConsentService.Models.ConsentStatus.Revoked).Should().BeTrue();

        var consent = new global::ConsentService.Models.Consent
        {
            TenantId = AcceptanceContext.TenantId,
            MemberId = "pat-001",
            ConsentType = global::ConsentService.Models.ConsentType.GeneralAuthorization,
            Status = global::ConsentService.Models.ConsentStatus.Active,
            GrantedBy = "pat-001",
        };
        consent.MemberId.Should().Be("pat-001");
        consent.Status.Should().Be(global::ConsentService.Models.ConsentStatus.Active);
    }

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    public void CONSENT01_OneRegistryCarriesPurposeSpecificConsents()
    {
        // The registry distinguishes what each consent authorizes, on one
        // aggregate and one lifecycle — not two stores. Payer-to-Payer
        // enforcement reads it (see PayerToPayerConsentTests).
        var p2p = new global::ConsentService.Models.Consent
        {
            TenantId = AcceptanceContext.TenantId,
            MemberId = "pat-001",
            ConsentType = global::ConsentService.Models.ConsentType.GeneralAuthorization,
            PurposeOfUse = CloudHealthOffice.Consent.Contracts.ConsentPurposeOfUse.PayerToPayerExchange,
            Status = global::ConsentService.Models.ConsentStatus.Active,
            GrantedBy = "pat-001",
        };
        var providerAccess = new global::ConsentService.Models.Consent
        {
            TenantId = AcceptanceContext.TenantId,
            MemberId = "pat-001",
            ConsentType = global::ConsentService.Models.ConsentType.GeneralAuthorization,
            PurposeOfUse = CloudHealthOffice.Consent.Contracts.ConsentPurposeOfUse.ProviderAccess,
            Status = global::ConsentService.Models.ConsentStatus.Active,
            GrantedBy = "pat-001",
        };

        p2p.ToAuthorizationSnapshot().PurposeOfUse.Should()
            .NotBe(providerAccess.ToAuthorizationSnapshot().PurposeOfUse);
    }

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public void CONSENT01_BothPurposesEnforceThroughTheOneRegistry()
    {
        // This replaces the GAP test that asserted no Provider Access consent
        // enforcement existed. Both purposes are now enforced server-side, and
        // both reach their answer through the SAME evaluator and the SAME
        // ConsentAuthorizationPolicy — not two implementations that happen to
        // agree today.
        var evaluatorType = typeof(FhirService.Services.Consent.IConsentEvaluator);

        // Payer-to-Payer's gate holds no policy of its own: it delegates.
        typeof(FhirService.Services.PayerToPayer.ConsentRegistryPayerToPayerConsentGate)
            .GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType)
            .Should().Contain(evaluatorType);

        // Provider Access composes attribution with the same evaluator.
        typeof(FhirService.Services.ProviderAccess.ProviderAccessAuthorizationService)
            .GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType)
            .Should().Contain(evaluatorType)
            .And.Contain(typeof(FhirService.Services.ProviderAccess.IProviderAttributionSource));

        // And they ask for different purposes, so neither satisfies the other.
        FhirService.Services.ProviderAccess.ProviderAccessAuthorizationService.RequiredPurpose
            .Should().NotBe(
                FhirService.Services.PayerToPayer.ConsentRegistryPayerToPayerConsentGate.RequiredPurpose);
    }

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public void CONSENT01_OneRegistryNotTwo()
    {
        // A second consent store would be the real failure mode here. Every
        // consent read in fhir-service goes through the one IConsentSource seam,
        // whose production implementation is the consent-service registry.
        typeof(FhirService.Services.PayerToPayer.HttpConsentRegistryConsentSource)
            .Should().Implement<FhirService.Services.Consent.IConsentSource>();

        FhirService.Services.PayerToPayer.HttpConsentRegistryConsentSource.HttpClientName
            .Should().Be("ChoConsentService");
    }

    // ── METRICS-01 CMS public metric set ────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "METRICS-01")]
    [Trait("Backend", "Replace")]
    public void METRICS01_RealReportingSplitsUrgency_ComputesMedianAndTimeliness_AndNamesMissingMeasures()
    {
        var received = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        global::AuthorizationService.Models.Authorization Row(int hours, bool urgent, bool denied = false) => new()
        {
            SubmittedDate = received, ReviewedDate = received.AddHours(hours),
            LevelOfService = urgent ? "U" : "E",
            Status = denied ? global::AuthorizationService.Models.AuthorizationStatus.Denied
                : global::AuthorizationService.Models.AuthorizationStatus.Approved,
        };
        var metrics = global::AuthorizationService.Services.Cms0057.Cms0057Reporting.ComputeMetrics(
            [Row(24, false), Row(48, false), Row(192, false, true), Row(24, true), Row(96, true, true),
             new() { SubmittedDate = received, Status = global::AuthorizationService.Models.AuthorizationStatus.Submitted }],
            received, received.AddMonths(1), null);

        metrics.TotalRequests.Should().Be(6);
        metrics.OpenRequests.Should().Be(1);
        metrics.DecidedRequests.Should().Be(5);
        metrics.Standard.Requests.Should().Be(4);
        metrics.Standard.MedianDecisionHours.Should().Be(48);
        metrics.Standard.DecidedWithinTimeframe.Should().Be(2);
        metrics.Standard.Approved.Should().Be(2);
        metrics.Standard.Denied.Should().Be(1);
        metrics.Expedited.Requests.Should().Be(2);
        metrics.Expedited.MedianDecisionHours.Should().Be(60);
        metrics.Expedited.DecidedWithinTimeframe.Should().Be(1);
        metrics.NotYetTracked.Should().HaveCount(2,
            "approved-after-appeal and extended-then-approved remain gaps; this is partial capability");
    }

    // ── SEC-01 read/write scope separation ──────────────────────────────────────

    [Theory]
    [Trait("Scenario", "SEC-01")]
    [Trait("Backend", "Replace")]
    // A WRITE cannot be authorized by a read scope. Claim/$submit creates a
    // prior authorization; before this was enforced, `Claim.read` was enough.
    [InlineData("POST", "/fhir/r4/Claim/$submit", "system/Claim.read", 403)]
    [InlineData("POST", "/fhir/r4/Claim/$submit", "system/*.read", 403)]
    [InlineData("POST", "/fhir/r4/Claim/$submit", "system/Claim.write", 200)]
    [InlineData("POST", "/fhir/r4/Claim/$submit", "system/*.write", 200)]
    // …and an operation that only READS is not gratuitously escalated: an
    // inquiry and a member match are POSTs by shape, not by effect.
    [InlineData("POST", "/fhir/r4/Claim/$inquire", "system/Claim.read", 200)]
    // Member match is a payer-to-payer operation (D-13): only the explicit
    // system/PayerToPayer.read grants it. This row was system/Patient.read →
    // 200; a Patient (or wildcard) read grant no longer opens it.
    [InlineData("POST", "/fhir/r4/Patient/$member-match", "system/PayerToPayer.read", 200)]
    [InlineData("POST", "/fhir/r4/Patient/$member-match", "system/Patient.read", 403)]
    [InlineData("POST", "/fhir/r4/Patient/$member-match", "system/*.read", 403)]
    [InlineData("POST", "/fhir/r4/Questionnaire/$questionnaire-package", "user/*.read", 200)]
    // Plain REST follows the method: a GET reads, a POST/PUT writes.
    [InlineData("GET", "/fhir/r4/Patient/pat-001", "user/Patient.read", 200)]
    [InlineData("POST", "/fhir/r4/Questionnaire", "user/Questionnaire.read", 403)]
    [InlineData("POST", "/fhir/r4/QuestionnaireResponse", "user/QuestionnaireResponse.read", 403)]
    [InlineData("POST", "/fhir/r4/QuestionnaireResponse", "user/QuestionnaireResponse.write", 200)]
    // Questionnaire AUTHORING is CHO-admin only (D-15): no SMART scope, not
    // even a write wildcard, gets a Questionnaire write past the middleware.
    // (This row was 200 before: a provider app could rewrite payer forms.)
    [InlineData("POST", "/fhir/r4/Questionnaire", "user/Questionnaire.write", 403)]
    [InlineData("PUT", "/fhir/r4/Questionnaire/q1", "system/*.write", 403)]
    [InlineData("PUT", "/fhir/r4/Questionnaire/q1", "user/*.read", 403)]
    // An operation nobody classified still needs a scope, and a POST to it needs
    // a write one — it does not fall through unenforced.
    [InlineData("POST", "/fhir/r4/$some-future-operation", "system/*.read", 403)]
    [InlineData("POST", "/fhir/r4/$some-future-operation", "system/*.write", 200)]
    public async Task SEC01_Replace_ScopeEnforcementFollowsTheInteractionNotJustThePath(
        string method, string path, string scope, int expected)
    {
        (await ScopeCheckStatusAsync(method, path, scope)).Should().Be(expected);
    }

    [Fact]
    [Trait("Scenario", "SEC-01")]
    [Trait("Backend", "Replace")]
    public async Task SEC01_Replace_AReadWildcardNeverSatisfiesAWrite()
    {
        // The one property the whole separation rests on. A `.read` wildcard is
        // the scope almost every client actually holds.
        (await ScopeCheckStatusAsync("POST", "/fhir/r4/Claim/$submit", "patient/*.read"))
            .Should().Be(403);
        (await ScopeCheckStatusAsync("POST", "/fhir/r4/Claim/$submit", "user/*.read"))
            .Should().Be(403);
    }

    [Fact]
    [Trait("Scenario", "SEC-01")]
    [Trait("Backend", "Replace")]
    public void SEC01_Replace_EveryWriteScopeTheSurfaceNeedsIsDiscoverable()
    {
        // A scope the server requires but never advertises is a surface nobody
        // can call. The write scopes are new, so they have to appear in
        // .well-known/smart-configuration alongside the reads.
        var controller = new SmartConfigurationController(AcceptanceContext.DemoConfig())
            .WithTenant();

        var ok = controller.GetSmartConfiguration().Should().BeOfType<OkObjectResult>().Subject;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));

        var scopes = doc.RootElement.GetProperty("scopes_supported").EnumerateArray()
            .Select(e => e.GetString()).ToList();

        scopes.Should().Contain(["system/*.write", "user/*.write",
                                 "system/Claim.write", "system/Task.write"]);

        // …and no PATIENT-context write scope: every write this server serves is
        // a provider/payer transaction.
        scopes.Should().NotContain(s => s!.StartsWith("patient/", StringComparison.Ordinal)
                                        && s.EndsWith(".write", StringComparison.Ordinal));
    }

    /// <summary>
    /// Drives the REAL SMART scope middleware and reports the status it produced.
    /// 200 means the request reached the terminal delegate — the middleware
    /// allowed it.
    /// </summary>
    private static async Task<int> ScopeCheckStatusAsync(string method, string path, string scope)
    {
        var reached = false;

        var middleware = new FhirService.Middleware.SmartScopeEnforcementMiddleware(
            _ => { reached = true; return Task.CompletedTask; },
            AcceptanceContext.Logger<FhirService.Middleware.SmartScopeEnforcementMiddleware>());

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "acceptance-client"), new Claim("scope", scope)], "test"));

        await middleware.InvokeAsync(context);

        return reached ? 200 : context.Response.StatusCode;
    }
}
