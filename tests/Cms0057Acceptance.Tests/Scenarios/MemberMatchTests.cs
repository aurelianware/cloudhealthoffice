using Cms0057Acceptance.Tests.TestSupport;
using FhirService.Models;
using FhirService.Models.PayerToPayer;
using FhirService.Services;
using FhirService.Services.PayerToPayer;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Cms0057Acceptance.Tests.Scenarios;

/// <summary>
/// P2P-04 — Payer-to-Payer member match ($member-match) as REAL Cloud Health
/// Office Replace-mode capability. A receiving payer supplies a transitioning
/// member's identity attributes; Cloud Health Office resolves the same person
/// across payer contexts within the tenant and returns the relevant coverage
/// context — deterministically and fail-safe.
///
/// These scenarios exercise the SAME production classes the running service
/// binds — <see cref="PayerToPayerMemberMatchService"/>, the deterministic
/// <see cref="MemberMatchPolicy"/>, the <see cref="PayerToPayerCoverageSelector"/>,
/// the <see cref="MemberIdentityNormalizer"/>, and the tenant-scoped
/// <see cref="PatientAccessPayerToPayerMemberMatchSource"/> over the CHO member
/// directory (<see cref="MockPatientAccessDataProvider"/>). Synthetic data only;
/// no PHI.
///
/// P2P-02 (outbound initiation) is implemented separately (see
/// PayerToPayerOutboundTests); P2P-03 (dedicated P2P consent)
/// remains PARTIAL — member-match is identity resolution only and does not gate
/// on or introduce consent.
///
/// Traceability:
///   service   src/services/fhir-service/Services/PayerToPayer/PayerToPayerMemberMatchService.cs
///   policy    src/services/fhir-service/Services/PayerToPayer/MemberMatchPolicy.cs
///   coverage  src/services/fhir-service/Services/PayerToPayer/PayerToPayerCoverageSelector.cs
///   normalize src/services/fhir-service/Services/PayerToPayer/MemberIdentityNormalizer.cs
///   source    src/services/fhir-service/Services/PayerToPayer/PayerToPayerMemberMatchSource.cs
/// </summary>
public class MemberMatchTests
{
    private const string RequestingPayer = "receiving-payer-001";

    private static PayerToPayerMemberMatchService Service(IPayerToPayerConsentAttestationStore? store = null)
    {
        var provider = new MockPatientAccessDataProvider();
        var source = new PatientAccessPayerToPayerMemberMatchSource(
            provider, Options.Create(new FhirAdapterOptions { TenantId = AcceptanceContext.TenantId }));
        return new PayerToPayerMemberMatchService(
            source, store ?? new InMemoryPayerToPayerConsentAttestationStore(), AcceptanceContext.Directory(),
            TimeProvider.System, AcceptanceContext.Logger<PayerToPayerMemberMatchService>());
    }

    private static MemberMatchRequest Req(
        string tenant = AcceptanceContext.TenantId,
        string? memberId = null, string? family = null, string? given = null,
        string? dob = null, string? gender = null, string? ssn = null,
        string? postalCode = null, string? phone = null, string? email = null,
        string? requestedPayerId = null, string? requestedSubscriberId = null, string? asOf = null,
        System.Text.Json.Nodes.JsonObject? consent = null, bool withConsent = true) => new()
    {
        TenantId = tenant,
        ReceivingPayerId = RequestingPayer,
        // The member's consent, as the requesting payer collected it (ADR 022).
        Consent = withConsent ? consent ?? AcceptanceContext.PdexConsent(RequestingPayer) : null,
        MemberId = memberId,
        FamilyName = family,
        GivenName = given,
        BirthDate = dob,
        Gender = gender,
        Ssn = ssn,
        PostalCode = postalCode,
        Phone = phone,
        Email = email,
        RequestedPayerId = requestedPayerId,
        RequestedSubscriberId = requestedSubscriberId,
        AsOfDate = asOf,
    };

