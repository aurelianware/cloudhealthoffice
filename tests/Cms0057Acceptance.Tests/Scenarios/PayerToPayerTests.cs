using FluentAssertions;

namespace Cms0057Acceptance.Tests.Scenarios;

/// <summary>
/// P2P-03 — Payer-to-Payer opt-in enforcement. Payer-to-Payer authorization is
/// now a first-class consent purpose on the one registry, enforced server-side in
/// both directions; the behavioural evidence is in PayerToPayerConsentTests and
/// the policy's own rules are pinned in
/// ConsentService.Tests/Services/ConsentAuthorizationPolicyTests. The rest of
/// the Payer-to-Payer set: inbound respond (P2P-01, PayerToPayerExportTests),
/// member-match / concurrent coverage (P2P-04, MemberMatchTests), and outbound
/// initiation with durable ingestion (P2P-02, PayerToPayerOutboundTests and
/// PayerToPayerIngestionTests).
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
    // Every Payer-to-Payer scenario is now implemented as real CHO Replace-mode
    // capability, proven behaviourally in its own suite. The GAP markers that
    // used to live here — pinning the adapter layer as OutOfScope, asserting no
    // member-match surface, asserting no outbound initiator type, and asserting
    // no dedicated Payer-to-Payer consent — have been removed as each path
    // landed. What remains PARTIAL for Payer-to-Payer is external-core (QNXT)
    // integration, which is a different axis entirely.

    [Fact]
    [Trait("Scenario", "P2P-03")]
    [Trait("Backend", "Replace")]
    public void P2P03_PayerToPayerHasItsOwnConsentPurpose_DistinctFromProviderAccess()
    {
        // The registry now distinguishes WHAT a consent authorizes. The
        // behavioural enforcement lives in PayerToPayerConsentTests; this pins
        // the vocabulary the two directions share.
        var purposes = Enum.GetNames(typeof(CloudHealthOffice.Consent.Contracts.ConsentPurposeOfUse));
        purposes.Should().Contain("PayerToPayerExchange");
        purposes.Should().Contain("ProviderAccess");

        // They are different values — one cannot stand in for the other.
        CloudHealthOffice.Consent.Contracts.ConsentPurposeOfUse.PayerToPayerExchange
            .Should().NotBe(CloudHealthOffice.Consent.Contracts.ConsentPurposeOfUse.ProviderAccess);

        // And the default authorizes nothing, so a record written before this
        // axis existed is not Payer-to-Payer authorization.
        new global::ConsentService.Models.Consent().PurposeOfUse
            .Should().Be(CloudHealthOffice.Consent.Contracts.ConsentPurposeOfUse.Unspecified);
    }
}
