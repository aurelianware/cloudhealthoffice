using Cms0057Acceptance.Tests.TestSupport;
using FhirService.Models;
using FhirService.Models.PayerToPayer;
using FhirService.Services;
using FhirService.Services.PayerToPayer;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Cms0057Acceptance.Tests.Scenarios;

/// <summary>
/// P2P-01 — Payer-to-Payer inbound respond as REAL Cloud Health Office
/// Replace-mode capability. A receiving payer supplies a transitioning member's
/// identifiers and opt-in; Cloud Health Office (the prior payer) matches the
/// member deterministically and returns a member-scoped FHIR export package from
/// its own authoritative data.
///
/// These scenarios exercise the SAME production classes the running service
/// binds — <see cref="PayerToPayerExchangeService"/>,
/// <see cref="PayerToPayerMemberResolver"/>, the tenant-scoped
/// <see cref="PatientAccessPayerToPayerMemberSource"/> (over the CHO Patient
/// Access data provider), and <see cref="PayerToPayerExportBuilder"/> (reusing
/// the existing CARIN/US Core <see cref="FhirService.Mappers.PatientAccessMapper"/>).
/// Synthetic data only; no PHI.
///
/// P2P-04 ($member-match / concurrent coverage) and P2P-02 (outbound initiation)
/// are separate paths, covered by MemberMatchTests and PayerToPayerOutboundTests.
///
/// Traceability:
///   service   src/services/fhir-service/Services/PayerToPayer/PayerToPayerExchangeService.cs
///   resolver  src/services/fhir-service/Services/PayerToPayer/PayerToPayerMemberResolver.cs
///   source    src/services/fhir-service/Services/PayerToPayer/PayerToPayerMemberSource.cs
///   builder   src/services/fhir-service/Services/PayerToPayer/PayerToPayerExportBuilder.cs
///   mapper    src/services/fhir-service/Mappers/PatientAccessMapper.cs
/// </summary>
public class PayerToPayerExportTests
{
    // Members with a server-side opt-in on record for the demo tenant. Consent is
    // decided here, never from the request — a caller cannot self-attest it.
    private static IPayerToPayerConsentGate Gate(params string[] membersWithP2pConsent) =>
        AcceptanceContext.PayerToPayerConsentGate(membersWithP2pConsent);

    private static PayerToPayerExchangeService ServiceOverCho(params string[] optedInMembers)
    {
        var provider = new MockPatientAccessDataProvider();
        var source = new PatientAccessPayerToPayerMemberSource(
            provider, Options.Create(new FhirAdapterOptions { TenantId = AcceptanceContext.TenantId }));
        return new PayerToPayerExchangeService(
            new PayerToPayerMemberResolver(source), source, Gate(optedInMembers),
            new PayerToPayerExportBuilder(), AcceptanceContext.Logger<PayerToPayerExchangeService>());
    }

    private static PayerToPayerExchangeService ServiceOverSource(
        IPayerToPayerMemberSource source, params string[] optedInMembers) =>
        new(new PayerToPayerMemberResolver(source), source, Gate(optedInMembers),
            new PayerToPayerExportBuilder(), AcceptanceContext.Logger<PayerToPayerExchangeService>());