    // ── Exact match: strong identifier ──────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-04")]
    [Trait("Backend", "Replace")]
    public async Task P2P04_Replace_StrongIdentifierWithAgreeingDemographics_ResolvesOneMember()
    {
        var result = await Service().MatchAsync(
            Req(memberId: "pat-001", family: "Smith", dob: "1955-07-14"));

        result.Outcome.Should().Be(MemberMatchOutcome.Matched);
        result.MatchedMemberId.Should().Be("pat-001");
        result.Coverage.Should().NotBeNull();
        result.Audit.MatchedMemberId.Should().Be("pat-001");
        result.Audit.Outcome.Should().Be("Matched");
    }

    [Fact]
    [Trait("Scenario", "P2P-04")]
    [Trait("Backend", "Replace")]
    public async Task P2P04_Replace_PriorPayerSubscriberId_ResolvesTheCorrectMember()
    {
        // The id the member held under the prior payer (a coverage subscriber id)
        // legitimately resolves to that CHO member — cross-payer identity.
        var result = await Service().MatchAsync(Req(memberId: "SUB-2001"));

        result.Outcome.Should().Be(MemberMatchOutcome.Matched);
        result.MatchedMemberId.Should().Be("pat-001");
    }

    // ── Exact match: demographic pair (no strong id) ────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-04")]
    [Trait("Backend", "Replace")]
    public async Task P2P04_Replace_UniqueFamilyNameAndBirthDate_ResolvesOneMember()
    {
        var result = await Service().MatchAsync(Req(family: "Williams", dob: "1948-11-30"));

        result.Outcome.Should().Be(MemberMatchOutcome.Matched);
        result.MatchedMemberId.Should().Be("pat-003");
        result.Coverage!.CoverageId.Should().Be("COV-003");
    }

    // ── No match ────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-04")]
    [Trait("Backend", "Replace")]
    public async Task P2P04_Replace_IncorrectIdentity_ReturnsNoMatchNoData()
    {
        var result = await Service().MatchAsync(Req(family: "Nobody", dob: "2001-01-01"));

        result.Outcome.Should().Be(MemberMatchOutcome.NoMatch);
        result.Member.Should().BeNull();
        result.Coverage.Should().BeNull();
    }

    // ── Ambiguous identity — refuse rather than guess ───────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-04")]
    [Trait("Backend", "Replace")]
    public async Task P2P04_Replace_FamilyNameAndDobSharedByTwoMembers_IsAmbiguousNoData()
    {
        // pat-010 and pat-011 share family name + DOB. Last name + DOB alone is not
        // enough to single one out — the match must refuse, not return either.
        var result = await Service().MatchAsync(Req(family: "Brown", dob: "1990-05-05"));

        result.Outcome.Should().Be(MemberMatchOutcome.AmbiguousMatch);
        result.Member.Should().BeNull();
    }

    [Fact]
    [Trait("Scenario", "P2P-04")]
    [Trait("Backend", "Replace")]
    public async Task P2P04_Replace_GivenNameNarrowsAnOtherwiseAmbiguousPair()
    {
        var result = await Service().MatchAsync(Req(family: "Brown", dob: "1990-05-05", given: "Alice"));

        result.Outcome.Should().Be(MemberMatchOutcome.Matched);
        result.MatchedMemberId.Should().Be("pat-010");
    }

    [Fact]
    [Trait("Scenario", "P2P-04")]
    [Trait("Backend", "Replace")]
    public async Task P2P04_Replace_GenderNarrowsAnOtherwiseAmbiguousPair()
    {
        var result = await Service().MatchAsync(Req(family: "Brown", dob: "1990-05-05", gender: "M"));

        result.Outcome.Should().Be(MemberMatchOutcome.Matched);
        result.MatchedMemberId.Should().Be("pat-011");
    }

    // ── Conflicting identifiers must not match ──────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-04")]
    [Trait("Backend", "Replace")]
    public async Task P2P04_Replace_CorrectMemberIdButWrongBirthDate_DoesNotMatch()
    {
        // The member id exists, but the DOB contradicts it: the workflow must not
        // return that member (a contradicting strong+demographic pair fails closed).
        var result = await Service().MatchAsync(Req(memberId: "pat-001", dob: "1900-01-01"));

        result.Outcome.Should().Be(MemberMatchOutcome.NoMatch);
        result.Member.Should().BeNull();
    }

