using System.Reflection;
using System.Text.Json;
using Cms0057Acceptance.Tests.TestSupport;
using FhirService.Models;
using FhirService.Models.PayerToPayer;
using FhirService.Services;
using FhirService.Services.PayerToPayer;
using FhirService.Services.PayerToPayer.Outbound;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Options;

namespace Cms0057Acceptance.Tests.Scenarios;

/// <summary>
/// P2P-02 — outbound Payer-to-Payer initiation as REAL Cloud Health Office
/// Replace-mode capability. On a coverage transition, CHO (the member's new
/// payer) initiates the exchange against the member's prior payer: it resolves
/// the member and prior-coverage context from its OWN authoritative data,
/// resolves the target payer through the trusted directory, enforces the
/// member's opt-in server-side, calls the remote <c>$member-match</c>, requests
/// the member-scoped export only after a single member resolves, validates what
/// comes back, and records the exchange.
///
/// These scenarios drive the SAME production classes the running service binds —
/// <see cref="PayerToPayerOutboundService"/>,
/// <see cref="ConfiguredPayerToPayerEndpointResolver"/>,
/// <see cref="ConfiguredPayerToPayerConsentGate"/>,
/// <see cref="PayerToPayerResponseReader"/>, the tenant-scoped CHO member /
/// coverage sources, and <see cref="InMemoryPayerToPayerOutboundExchangeStore"/>.
/// Only the far side of the wire is a test double: <see cref="ScriptedPriorPayer"/>
/// implements the transport seam <see cref="IPayerToPayerRemoteClient"/> and
/// answers with real FHIR R4 payloads, so CHO's orchestration, parsing, and
/// validation all run for real while the peer stays deterministic. Synthetic
/// data only; no PHI.
///
/// Traceability:
///   service   src/services/fhir-service/Services/PayerToPayer/Outbound/PayerToPayerOutboundService.cs
///   directory src/services/fhir-service/Services/PayerToPayer/Outbound/PayerToPayerEndpointResolver.cs
///   transport src/services/fhir-service/Services/PayerToPayer/Outbound/IPayerToPayerRemoteClient.cs
///   reader    src/services/fhir-service/Services/PayerToPayer/Outbound/PayerToPayerResponseReader.cs
///   state     src/services/fhir-service/Services/PayerToPayer/Outbound/PayerToPayerOutboundExchangeStore.cs
///   consent   src/services/fhir-service/Services/PayerToPayer/PayerToPayerConsentGate.cs
/// </summary>
public class PayerToPayerOutboundTests
{
    private const string TargetPayer = "PRIOR-PLAN";     // pat-001's prior coverage payer (synthetic)
    private const string RemoteMemberId = "prior-1001";  // the member id the prior payer knows
    private const string PriorSubscriberId = "SUB-1001"; // pat-001's subscriber id with that payer

    // ── Harness ─────────────────────────────────────────────────────────────────

    private sealed record Harness(
        PayerToPayerOutboundService Service,
        ScriptedPriorPayer Peer,
        InMemoryPayerToPayerOutboundExchangeStore Store);

    private static Harness Build(
        ScriptedPriorPayer peer,
        string[]? optedInMembers = null,
        string baseUrl = "https://prior-payer.example/fhir/r4",
        bool allowInsecureTransport = false,
        string directoryTenant = AcceptanceContext.TenantId,
        IPayerToPayerMemberMatchSource? coverageSource = null)
    {
        var provider = new MockPatientAccessDataProvider();
        var adapterOptions = Options.Create(new FhirAdapterOptions { TenantId = AcceptanceContext.TenantId });

        var memberSource = new PatientAccessPayerToPayerMemberSource(provider, adapterOptions);
        coverageSource ??= new PatientAccessPayerToPayerMemberMatchSource(provider, adapterOptions);

        var consentGate = new ConfiguredPayerToPayerConsentGate(Options.Create(new PayerToPayerConsentOptions
        {
            OptedInMembersByTenant = new()
            {
                [AcceptanceContext.TenantId] = (optedInMembers ?? Array.Empty<string>()).ToList(),
            },
        }));

        var directory = Options.Create(new PayerToPayerDirectoryOptions
        {
            LocalPayerId = "cloud-health-office",
            AllowInsecureTransport = allowInsecureTransport,
            PayersByTenant = new()
            {
                [directoryTenant] =
                [
                    new PayerToPayerEndpointEntry
                    {
                        PayerId = TargetPayer,
                        EndpointKey = "prior-plan-fhir",
                        BaseUrl = baseUrl,
                    },
                ],
            },
        });

        var store = new InMemoryPayerToPayerOutboundExchangeStore();
        var service = new PayerToPayerOutboundService(
            memberSource,
            coverageSource,
            consentGate,
            new ConfiguredPayerToPayerEndpointResolver(
                directory, AcceptanceContext.Logger<ConfiguredPayerToPayerEndpointResolver>()),
            peer,
            store,
            directory,
            AcceptanceContext.Logger<PayerToPayerOutboundService>());

        return new Harness(service, peer, store);
    }