    private static PayerToPayerExchangeRequest Request(
        string? memberId, string tenant = AcceptanceContext.TenantId,
        string? dob = null, DateTime? exchangeDate = null) => new()
    {
        TenantId = tenant,
        ReceivingPayerId = "receiving-payer-001",
        MemberId = memberId,
        Dob = dob,
        ExchangeDateUtc = exchangeDate ?? new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc),
    };

    private static IReadOnlyList<FhirResource> ResourcesOfType(FhirBundle bundle, string type) =>
        bundle.Entry!.Where(e => e.Resource?.ResourceType == type).Select(e => e.Resource!).ToList();

    // ── Happy path ──────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-01")]
    [Trait("Backend", "Replace")]
    public async Task P2P01_Replace_MatchedAuthorizedMember_ProducesMemberScopedExport()
    {
        var result = await ServiceOverCho("pat-001").RespondAsync(Request("pat-001", dob: "1955-07-14"));

        result.Succeeded.Should().BeTrue();
        result.Outcome.Should().Be(PayerToPayerOutcome.Exported);
        result.MatchedMemberId.Should().Be("pat-001");

        var bundle = result.Bundle!;
        bundle.Type.Should().Be("collection");
        ResourcesOfType(bundle, "Patient").Should().ContainSingle().Which
            .Should().BeOfType<FhirPatient>().Which.Id.Should().Be("pat-001");
        ResourcesOfType(bundle, "Coverage").Should().ContainSingle();
        ResourcesOfType(bundle, "ExplanationOfBenefit").Should().HaveCount(2); // pat-001 has 2 payments

        // Every resource is member-scoped: all references point to pat-001, none to another member.
        var coverage = (FhirCoverage)ResourcesOfType(bundle, "Coverage")[0];
        coverage.Beneficiary!.Reference.Should().Be("Patient/pat-001");
        var eobs = ResourcesOfType(bundle, "ExplanationOfBenefit").Cast<FhirExplanationOfBenefit>().ToList();
        eobs.Should().OnlyContain(eob => eob.Patient!.Reference == "Patient/pat-001",
            "no other member's claims may appear in the export");
        // The EOBs are exactly pat-001's own payments (PMT-003 belongs to pat-002 and must not appear).
        eobs.Select(e => e.Id).Should().BeEquivalentTo(new[] { "PMT-001", "PMT-002" });

        // Auditable.
        result.Audit.MatchedMemberId.Should().Be("pat-001");
        result.Audit.ReceivingPayerId.Should().Be("receiving-payer-001");
        result.Audit.Outcome.Should().Be("Exported");
        result.Audit.ResourceCount.Should().Be(bundle.Total);
    }

    // ── Wrong member (demographic mismatch) ─────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-01")]
    [Trait("Backend", "Replace")]
    public async Task P2P01_Replace_MemberIdWithMismatchedDob_ReturnsNoDataNotAnotherMember()
    {
        // The member id exists, but the supplied DOB does not match: the workflow
        // must return nothing, never the record for that id.
        var result = await ServiceOverCho().RespondAsync(Request("pat-001", dob: "1900-01-01"));

        result.Outcome.Should().Be(PayerToPayerOutcome.NoMatch);
        result.Bundle.Should().BeNull();
    }

    [Fact]
    [Trait("Scenario", "P2P-01")]
    [Trait("Backend", "Replace")]
    public async Task P2P01_Replace_UnknownMember_FailsSafelyWithNoMatch()
    {
        var result = await ServiceOverCho().RespondAsync(Request("pat-does-not-exist"));

        result.Outcome.Should().Be(PayerToPayerOutcome.NoMatch);
        result.Bundle.Should().BeNull();
    }

    [Fact]
    [Trait("Scenario", "P2P-01")]
    [Trait("Backend", "Replace")]
    public async Task P2P01_Replace_MissingMemberIdentifier_IsInsufficientCriteria()
    {
        var result = await ServiceOverCho().RespondAsync(Request(memberId: null));

        result.Outcome.Should().Be(PayerToPayerOutcome.InsufficientCriteria);
        result.Bundle.Should().BeNull();
    }

    // ── Ambiguous match — refuse rather than guess ──────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-01")]
    [Trait("Backend", "Replace")]
    public async Task P2P01_Replace_MoreThanOneCandidate_RefusesWithAmbiguousMatch()
    {
        var source = new FixedMemberSource(AcceptanceContext.TenantId,
            new ChoMember { MemberId = "dup", FirstName = "A", LastName = "One", Dob = "1980-01-01", Gender = "M" },
            new ChoMember { MemberId = "dup", FirstName = "B", LastName = "Two", Dob = "1980-01-01", Gender = "F" });

        var result = await ServiceOverSource(source).RespondAsync(Request("dup"));

        result.Outcome.Should().Be(PayerToPayerOutcome.AmbiguousMatch);
        result.Bundle.Should().BeNull();
    }

    // ── Tenant boundary ─────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-01")]
    [Trait("Backend", "Replace")]
    public async Task P2P01_Replace_RequestForAnotherTenant_CannotExport()
    {
        var result = await ServiceOverCho().RespondAsync(Request("pat-001", tenant: "other-tenant"));

        result.Outcome.Should().Be(PayerToPayerOutcome.TenantMismatch);
        result.Bundle.Should().BeNull();
    }

    // ── Consent / authorization gate ────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-01")]
    [Trait("Backend", "Replace")]
    public async Task P2P01_Replace_MemberNotOptedIn_IsNotAuthorized_NoExport()
    {
        // Consent is enforced, not bypassed: a matched member with no active
        // opt-in yields NotAuthorized and no data. (This does not introduce a
        // dedicated Payer-to-Payer ConsentType — P2P-03 stays PARTIAL.)
        var result = await ServiceOverCho(/* pat-001 NOT opted in */).RespondAsync(Request("pat-001"));

        result.Outcome.Should().Be(PayerToPayerOutcome.NotAuthorized);
        result.Bundle.Should().BeNull();
        result.Audit.MatchedMemberId.Should().Be("pat-001"); // matched, then refused
    }

    // ── Empty-but-valid member ──────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-01")]
    [Trait("Backend", "Replace")]
    public async Task P2P01_Replace_MatchedMemberWithNoClaims_ExportsDeterministicPatientAndCoverage()
    {
        // pat-003 exists but has no payments — a valid export with the member's
        // Patient + Coverage and zero EOBs.
        var result = await ServiceOverCho("pat-003").RespondAsync(Request("pat-003"));

        result.Outcome.Should().Be(PayerToPayerOutcome.Exported);
        var bundle = result.Bundle!;
        ResourcesOfType(bundle, "Patient").Should().ContainSingle();
        ResourcesOfType(bundle, "Coverage").Should().ContainSingle();
        ResourcesOfType(bundle, "ExplanationOfBenefit").Should().BeEmpty();
        bundle.Total.Should().Be(2);
    }

    // ── 5-year lookback ─────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-01")]
    [Trait("Backend", "Replace")]
    public async Task P2P01_Replace_ClaimsOutsideFiveYearLookback_AreExcluded()
    {
        // Exchange dated far enough forward that pat-001's 2025 payments fall
        // outside the 5-year window: the export still succeeds, with no EOBs.
        var result = await ServiceOverCho("pat-001").RespondAsync(
            Request("pat-001", exchangeDate: new DateTime(2035, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        result.Outcome.Should().Be(PayerToPayerOutcome.Exported);
        ResourcesOfType(result.Bundle!, "ExplanationOfBenefit").Should().BeEmpty();
        ResourcesOfType(result.Bundle!, "Patient").Should().ContainSingle();
    }

    private sealed class FixedMemberSource : IPayerToPayerMemberSource
    {
        private readonly IReadOnlyList<ChoMember> _members;
        public FixedMemberSource(string tenant, params ChoMember[] members)
        {
            ServedTenantId = tenant;
            _members = members;
        }

        public string ServedTenantId { get; }

        public Task<IReadOnlyList<ChoMember>> FindCandidatesAsync(
            string tenantId, PayerToPayerMemberCriteria criteria, CancellationToken ct = default) =>
            Task.FromResult(string.Equals(tenantId, ServedTenantId, StringComparison.Ordinal)
                ? _members
                : Array.Empty<ChoMember>());

        public Task<IReadOnlyList<ChoPaymentDocument>> GetPaymentsAsync(
            string tenantId, string memberId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ChoPaymentDocument>>(Array.Empty<ChoPaymentDocument>());
    }
}