    [Fact]
    [Trait("Scenario", "P2P-04")]
    [Trait("Backend", "Replace")]
    public async Task P2P04_Replace_CorrectNameAndDobButConflictingGivenName_DoesNotMatch()
    {
        var result = await Service().MatchAsync(Req(family: "Smith", dob: "1955-07-14", given: "Jane"));

        result.Outcome.Should().Be(MemberMatchOutcome.NoMatch);
        result.Member.Should().BeNull();
    }

    // ── Cross-tenant never matches ──────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-04")]
    [Trait("Backend", "Replace")]
    public async Task P2P04_Replace_RequestForAnotherTenant_CannotMatch()
    {
        var result = await Service().MatchAsync(
            Req(tenant: "other-tenant", memberId: "pat-001", family: "Smith", dob: "1955-07-14"));

        result.Outcome.Should().Be(MemberMatchOutcome.TenantMismatch);
        result.Member.Should().BeNull();
    }

    [Fact]
    [Trait("Scenario", "P2P-04")]
    [Trait("Backend", "Replace")]
    public async Task P2P04_Replace_TenantWithSurroundingWhitespace_StillMatchesItsOwnTenant()
    {
        // The tenant can arrive from headers/claims untrimmed; a stray space must
        // not turn the served tenant into a false TenantMismatch.
        var result = await Service().MatchAsync(Req(tenant: "  demo-tenant  ", memberId: "pat-001"));

        result.Outcome.Should().Be(MemberMatchOutcome.Matched);
        result.MatchedMemberId.Should().Be("pat-001");
    }

    // ── Insufficient criteria fail safely (anti-enumeration) ────────────────────

    [Fact]
    [Trait("Scenario", "P2P-04")]
    [Trait("Backend", "Replace")]
    public async Task P2P04_Replace_FamilyNameAloneIsInsufficient()
    {
        var result = await Service().MatchAsync(Req(family: "Smith"));

        result.Outcome.Should().Be(MemberMatchOutcome.InsufficientCriteria);
        result.Member.Should().BeNull();
    }

    [Fact]
    [Trait("Scenario", "P2P-04")]
    [Trait("Backend", "Replace")]
    public async Task P2P04_Replace_GenderAloneIsInsufficient()
    {
        var result = await Service().MatchAsync(Req(gender: "F"));

        result.Outcome.Should().Be(MemberMatchOutcome.InsufficientCriteria);
    }

    // ── Normalization: equivalent formatting matches consistently ───────────────

    [Fact]
    [Trait("Scenario", "P2P-04")]
    [Trait("Backend", "Replace")]
    public async Task P2P04_Replace_WhitespaceCasingAndZipPlusFour_NormalizeToTheSameMember()
    {
        // "  smith  " vs "Smith", and a ZIP+4 vs the stored ZIP5, must not prevent
        // the match; the corroborating postal code agrees after normalization.
        var result = await Service().MatchAsync(
            Req(family: "  SMITH  ", dob: "1955-07-14", postalCode: "37201-1234"));

        result.Outcome.Should().Be(MemberMatchOutcome.Matched);
        result.MatchedMemberId.Should().Be("pat-001");
    }

    [Fact]
    [Trait("Scenario", "P2P-04")]
    [Trait("Backend", "Replace")]
    public async Task P2P04_Replace_IdentifierFormattingDifferences_NormalizeToTheSameMember()
    {
        // "sub 2001" / "SUB-2001" / "sub-2001" all resolve to the same coverage id.
        var result = await Service().MatchAsync(Req(memberId: "sub 2001"));

        result.Outcome.Should().Be(MemberMatchOutcome.Matched);
        result.MatchedMemberId.Should().Be("pat-001");
    }

    [Fact]
    [Trait("Scenario", "P2P-04")]
    [Trait("Backend", "Replace")]
    public async Task P2P04_Replace_UnrecognizedGender_IsTreatedAsNotSupplied_NotAConflict()
    {
        // An unknown/invalid gender must be ignored (treated as "not supplied"),
        // not compared as a contradiction — a strong id that agrees still matches.
        var result = await Service().MatchAsync(Req(memberId: "pat-001", gender: "X"));

        result.Outcome.Should().Be(MemberMatchOutcome.Matched);
        result.MatchedMemberId.Should().Be("pat-001");
    }

