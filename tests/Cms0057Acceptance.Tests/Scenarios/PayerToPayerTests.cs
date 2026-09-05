using FhirService.Models;
using FhirService.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Cms0057Acceptance.Tests.Scenarios;

/// <summary>
/// P2P-02..04 — Payer-to-Payer exchange. Inbound respond (P2P-01) is now
/// implemented (see PayerToPayerExportTests); outbound initiation (P2P-02),
/// dedicated P2P consent (P2P-03, PARTIAL), and $member-match / concurrent
/// coverage (P2P-04) remain GAP/PARTIAL and are asserted honestly against the
/// REAL loaded product types and the consent registry.
///
/// Locked rule facts (documented, enforced by engagement work, not code yet):
///   5-year date-of-service lookback; exclude remittances and enrollee
///   cost-sharing; exclude drugs from the PA slice; opt-in required; a
///   concurrent-coverage exchange exists in the rule.
///
/// Traceability:
///   status   src/services/fhir-service/Services/FhirAdapterStatusService.cs (PayerToPayer = OutOfScope)
///   consent  src/services/consent-service/Models/Consent.cs (opt-in modeling)
/// </summary>
public class PayerToPayerTests
{
    private static FhirAdapterStatusReport Status()
    {
        var options = Options.Create(new FhirAdapterOptions()); // Demo defaults, empty overrides
        return new FhirAdapterStatusService(options, AcceptanceContext.EmptyConfig()).GetStatus();
    }

    // P2P-01 (inbound respond) is now implemented as real CHO Replace-mode
    // capability and proven behaviorally in PayerToPayerExportTests (member
    // resolution + consent gate + CHO-data FHIR export). The former GAP marker
    // here — which pinned the adapter layer as OutOfScope — has been removed now
    // that the respond path exists (the adapter-status report reflects it in Demo
    // mode). P2P-02 and P2P-04 remain GAP below.

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Kind", "GAP")]
    public void P2P02_Outbound_NoEnrollmentInitiationHook()
    {
        // GAP: no enrollment/opt-in-triggered outbound initiation or ingestion
        // target is wired. Assert there is no P2P initiation type in the loaded
        // assemblies.
        var initiatorExists = AcceptanceContext.ProductTypes()
            .Any(t => t.Name.Contains("PayerToPayerInitiat", StringComparison.OrdinalIgnoreCase)
                   || t.Name.Contains("P2PInitiat", StringComparison.OrdinalIgnoreCase));
        initiatorExists.Should().BeFalse();
    }

    [Fact]
    [Trait("Scenario", "P2P-03")]
    public void P2P03_OptInModeledInConsentRegistry_ButNoDedicatedP2pConsentType()
    {
        // PARTIAL: the consent registry can express opt-in (an Active consent),
        // but there is no dedicated Payer-to-Payer opt-in ConsentType value yet.
        var active = global::ConsentService.Models.ConsentStatus.Active;
        active.ToString().Should().Be("Active");

        var consentTypeNames = Enum.GetNames(typeof(global::ConsentService.Models.ConsentType));
        consentTypeNames.Should().NotContain(n =>
            n.Contains("PayerToPayer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Scenario", "P2P-04")]
    [Trait("Kind", "GAP")]
    public void P2P04_NoMemberMatchOrConcurrentCoverageSurface()
    {
        // GAP: no P2P $member-match / concurrent-coverage exchange exists in CHO
        // product code. (A framework/NuGet type may share the name; scope the
        // scan to product assemblies only.)
        var memberMatchExists = AcceptanceContext.ProductTypes()
            .Any(t => t.Name.Contains("MemberMatch", StringComparison.OrdinalIgnoreCase)
                   || t.Name.Contains("ConcurrentCoverage", StringComparison.OrdinalIgnoreCase));
        memberMatchExists.Should().BeFalse();
    }
}