    private static PayerToPayerOutboundRequest Request(
        string memberId = "pat-001",
        string tenant = AcceptanceContext.TenantId,
        string targetPayer = TargetPayer,
        string? transitionKey = "transition-2026-01") => new()
    {
        TenantId = tenant,
        MemberId = memberId,
        TargetPayerId = targetPayer,
        TransitionKey = transitionKey,
        InitiatedBy = "enrollment:coverage-transition",
        ExchangeDateUtc = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc),
    };

    // ── Happy path ──────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_AuthorizedTransition_MatchesThenRequestsDataAndCompletes()
    {
        var harness = Build(ScriptedPriorPayer.HappyPath(), optedInMembers: ["pat-001"]);

        var result = await harness.Service.InitiateAsync(Request());

        result.Succeeded.Should().BeTrue();
        result.Exchange.Status.Should().Be(PayerToPayerOutboundStatus.Completed);
        result.Exchange.Failure.Should().Be(PayerToPayerOutboundFailure.None);

        // Both remote operations were called, in order: match first, export second.
        harness.Peer.Calls.Select(c => c.Operation).Should()
            .Equal("member-match", "member-data-export");

        // The match request carried the member's identity with the PRIOR payer
        // (from CHO's own coverage record) plus the minimum demographics — and
        // CHO identified itself as the receiving payer.
        var match = harness.Peer.MatchRequests.Should().ContainSingle().Subject;
        match.MemberId.Should().Be(PriorSubscriberId);
        match.FamilyName.Should().Be("Smith");
        match.BirthDate.Should().Be("1955-07-14");
        match.RequestedPayerId.Should().Be(TargetPayer);
        match.ReceivingPayerId.Should().Be("cloud-health-office");

        // The export used the id the REMOTE payer resolved — not CHO's member id.
        var export = harness.Peer.DataRequests.Should().ContainSingle().Subject;
        export.MemberId.Should().Be(RemoteMemberId);
        export.LookbackYears.Should().Be(5);

        // Both calls went to the endpoint the trusted directory resolved.
        harness.Peer.Calls.Should().OnlyContain(c => c.EndpointKey == "prior-plan-fhir");

        // A validated, member-scoped package was received.
        var package = result.Package.Should().NotBeNull().And.BeOfType<PayerToPayerReceivedPackage>().Subject;
        package.RemoteMemberId.Should().Be(RemoteMemberId);
        package.ResourceCount.Should().Be(4);   // Patient + Coverage + 2 EOBs
        result.Exchange.ReceivedResourceCount.Should().Be(4);
        result.Exchange.LocalCoverageId.Should().Be("COV-001-PRIOR");
        result.Exchange.RemoteMemberId.Should().Be(RemoteMemberId);

        // Provenance: the package is stamped as the prior payer's data, never as
        // CHO-originated.
        package.Provenance.SourcePayerId.Should().Be(TargetPayer);
        package.Provenance.SourceEndpointKey.Should().Be("prior-plan-fhir");
        package.Provenance.ExchangeId.Should().Be(result.Exchange.ExchangeId);
        var provenance = package.Bundle.Entry.Select(e => e.Resource).OfType<Provenance>()
            .Should().ContainSingle().Subject;
        provenance.Agent.Should().ContainSingle().Which.Who.Display.Should().Be(TargetPayer);

        // Auditable, and the exchange is recorded.
        result.Audit.Outcome.Should().Be("Completed");
        result.Audit.MemberId.Should().Be("pat-001");
        result.Audit.TargetPayerId.Should().Be(TargetPayer);
        result.Audit.ResourceCount.Should().Be(4);
        var stored = await harness.Store.GetAsync(AcceptanceContext.TenantId, result.Exchange.ExchangeId);
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(PayerToPayerOutboundStatus.Completed);
        stored.TargetEndpointKey.Should().Be("prior-plan-fhir");
    }

    // ── Authorization ───────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_MemberNotOptedIn_SendsNothingToThePriorPayer()
    {
        // Consent is decided server-side. Without an active opt-in the exchange
        // stops before ANY remote call — the member's identity is never disclosed.
        var harness = Build(ScriptedPriorPayer.HappyPath() /* pat-001 NOT opted in */);

        var result = await harness.Service.InitiateAsync(Request());

        result.Exchange.Status.Should().Be(PayerToPayerOutboundStatus.NotAuthorized);
        result.Exchange.Failure.Should().Be(PayerToPayerOutboundFailure.NotAuthorized);
        result.Package.Should().BeNull();
        harness.Peer.Calls.Should().BeEmpty();
    }

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public void P2P02_Replace_ConsentIsNeverAcceptedFromTheCaller()
    {
        // Structural guard: there is no way for a caller to assert the member's
        // opt-in on an outbound request (no "memberOptedIn"-style field).
        var consentish = typeof(PayerToPayerOutboundRequest).GetProperties()
            .Select(p => p.Name)
            .Where(n => n.Contains("consent", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("optIn", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("authorized", StringComparison.OrdinalIgnoreCase));
        consentish.Should().BeEmpty();
    }

    // ── Target payer resolution / SSRF boundary ─────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_UnknownTargetPayer_FailsSafelyWithoutCalling()
    {
        var harness = Build(ScriptedPriorPayer.HappyPath(), optedInMembers: ["pat-001"]);

        var result = await harness.Service.InitiateAsync(Request(targetPayer: "PAYER-NOT-IN-DIRECTORY"));

        result.Exchange.Status.Should().Be(PayerToPayerOutboundStatus.Failed);
        result.Exchange.Failure.Should().Be(PayerToPayerOutboundFailure.TargetPayerNotConfigured);
        harness.Peer.Calls.Should().BeEmpty();
    }

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_PlainHttpDirectoryEntry_IsRejectedNotSilentlyDowngraded()
    {
        var harness = Build(ScriptedPriorPayer.HappyPath(), optedInMembers: ["pat-001"],
            baseUrl: "http://prior-payer.example/fhir/r4");

        var result = await harness.Service.InitiateAsync(Request());

        result.Exchange.Failure.Should().Be(PayerToPayerOutboundFailure.TargetPayerNotConfigured);
        harness.Peer.Calls.Should().BeEmpty();
    }

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public void P2P02_Replace_CallerCannotSupplyARemoteLocation()
    {
        // SSRF guard, structurally: the outbound request carries payer/member
        // references only. Endpoints come exclusively from the trusted directory.
        var locationish = typeof(PayerToPayerOutboundRequest).GetProperties()
            .Concat(typeof(FhirService.Controllers.PayerToPayerInitiateRequestDto).GetProperties())
            .Select(p => p.Name)
            .Where(n => n.Contains("url", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("uri", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("endpoint", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("host", StringComparison.OrdinalIgnoreCase));
        locationish.Should().BeEmpty();
    }

    // ── Tenant boundary ─────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_AnotherTenantsMember_CannotBeInitiatedOn()
    {
        var harness = Build(ScriptedPriorPayer.HappyPath(), optedInMembers: ["pat-001"],
            directoryTenant: "other-tenant");

        var result = await harness.Service.InitiateAsync(Request(tenant: "other-tenant"));

        result.Exchange.Status.Should().Be(PayerToPayerOutboundStatus.Failed);
        result.Exchange.Failure.Should().Be(PayerToPayerOutboundFailure.TenantMismatch);
        result.Package.Should().BeNull();
        harness.Peer.Calls.Should().BeEmpty();
    }

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_UnknownLocalMember_FailsBeforeAnyRemoteCall()
    {
        var harness = Build(ScriptedPriorPayer.HappyPath(), optedInMembers: ["pat-001"]);

        var result = await harness.Service.InitiateAsync(Request(memberId: "pat-does-not-exist"));

        result.Exchange.Failure.Should().Be(PayerToPayerOutboundFailure.MemberNotFound);
        harness.Peer.Calls.Should().BeEmpty();
    }

    // ── Remote member-match outcomes ────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_PriorPayerNoMatch_DoesNotRequestData()
    {
        var harness = Build(
            ScriptedPriorPayer.MatchFails(RemoteCallOutcome.NoMatch), optedInMembers: ["pat-001"]);

        var result = await harness.Service.InitiateAsync(Request());

        result.Exchange.Status.Should().Be(PayerToPayerOutboundStatus.NoMatch);
        result.Exchange.Failure.Should().Be(PayerToPayerOutboundFailure.MemberNoMatch);
        result.Package.Should().BeNull();
        harness.Peer.DataRequests.Should().BeEmpty("no data may be requested without a resolved member");
    }

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_PriorPayerAmbiguousMatch_DoesNotRequestData()
    {
        var harness = Build(
            ScriptedPriorPayer.MatchFails(RemoteCallOutcome.Ambiguous), optedInMembers: ["pat-001"]);

        var result = await harness.Service.InitiateAsync(Request());

        result.Exchange.Status.Should().Be(PayerToPayerOutboundStatus.Ambiguous);
        result.Exchange.Failure.Should().Be(PayerToPayerOutboundFailure.MemberAmbiguous);
        harness.Peer.DataRequests.Should().BeEmpty();
    }

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_PriorPayerRejectsAuthorization_IsAStructuredFailure()
    {
        var harness = Build(
            ScriptedPriorPayer.MatchFails(RemoteCallOutcome.Unauthorized), optedInMembers: ["pat-001"]);

        var result = await harness.Service.InitiateAsync(Request());

        result.Exchange.Status.Should().Be(PayerToPayerOutboundStatus.Failed);
        result.Exchange.Failure.Should().Be(PayerToPayerOutboundFailure.RemoteUnauthorized);
        result.Exchange.MemberMatchOutcome.Should().Be("Unauthorized");
        harness.Peer.DataRequests.Should().BeEmpty();
    }

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_PriorPayerUnavailableDuringExport_IsAStructuredFailure()
    {
        var harness = Build(
            ScriptedPriorPayer.ExportFails(RemoteCallOutcome.Unavailable), optedInMembers: ["pat-001"]);

        var result = await harness.Service.InitiateAsync(Request());

        result.Exchange.Status.Should().Be(PayerToPayerOutboundStatus.Failed);
        result.Exchange.Failure.Should().Be(PayerToPayerOutboundFailure.RemoteUnavailable);
        result.Exchange.ExportOutcome.Should().Be("Unavailable");
        result.Package.Should().BeNull();
    }

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_UnreadableMatchResponse_IsRejected()
    {
        var harness = Build(
            ScriptedPriorPayer.WithPayloads(matchPayload: "{ not-fhir", exportPayload: null),
            optedInMembers: ["pat-001"]);

        var result = await harness.Service.InitiateAsync(Request());

        result.Exchange.Failure.Should().Be(PayerToPayerOutboundFailure.InvalidRemoteResponse);
        harness.Peer.DataRequests.Should().BeEmpty();
    }

    // ── Received package validation ─────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_MalformedDataPackage_IsRejectedSafely()
    {
        var harness = Build(
            ScriptedPriorPayer.WithPayloads(
                matchPayload: PriorPayerPayloads.MatchBundle(RemoteMemberId),
                exportPayload: "<not json at all>"),
            optedInMembers: ["pat-001"]);

        var result = await harness.Service.InitiateAsync(Request());

        result.Exchange.Status.Should().Be(PayerToPayerOutboundStatus.Failed);
        result.Exchange.Failure.Should().Be(PayerToPayerOutboundFailure.InvalidRemoteResponse);
        result.Package.Should().BeNull();
        result.Exchange.ReceivedResourceCount.Should().Be(0);
    }

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_PackageForADifferentMember_IsRejected()
    {
        // The peer matched prior-1001 but returned someone else's record.
        var harness = Build(
            ScriptedPriorPayer.WithPayloads(
                matchPayload: PriorPayerPayloads.MatchBundle(RemoteMemberId),
                exportPayload: PriorPayerPayloads.ExportBundle("prior-9999")),
            optedInMembers: ["pat-001"]);

        var result = await harness.Service.InitiateAsync(Request());

        result.Exchange.Failure.Should().Be(PayerToPayerOutboundFailure.InvalidRemoteResponse);
        result.Package.Should().BeNull();
    }

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_PackageCarryingAnotherMembersClaim_IsRejected()
    {
        var harness = Build(
            ScriptedPriorPayer.WithPayloads(
                matchPayload: PriorPayerPayloads.MatchBundle(RemoteMemberId),
                exportPayload: PriorPayerPayloads.ExportBundle(
                    RemoteMemberId, foreignPatientOnClaim: "prior-8888")),
            optedInMembers: ["pat-001"]);

        var result = await harness.Service.InitiateAsync(Request());

        result.Exchange.Failure.Should().Be(PayerToPayerOutboundFailure.InvalidRemoteResponse);
        result.Package.Should().BeNull();
    }

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_ForeignPatientReferenceAsAnAbsoluteUrl_IsStillRejected()
    {
        // An absolute reference must not be a way around the member-scoping check.
        var harness = Build(
            ScriptedPriorPayer.WithPayloads(
                matchPayload: PriorPayerPayloads.MatchBundle(RemoteMemberId),
                exportPayload: PriorPayerPayloads.ExportBundle(
                    RemoteMemberId,
                    foreignPatientOnClaim: "https://prior-payer.example/fhir/r4/Patient/prior-8888")),
            optedInMembers: ["pat-001"]);

        var result = await harness.Service.InitiateAsync(Request());

        result.Exchange.Failure.Should().Be(PayerToPayerOutboundFailure.InvalidRemoteResponse);
        result.Package.Should().BeNull();
    }

    // ── Local coverage context ──────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_OverlappingCoveragesWithTheTargetPayer_RefuseRatherThanGuess()
    {
        // Two coverages with the same prior payer, both in force on the exchange
        // date: CHO will not assert which relationship the exchange is about.
        var coverages = new FixedCoverageSource(AcceptanceContext.TenantId,
            new ChoCoverage
            {
                MemberId = "pat-001", CoverageId = "COV-A", PayerId = TargetPayer,
                SubscriberId = "SUB-A", PeriodStart = "2020-01-01", PeriodEnd = null,
            },
            new ChoCoverage
            {
                MemberId = "pat-001", CoverageId = "COV-B", PayerId = TargetPayer,
                SubscriberId = "SUB-B", PeriodStart = "2021-01-01", PeriodEnd = null,
            });

        var harness = Build(ScriptedPriorPayer.HappyPath(), optedInMembers: ["pat-001"],
            coverageSource: coverages);

        var result = await harness.Service.InitiateAsync(Request());

        result.Exchange.Failure.Should().Be(PayerToPayerOutboundFailure.LocalCoverageAmbiguous);
        harness.Peer.Calls.Should().BeEmpty();
    }

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_NoPriorCoverageOnRecord_StillMatchesOnDemographics()
    {
        // A member transitioning in may have no CHO coverage with the prior payer.
        // The exchange proceeds on the minimum demographics — with no identifier
        // asserted for a relationship CHO does not hold.
        var harness = Build(ScriptedPriorPayer.HappyPath(), optedInMembers: ["pat-003"]);

        var result = await harness.Service.InitiateAsync(Request(memberId: "pat-003"));

        result.Succeeded.Should().BeTrue();
        var match = harness.Peer.MatchRequests.Should().ContainSingle().Subject;
        match.MemberId.Should().BeNull();
        match.FamilyName.Should().Be("Williams");
        match.BirthDate.Should().Be("1948-11-30");
    }

    // ── Idempotency / retry ─────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_RepeatedInitiationForTheSameTransition_ReplaysOneExchange()
    {
        var harness = Build(ScriptedPriorPayer.HappyPath(), optedInMembers: ["pat-001"]);

        var first = await harness.Service.InitiateAsync(Request());
        var second = await harness.Service.InitiateAsync(Request());

        second.IsReplay.Should().BeTrue();
        second.Exchange.ExchangeId.Should().Be(first.Exchange.ExchangeId);
        second.Exchange.Status.Should().Be(PayerToPayerOutboundStatus.Completed);
        harness.Peer.Calls.Should().HaveCount(2, "a retry must not re-run the exchange against the prior payer");
    }

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_RetryAfterATransportFailure_ResumesTheSameExchange()
    {
        var peer = ScriptedPriorPayer.ExportFails(RemoteCallOutcome.Unavailable);
        var harness = Build(peer, optedInMembers: ["pat-001"]);

        var failed = await harness.Service.InitiateAsync(Request());
        failed.Exchange.Failure.Should().Be(PayerToPayerOutboundFailure.RemoteUnavailable);

        peer.Recover();  // the prior payer's endpoint comes back
        var retried = await harness.Service.InitiateAsync(Request());

        retried.IsReplay.Should().BeFalse();
        retried.Succeeded.Should().BeTrue();
        retried.Exchange.ExchangeId.Should().Be(failed.Exchange.ExchangeId);
        retried.Exchange.Failure.Should().Be(PayerToPayerOutboundFailure.None);
    }

    // ── Audit hygiene ───────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public void P2P02_Replace_AuditCarriesNoDemographicsAndNoEndpointLocation()
    {
        var fields = typeof(PayerToPayerOutboundAuditEntry).GetProperties().Select(p => p.Name).ToList();

        fields.Should().NotContain(n =>
            n.Contains("name", StringComparison.OrdinalIgnoreCase)
            || n.Contains("dob", StringComparison.OrdinalIgnoreCase)
            || n.Contains("birth", StringComparison.OrdinalIgnoreCase)
            || n.Contains("ssn", StringComparison.OrdinalIgnoreCase)
            || n.Contains("token", StringComparison.OrdinalIgnoreCase)
            || n.Contains("url", StringComparison.OrdinalIgnoreCase));

        // What it does carry: enough to trace the exchange end to end.
        fields.Should().Contain(["TenantId", "MemberId", "TargetPayerId", "ExchangeId", "Outcome", "ResourceCount"]);
    }

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public void P2P02_Replace_OutboundMatchRequestDisclosesOnlyMinimalIdentity()
    {
        // Only what $member-match needs leaves CHO: no SSN, address, phone, or email.
        var fields = typeof(RemoteMemberMatchRequest).GetProperties().Select(p => p.Name).ToList();

        fields.Should().NotContain(n =>
            n.Contains("ssn", StringComparison.OrdinalIgnoreCase)
            || n.Contains("address", StringComparison.OrdinalIgnoreCase)
            || n.Contains("postal", StringComparison.OrdinalIgnoreCase)
            || n.Contains("phone", StringComparison.OrdinalIgnoreCase)
            || n.Contains("email", StringComparison.OrdinalIgnoreCase));
    }

    // ── Test doubles: the far side of the wire only ─────────────────────────────

    /// <summary>
    /// A deterministic prior payer at the transport seam. It records every call
    /// (operation, endpoint key, request) so ordering and content can be asserted,
    /// and answers with real FHIR R4 payloads — CHO's own parsing and validation
    /// still run for real.
    /// </summary>
    private sealed class ScriptedPriorPayer : IPayerToPayerRemoteClient
    {
        private RemoteCallOutcome _matchOutcome;
        private RemoteCallOutcome _exportOutcome;
        private readonly string? _matchPayload;
        private readonly string? _exportPayload;

        private ScriptedPriorPayer(
            RemoteCallOutcome matchOutcome, RemoteCallOutcome exportOutcome,
            string? matchPayload, string? exportPayload)
        {
            _matchOutcome = matchOutcome;
            _exportOutcome = exportOutcome;
            _matchPayload = matchPayload;
            _exportPayload = exportPayload;
        }

        public List<(string Operation, string EndpointKey)> Calls { get; } = [];
        public List<RemoteMemberMatchRequest> MatchRequests { get; } = [];
        public List<RemoteMemberDataRequest> DataRequests { get; } = [];

        public static ScriptedPriorPayer HappyPath() => new(
            RemoteCallOutcome.Success, RemoteCallOutcome.Success,
            PriorPayerPayloads.MatchBundle(RemoteMemberId),
            PriorPayerPayloads.ExportBundle(RemoteMemberId));

        public static ScriptedPriorPayer MatchFails(RemoteCallOutcome outcome) => new(
            outcome, RemoteCallOutcome.Success, null,
            PriorPayerPayloads.ExportBundle(RemoteMemberId));

        // The payloads are the ones the peer would return once healthy; the
        // outcome flags decide whether it answers at all (so Recover() resumes a
        // real exchange rather than an empty one).
        public static ScriptedPriorPayer ExportFails(RemoteCallOutcome outcome) => new(
            RemoteCallOutcome.Success, outcome,
            PriorPayerPayloads.MatchBundle(RemoteMemberId),
            PriorPayerPayloads.ExportBundle(RemoteMemberId));

        public static ScriptedPriorPayer WithPayloads(string? matchPayload, string? exportPayload) => new(
            RemoteCallOutcome.Success, RemoteCallOutcome.Success, matchPayload, exportPayload);

        /// <summary>The peer's endpoint starts answering again (retry scenarios).</summary>
        public void Recover()
        {
            _matchOutcome = RemoteCallOutcome.Success;
            _exportOutcome = RemoteCallOutcome.Success;
        }

        public Task<RemoteCallResponse> MatchMemberAsync(
            PayerToPayerEndpoint endpoint, RemoteMemberMatchRequest request, CancellationToken ct = default)
        {
            Calls.Add(("member-match", endpoint.EndpointKey));
            MatchRequests.Add(request);
            return Task.FromResult(_matchOutcome == RemoteCallOutcome.Success
                ? RemoteCallResponse.Success(_matchPayload ?? string.Empty)
                : RemoteCallResponse.Failure(_matchOutcome));
        }

        public Task<RemoteCallResponse> RequestMemberDataAsync(
            PayerToPayerEndpoint endpoint, RemoteMemberDataRequest request, CancellationToken ct = default)
        {
            Calls.Add(("member-data-export", endpoint.EndpointKey));
            DataRequests.Add(request);
            return Task.FromResult(_exportOutcome == RemoteCallOutcome.Success
                ? RemoteCallResponse.Success(_exportPayload ?? string.Empty)
                : RemoteCallResponse.Failure(_exportOutcome));
        }
    }

    /// <summary>Real FHIR R4 payloads a conformant prior payer would return.</summary>
    private static class PriorPayerPayloads
    {
        private static readonly JsonSerializerOptions FhirJson =
            new JsonSerializerOptions().ForFhir(ModelInfo.ModelInspector);

        public static string MatchBundle(string memberId) => Serialize(new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Entry =
            [
                Entry(new Patient { Id = memberId, BirthDate = "1955-07-14" }),
                Entry(new Coverage
                {
                    Id = "PRIOR-COV-1",
                    Status = FinancialResourceStatusCodes.Cancelled,
                    Beneficiary = new ResourceReference($"Patient/{memberId}"),
                }),
            ],
        });

        public static string ExportBundle(string memberId, string? foreignPatientOnClaim = null) => Serialize(new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Entry =
            [
                Entry(new Patient { Id = memberId, BirthDate = "1955-07-14" }),
                Entry(new Coverage
                {
                    Id = "PRIOR-COV-1",
                    Status = FinancialResourceStatusCodes.Cancelled,
                    Beneficiary = new ResourceReference($"Patient/{memberId}"),
                }),
                Entry(Eob("PRIOR-EOB-1", memberId)),
                Entry(Eob("PRIOR-EOB-2", foreignPatientOnClaim ?? memberId)),
            ],
        });

        // A patient value that is already a URL is used verbatim, so a peer can
        // be made to reference a member by absolute URL as well as relatively.
        private static ExplanationOfBenefit Eob(string id, string patient) => new()
        {
            Id = id,
            Status = ExplanationOfBenefit.ExplanationOfBenefitStatus.Active,
            Patient = new ResourceReference(
                patient.StartsWith("http", StringComparison.Ordinal) ? patient : $"Patient/{patient}"),
        };

        private static Bundle.EntryComponent Entry(Resource resource) =>
            new() { FullUrl = $"{resource.TypeName}/{resource.Id}", Resource = resource };

        private static string Serialize(Bundle bundle) => JsonSerializer.Serialize(bundle, FhirJson);
    }

    /// <summary>A CHO coverage source returning a fixed set of coverages for the tenant.</summary>
    private sealed class FixedCoverageSource : IPayerToPayerMemberMatchSource
    {
        private readonly IReadOnlyList<ChoCoverage> _coverages;

        public FixedCoverageSource(string tenant, params ChoCoverage[] coverages)
        {
            ServedTenantId = tenant;
            _coverages = coverages;
        }

        public string ServedTenantId { get; }

        public Task<IReadOnlyList<ChoMember>> GetMembersAsync(string tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ChoMember>>(Array.Empty<ChoMember>());

        public Task<IReadOnlyList<ChoCoverage>> GetCoveragesAsync(
            string tenantId, string memberId, CancellationToken ct = default) =>
            Task.FromResult(string.Equals(tenantId, ServedTenantId, StringComparison.Ordinal)
                ? _coverages
                : Array.Empty<ChoCoverage>());
    }
}