    // ── Concurrent / overlapping coverage selection ─────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-04")]
    [Trait("Backend", "Replace")]
    public async Task P2P04_Replace_AsOfPriorDate_SelectsThePriorCoverage()
    {
        // pat-001 has a prior (2018–2022) and a current (2022–open) coverage. As of
        // 2020 only the prior is in force.
        var result = await Service().MatchAsync(Req(memberId: "pat-001", asOf: "2020-01-01"));

        result.Outcome.Should().Be(MemberMatchOutcome.Matched);
        result.Coverage!.CoverageId.Should().Be("COV-001-PRIOR");
    }

    [Fact]
    [Trait("Scenario", "P2P-04")]
    [Trait("Backend", "Replace")]
    public async Task P2P04_Replace_AsOfCurrentDate_SelectsTheCurrentCoverage()
    {
        var result = await Service().MatchAsync(Req(memberId: "pat-001", asOf: "2026-01-01"));

        result.Outcome.Should().Be(MemberMatchOutcome.Matched);
        result.Coverage!.CoverageId.Should().Be("COV-001-CURRENT");
    }

    [Fact]
    [Trait("Scenario", "P2P-04")]
    [Trait("Backend", "Replace")]
    public async Task P2P04_Replace_OverlappingCoveragesWithoutADiscriminator_IsAmbiguousCoverage()
    {
        // As of 2022-08 both coverages overlap; with no payer/subscriber
        // discriminator the workflow refuses to guess which relationship is meant.
        var result = await Service().MatchAsync(Req(memberId: "pat-001", asOf: "2022-08-01"));

        result.Outcome.Should().Be(MemberMatchOutcome.AmbiguousCoverage);
        result.Coverage.Should().BeNull();
    }

    [Fact]
    [Trait("Scenario", "P2P-04")]
    [Trait("Backend", "Replace")]
    public async Task P2P04_Replace_RequestedPayerContext_ResolvesOverlappingCoverageDeterministically()
    {
        // The same overlapping window, but the request pins the prior payer — now
        // it resolves to exactly that coverage.
        var result = await Service().MatchAsync(
            Req(memberId: "pat-001", asOf: "2022-08-01", requestedPayerId: "PRIOR-PLAN"));

        result.Outcome.Should().Be(MemberMatchOutcome.Matched);
        result.Coverage!.CoverageId.Should().Be("COV-001-PRIOR");
    }

    [Fact]
    [Trait("Scenario", "P2P-04")]
    [Trait("Backend", "Replace")]
    public async Task P2P04_Replace_RequestedPayerContextMatchingNoCoverage_RefusesRatherThanFallBack()
    {
        // The caller pins a payer context the member does not hold. The selector
        // must refuse (fail-safe) rather than fall back to a different coverage the
        // caller never asked for.
        var result = await Service().MatchAsync(
            Req(memberId: "pat-001", requestedPayerId: "PLAN-THAT-DOES-NOT-EXIST"));

        result.Outcome.Should().Be(MemberMatchOutcome.AmbiguousCoverage);
        result.Coverage.Should().BeNull();
    }

    // ── Hand-off to the P2P-01 export path ──────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-04")]
    [Trait("Backend", "Replace")]
    public async Task P2P04_Replace_ResolvedMember_IsDirectlyConsumableByTheP2P01ExportPath()
    {
        // A match yields a stable CHO member id; that id flows straight into the
        // P2P-01 respond service (with the member's opt-in on record) and produces
        // the member's export — no re-matching, no unsafe guessing.
        // The match records the requesting payer's consent; the export that
        // follows reads it from the same store, for that payer only.
        var store = new InMemoryPayerToPayerConsentAttestationStore();
        var match = await Service(store).MatchAsync(Req(family: "Smith", dob: "1955-07-14", memberId: "pat-001"));
        match.Outcome.Should().Be(MemberMatchOutcome.Matched);

        var provider = new MockPatientAccessDataProvider();
        var exportSource = new PatientAccessPayerToPayerMemberSource(
            provider, Options.Create(new FhirAdapterOptions { TenantId = AcceptanceContext.TenantId }));
        var export = new PayerToPayerExchangeService(
            new PayerToPayerMemberResolver(exportSource), new PatientAccessPayerToPayerExportSource(provider), store,
            AcceptanceContext.Directory(), TimeProvider.System,
            new PayerToPayerExportBuilder(),
            AcceptanceContext.Logger<PayerToPayerExchangeService>());

        var result = await export.RespondAsync(new PayerToPayerExchangeRequest
        {
            TenantId = AcceptanceContext.TenantId,
            ReceivingPayerId = RequestingPayer,
            MemberId = match.MatchedMemberId,
        });

        result.Outcome.Should().Be(PayerToPayerOutcome.Exported);
        result.MatchedMemberId.Should().Be("pat-001");
        result.Bundle!.Entry!.Select(e => e.Resource).OfType<FhirPatient>()
            .Should().ContainSingle().Which.Id.Should().Be("pat-001");
    }

