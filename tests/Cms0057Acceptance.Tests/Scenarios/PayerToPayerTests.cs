using FluentAssertions;

namespace Cms0057Acceptance.Tests.Scenarios;

/// <summary>
/// P2P-03 — Payer-to-Payer opt-in enforcement. The rest of the Payer-to-Payer
/// set is implemented as real Replace-mode capability: inbound respond (P2P-01,
/// see PayerToPayerExportTests), member-match / concurrent coverage (P2P-04, see
/// MemberMatchTests), and outbound initiation (P2P-02, see
/// PayerToPayerOutboundTests). Dedicated Payer-to-Payer consent semantics remain
/// PARTIAL and are asserted honestly against the real consent registry.
///
/// Locked rule facts (documented, enforced by engagement work, not code yet):
///   5-year date-of-service lookback; exclude remittances and enrollee
///   cost-sharing; exclude drugs from the PA slice; opt-in required.
///
/// Traceability:
///   consent  src/services/consent-service/Models/Consent.cs (opt-in modeling)
/// </summary>
public class PayerToPayerTests
{
    // P2P-01 (inbound respond), P2P-04 ($member-match / concurrent coverage), and
    // P2P-02 (outbound initiation) are implemented as real CHO Replace-mode
    // capability and proven behaviorally in PayerToPayerExportTests,
    // MemberMatchTests, and PayerToPayerOutboundTests. The GAP markers that used
    // to live here — pinning the adapter layer as OutOfScope, asserting no
    // member-match surface, and asserting no outbound initiator type — have been
    // removed as each path landed. P2P-03 remains PARTIAL below: opt-in is still
    // a generic Active consent, with no dedicated Payer-to-Payer ConsentType.

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
}
