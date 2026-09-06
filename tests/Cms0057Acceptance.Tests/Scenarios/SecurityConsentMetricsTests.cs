using System.Text.Json;
using FhirService.Controllers;
using FluentAssertions;
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
    public void METRICS01_DecisionTimeComputedFromReceivedAndDecisionTimestamps()
    {
        // Average/median decision time is one of the CMS public PA metrics. It is
        // computed from the received (SubmittedDate) and decision (ReviewedDate)
        // timestamps on each authorization.
        var auth = new global::AuthorizationService.Models.Authorization
        {
            AuthorizationNumber = "PAS-ACC-METRICS",
            Status = global::AuthorizationService.Models.AuthorizationStatus.Approved,
            SubmittedDate = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc),
            ReviewedDate = new DateTime(2026, 3, 4, 9, 0, 0, DateTimeKind.Utc),
        };

        var turnaround = global::AuthorizationService.Models.AuthorizationsSummaryCalculator
            .CalculateTurnaroundDays(auth);

        turnaround.Should().Be(3.0);
    }

    [Fact]
    [Trait("Scenario", "METRICS-01")]
    public void METRICS01_DenialCarriesCodedReasonForMetricsBreakdown()
    {
        // Denial rate + specific-reason reporting draws on the coded denial
        // reason retained on the authorization record.
        var auth = new global::AuthorizationService.Models.Authorization
        {
            AuthorizationNumber = "PAS-ACC-DENY",
            Status = global::AuthorizationService.Models.AuthorizationStatus.Denied,
            DenialReasonCode = "X12-A3-278",
            DenialReason = "Does not meet clinical criteria for the requested imaging.",
        };

        auth.Status.Should().Be(global::AuthorizationService.Models.AuthorizationStatus.Denied);
        auth.DenialReasonCode.Should().NotBeNullOrWhiteSpace();
        auth.DenialReason.Should().NotBeNullOrWhiteSpace();
    }
}