    // ── Consent (D-13, ADR 022) ─────────────────────────────────────────────────

    [Theory]
    [Trait("Scenario", "P2P-04")]
    [Trait("Backend", "Replace")]
    [InlineData("none")]
    [InlineData("other-payer")]
    [InlineData("regular")]
    public async Task P2P04_Replace_WithoutAnAcceptableConsentNoMemberIsLookedAt(string kind)
    {
        var source = new RecordingSource();
        var service = new PayerToPayerMemberMatchService(source, new InMemoryPayerToPayerConsentAttestationStore(),
            AcceptanceContext.Directory(), TimeProvider.System, AcceptanceContext.Logger<PayerToPayerMemberMatchService>());
        var request = kind switch
        {
            "none" => Req(memberId: "pat-001", family: "Smith", dob: "1955-07-14", withConsent: false),
            "other-payer" => Req(memberId: "pat-001", family: "Smith", dob: "1955-07-14",
                consent: AcceptanceContext.PdexConsent("other-payer-002")),
            _ => Req(memberId: "pat-001", family: "Smith", dob: "1955-07-14",
                consent: AcceptanceContext.PdexConsent(RequestingPayer, policy: PdexConsentValidator.RegularPolicy)),
        };

        var result = await service.MatchAsync(request);

        result.Outcome.Should().Be(MemberMatchOutcome.ConsentNotAccepted);
        result.Member.Should().BeNull();
        source.Calls.Should().Be(0, "the consent is decided before any member is looked at");
    }

    [Fact]
    [Trait("Scenario", "P2P-04")]
    [Trait("Backend", "Replace")]
    public async Task P2P04_Replace_AMatchRecordsTheConsentForTheRequestingPayerOnly()
    {
        var store = new InMemoryPayerToPayerConsentAttestationStore();

        var match = await Service(store).MatchAsync(Req(family: "Smith", dob: "1955-07-14", memberId: "pat-001"));

        match.Outcome.Should().Be(MemberMatchOutcome.Matched);
        match.Audit.ConsentFingerprint.Should().NotBeNullOrEmpty();
        store.Records.Should().ContainSingle().Which.Should().Match<PayerToPayerConsentAttestation>(a =>
            a.MemberId == "pat-001" && a.ReceivingPayerId == RequestingPayer
            && a.Policy == PdexConsentValidator.SensitivePolicy);
    }

    [Fact]
    [Trait("Scenario", "P2P-04")]
    [Trait("Backend", "Replace")]
    public async Task P2P04_Replace_AFailedMatchRecordsNoConsent()
    {
        var store = new InMemoryPayerToPayerConsentAttestationStore();

        (await Service(store).MatchAsync(Req(family: "Nobody", dob: "1900-01-01"))).Outcome
            .Should().Be(MemberMatchOutcome.NoMatch);
        store.Records.Should().BeEmpty();
    }

    private sealed class RecordingSource : IPayerToPayerMemberMatchSource
    {
        public int Calls { get; private set; }
        public string ServedTenantId => AcceptanceContext.TenantId;

        public Task<MemberMatchCandidates> GetCandidatesAsync(string tenantId, MemberMatchCriteria criteria, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(MemberMatchCandidates.None);
        }

        public Task<IReadOnlyList<ChoCoverage>> GetCoveragesAsync(string tenantId, string memberId, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<ChoCoverage>>([]);
        }
    }
}
