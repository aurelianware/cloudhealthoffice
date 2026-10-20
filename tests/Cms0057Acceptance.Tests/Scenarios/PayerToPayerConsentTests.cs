using CloudHealthOffice.Consent.Contracts;
using Hl7.Fhir.Serialization;
using Cms0057Acceptance.Tests.TestSupport;
using FhirService.Models;
using FhirService.Models.PayerToPayer;
using FhirService.Services;
using FhirService.Services.PayerToPayer;
using FhirService.Services.PayerToPayer.Ingestion;
using FhirService.Services.PayerToPayer.Outbound;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Cms0057Acceptance.Tests.Scenarios;

/// <summary>
/// P2P-03 — Payer-to-Payer opt-in enforcement.
///
/// Inbound (CHO is the prior payer, ADR 022): the requesting payer collected the
/// member's opt-in and supplies it as an HRex Consent naming itself; CHO records
/// it per payer and serves only that payer. Outbound (CHO is the new payer): the
/// member's opt-in is read from CHO's own registry, as below.
///
/// Authorization for a Payer-to-Payer exchange is no longer "the member has some
/// Active consent". It is an Active consent whose PURPOSE is
/// <see cref="ConsentPurposeOfUse.PayerToPayerExchange"/>, evaluated
/// server-side against the registry by the shared
/// <see cref="ConsentAuthorizationPolicy"/> — the same policy for both
/// directions of the exchange, so neither can drift more permissive.
///
/// These scenarios drive the production gate
/// (<see cref="ConsentRegistryPayerToPayerConsentGate"/>) through the real
/// inbound (<see cref="PayerToPayerExchangeService"/>) and outbound
/// (<see cref="PayerToPayerOutboundService"/>) workflows. Synthetic data only.
///
/// Traceability:
///   purpose   src/services/shared/CloudHealthOffice.Consent.Contracts/ConsentPurposeOfUse.cs
///   policy    src/services/shared/CloudHealthOffice.Consent.Contracts/ConsentAuthorizationPolicy.cs
///   gate      src/services/fhir-service/Services/PayerToPayer/PayerToPayerConsentGate.cs
///   registry  src/services/consent-service/Models/Consent.cs (PurposeOfUse + ToAuthorizationSnapshot)
/// </summary>
public class PayerToPayerConsentTests
{
    private const string Member = "pat-001";
    private const string TargetPayer = "PRIOR-PLAN";
    private const string RemoteMemberId = "prior-1001";

    // ── Consent fixtures ────────────────────────────────────────────────────────

    private static ConfiguredConsentRecord Consent(
        ConsentPurposeOfUse purpose,
        ConsentLifecycleStatus status = ConsentLifecycleStatus.Active,
        string member = Member,
        DateTime? effectiveAt = null,
        DateTime? expiresAt = null,
        string? consentId = null) => new()
    {
        MemberId = member,
        ConsentId = consentId ?? $"consent-{purpose}-{status}",
        PurposeOfUse = purpose,
        Status = status,
        EffectiveAt = effectiveAt,
        ExpiresAt = expiresAt,
    };

    // ── Inbound respond: the requesting payer's consent, bound to that payer (D-13, ADR 022) ──

    private const string RequestingPayer = "receiving-payer-001";
    private const string OtherPayer = "other-payer-002";

    private static (PayerToPayerExchangeService Service, InMemoryPayerToPayerConsentAttestationStore Store) Inbound()
    {
        var provider = new MockPatientAccessDataProvider();
        var source = new PatientAccessPayerToPayerMemberSource(
            provider, Options.Create(new FhirAdapterOptions { TenantId = AcceptanceContext.TenantId }));
        var store = new InMemoryPayerToPayerConsentAttestationStore();

        return (new PayerToPayerExchangeService(
            new PayerToPayerMemberResolver(source), new PatientAccessPayerToPayerExportSource(provider),
            store, AcceptanceContext.Directory(), TimeProvider.System,
            new PayerToPayerExportBuilder(),
            AcceptanceContext.Logger<PayerToPayerExchangeService>()), store);
    }

    private static PayerToPayerExchangeRequest InboundRequest(
        System.Text.Json.Nodes.JsonObject? consent = null, string payer = RequestingPayer, string member = Member) => new()
    {
        TenantId = AcceptanceContext.TenantId,
        ReceivingPayerId = payer,
        MemberId = member,
        ExchangeDateUtc = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc),
        Consent = consent,
    };

    [Fact]
    [Trait("Scenario", "P2P-03")]
    [Trait("Backend", "Replace")]
    public async Task P2P03_Replace_AConsentNamingTheRequestingPayer_AuthorizesInboundRespond()
    {
        var (service, store) = Inbound();

        var result = await service.RespondAsync(InboundRequest(AcceptanceContext.PdexConsent(RequestingPayer)));

        result.Outcome.Should().Be(PayerToPayerOutcome.Exported);
        result.Audit.AuthorizingConsentId.Should().StartWith("p2p-consent:", "the decision names its evidence");
        store.Records.Should().ContainSingle(r => r.ReceivingPayerId == RequestingPayer && r.MemberId == Member);
    }

    [Fact]
    [Trait("Scenario", "P2P-03")]
    [Trait("Backend", "Replace")]
    public async Task P2P03_Replace_ARecordedConsentAuthorizesThatPayerAndNoOther()
    {
        // The D-13 remainder: consent bound to the receiving payer. One payer's
        // recorded consent opens this member to that payer only.
        var (service, _) = Inbound();
        (await service.RespondAsync(InboundRequest(AcceptanceContext.PdexConsent(RequestingPayer))))
            .Outcome.Should().Be(PayerToPayerOutcome.Exported);

        (await service.RespondAsync(InboundRequest())).Outcome.Should().Be(PayerToPayerOutcome.Exported,
            "the same payer's consent is on record");
        var other = await service.RespondAsync(InboundRequest(payer: OtherPayer));
        other.Outcome.Should().Be(PayerToPayerOutcome.NotAuthorized);
        other.Bundle.Should().BeNull();
    }

    [Fact]
    [Trait("Scenario", "P2P-03")]
    [Trait("Backend", "Replace")]
    public async Task P2P03_Replace_NoConsentOnRecord_IsNotAuthorized()
    {
        var (service, _) = Inbound();

        var result = await service.RespondAsync(InboundRequest());

        result.Outcome.Should().Be(PayerToPayerOutcome.NotAuthorized);
        result.Bundle.Should().BeNull();
        result.Audit.ConsentDecisionReason.Should().Be(nameof(ConsentAuthorizationReason.NoConsentOnRecord));
    }

    [Fact]
    [Trait("Scenario", "P2P-03")]
    [Trait("Backend", "Replace")]
    public async Task P2P03_Replace_AConsentNamingAnotherPayer_IsRefused()
    {
        // A payer cannot pass off a consent the member gave someone else.
        var (service, store) = Inbound();

        var result = await service.RespondAsync(InboundRequest(AcceptanceContext.PdexConsent(OtherPayer)));

        result.Outcome.Should().Be(PayerToPayerOutcome.NotAuthorized);
        result.Audit.ConsentFailure.Should().Be(nameof(PdexConsentFailure.UnknownParty));
        store.Records.Should().BeEmpty();
    }

    [Theory]
    [Trait("Scenario", "P2P-03")]
    [Trait("Backend", "Replace")]
    [InlineData(-30, -1)]   // ended yesterday
    [InlineData(1, 30)]     // starts tomorrow
    public async Task P2P03_Replace_AConsentNotInForce_IsRefused(int startDays, int endDays)
    {
        var (service, _) = Inbound();
        var consent = AcceptanceContext.PdexConsent(RequestingPayer,
            DateTime.UtcNow.AddDays(startDays), DateTime.UtcNow.AddDays(endDays));

        var result = await service.RespondAsync(InboundRequest(consent));

        result.Outcome.Should().Be(PayerToPayerOutcome.NotAuthorized);
        result.Audit.ConsentFailure.Should().Be(nameof(PdexConsentFailure.NotInForce));
    }

    [Fact]
    [Trait("Scenario", "P2P-03")]
    [Trait("Backend", "Replace")]
    public async Task P2P03_Replace_ARecordedConsentLapsesAtItsEnd()
    {
        // Judged at the disclosure instant, not at the request's exchange date.
        var (service, store) = Inbound();
        await store.RecordAsync(new PayerToPayerConsentAttestation
        {
            TenantId = AcceptanceContext.TenantId, MemberId = Member, ReceivingPayerId = RequestingPayer,
            Policy = PdexConsentValidator.SensitivePolicy,
            PeriodStartUtc = DateTime.UtcNow.AddYears(-1), PeriodEndUtc = DateTime.UtcNow.AddMinutes(-1),
            ConsentFingerprint = "f", RecordedAtUtc = DateTime.UtcNow.AddYears(-1), RecordedBy = "test",
        });

        (await service.RespondAsync(InboundRequest())).Outcome.Should().Be(PayerToPayerOutcome.NotAuthorized);
    }

    [Fact]
    [Trait("Scenario", "P2P-03")]
    [Trait("Backend", "Replace")]
    public async Task P2P03_Replace_TheRegularPolicyIsRefusedBecauseCHOCannotSegmentSensitiveData()
    {
        var (service, _) = Inbound();

        var result = await service.RespondAsync(InboundRequest(
            AcceptanceContext.PdexConsent(RequestingPayer, policy: PdexConsentValidator.RegularPolicy)));

        result.Outcome.Should().Be(PayerToPayerOutcome.NotAuthorized);
        result.Audit.ConsentFailure.Should().Be(nameof(PdexConsentFailure.SegmentationUnsupported));
    }

    [Fact]
    [Trait("Scenario", "P2P-03")]
    [Trait("Backend", "Replace")]
    public async Task P2P03_Replace_AConsentForOneMemberCannotBeReplayedForAnother()
    {
        // The consent names pat-001; sent with an export for pat-002 it opens nothing.
        var (service, store) = Inbound();

        var result = await service.RespondAsync(InboundRequest(
            AcceptanceContext.PdexConsent(RequestingPayer, memberId: "pat-001"), member: "pat-002"));

        result.Outcome.Should().BeOneOf(PayerToPayerOutcome.NotAuthorized, PayerToPayerOutcome.NoMatch);
        result.Bundle.Should().BeNull();
        store.Records.Should().BeEmpty();
    }

    [Fact]
    [Trait("Scenario", "P2P-03")]
    [Trait("Backend", "Replace")]
    public async Task P2P03_Replace_OneMembersConsentDoesNotOpenAnother()
    {
        var (service, _) = Inbound();
        (await service.RespondAsync(InboundRequest(AcceptanceContext.PdexConsent(RequestingPayer))))
            .Outcome.Should().Be(PayerToPayerOutcome.Exported);

        (await service.RespondAsync(InboundRequest(member: "pat-002"))).Outcome
            .Should().BeOneOf(PayerToPayerOutcome.NotAuthorized, PayerToPayerOutcome.NoMatch);
    }

    // ── Outbound initiation ─────────────────────────────────────────────────────

    private sealed record OutboundHarness(
        PayerToPayerOutboundService Service, RecordingPeer Peer, MutableConsentSource Consents);

    private static OutboundHarness OutboundWith(params ConfiguredConsentRecord[] consents)
    {
        var provider = new MockPatientAccessDataProvider();
        var adapterOptions = Options.Create(new FhirAdapterOptions { TenantId = AcceptanceContext.TenantId });

        var directory = Options.Create(new PayerToPayerDirectoryOptions
        {
            LocalPayerId = "cloud-health-office",
            PayersByTenant = new()
            {
                [AcceptanceContext.TenantId] =
                [
                    new PayerToPayerEndpointEntry
                    {
                        PayerId = TargetPayer,
                        EndpointKey = "prior-plan-fhir",
                        BaseUrl = "https://prior-payer.example/fhir/r4",
                    },
                ],
            },
        });

        var consentSource = new MutableConsentSource(consents);
        var peer = new RecordingPeer();

        var service = new PayerToPayerOutboundService(
            new PatientAccessPayerToPayerMemberSource(provider, adapterOptions),
            new PatientAccessPayerToPayerMemberMatchSource(provider, adapterOptions),
            new ConsentRegistryPayerToPayerConsentGate(
                new FhirService.Services.Consent.RegistryConsentEvaluator(
                    consentSource,
                    AcceptanceContext.Logger<FhirService.Services.Consent.RegistryConsentEvaluator>())),
            new ConfiguredPayerToPayerEndpointResolver(
                directory, AcceptanceContext.Logger<ConfiguredPayerToPayerEndpointResolver>()),
            peer,
            new InMemoryPayerToPayerOutboundExchangeStore(),
            new PayerToPayerPackageIngestionService(
                new InMemoryPayerToPayerImportRepository(),
                AcceptanceContext.Logger<PayerToPayerPackageIngestionService>(),
                new FhirService.Services.Clinical.ClinicalPayloadValidator()),
            directory,
            AcceptanceContext.Logger<PayerToPayerOutboundService>());

        return new OutboundHarness(service, peer, consentSource);
    }

    private static PayerToPayerOutboundRequest OutboundRequest(string? transitionKey = "transition-1") => new()
    {
        TenantId = AcceptanceContext.TenantId,
        MemberId = Member,
        TargetPayerId = TargetPayer,
        TransitionKey = transitionKey,
        InitiatedBy = "enrollment:coverage-transition",
        ExchangeDateUtc = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    [Trait("Scenario", "P2P-03")]
    [Trait("Backend", "Replace")]
    public async Task P2P03_Replace_ActivePayerToPayerConsent_AuthorizesOutboundInitiation()
    {
        var harness = OutboundWith(Consent(ConsentPurposeOfUse.PayerToPayerExchange));

        var result = await harness.Service.InitiateAsync(OutboundRequest());

        result.Succeeded.Should().BeTrue();
        harness.Peer.Calls.Should().Equal("member-match", "member-data-export");

        // The exchange record carries the authorization it ran under.
        result.Exchange.AuthorizingConsentId.Should().Be("consent-PayerToPayerExchange-Active");
        result.Exchange.ConsentDecisionReason.Should().Be(nameof(ConsentAuthorizationReason.Granted));
        result.Exchange.ConsentEvaluatedAtUtc.Should().NotBeNull();
        result.Audit.AuthorizingConsentId.Should().Be("consent-PayerToPayerExchange-Active");

        // Both calls carry the member's opt-in as an HRex consent the prior payer
        // can bind: CHO as recipient, the prior payer as the only other party.
        harness.Peer.Consents.Should().HaveCount(2).And.OnlyContain(c => c != null);
        foreach (var consent in harness.Peer.Consents)
            PdexConsentValidator.Validate(consent, "cloud-health-office", TargetPayer, DateTime.UtcNow)
                .Valid.Should().BeTrue("a CHO prior payer would accept it");
    }

    [Theory]
    [Trait("Scenario", "P2P-03")]
    [Trait("Backend", "Replace")]
    [InlineData(ConsentPurposeOfUse.ProviderAccess)]
    [InlineData(ConsentPurposeOfUse.Unspecified)]
    public async Task P2P03_Replace_WithoutPayerToPayerPurpose_NoMemberIdentityLeavesCho(
        ConsentPurposeOfUse purpose)
    {
        // The strongest privacy boundary in this workflow: without authorization
        // for THIS purpose, CHO does not even tell the other payer who it is
        // asking about. $member-match is a disclosure too.
        var harness = OutboundWith(Consent(purpose));

        var result = await harness.Service.InitiateAsync(OutboundRequest());

        result.Exchange.Status.Should().Be(PayerToPayerOutboundStatus.NotAuthorized);
        harness.Peer.Calls.Should().BeEmpty("no identity and no data may be disclosed without P2P consent");
        result.Exchange.ConsentDecisionReason.Should().Be(nameof(ConsentAuthorizationReason.NoConsentForPurpose));
    }

    [Fact]
    [Trait("Scenario", "P2P-03")]
    [Trait("Backend", "Replace")]
    public async Task P2P03_Replace_RevokedConsent_StopsOutboundBeforeAnyDisclosure()
    {
        var harness = OutboundWith(
            Consent(ConsentPurposeOfUse.PayerToPayerExchange, ConsentLifecycleStatus.Revoked));

        var result = await harness.Service.InitiateAsync(OutboundRequest());

        result.Exchange.Status.Should().Be(PayerToPayerOutboundStatus.NotAuthorized);
        result.Exchange.ConsentDecisionReason.Should().Be(nameof(ConsentAuthorizationReason.Revoked));
        harness.Peer.Calls.Should().BeEmpty();
    }

    // ── Lifecycle: grant, revoke, re-grant ──────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-03")]
    [Trait("Backend", "Replace")]
    public async Task P2P03_Replace_RevokingAfterAnExchange_DeniesTheNextOne()
    {
        var harness = OutboundWith(Consent(ConsentPurposeOfUse.PayerToPayerExchange));

        var allowed = await harness.Service.InitiateAsync(OutboundRequest("transition-1"));
        allowed.Succeeded.Should().BeTrue();

        harness.Consents.Revoke();
        var denied = await harness.Service.InitiateAsync(OutboundRequest("transition-2"));

        denied.Exchange.Status.Should().Be(PayerToPayerOutboundStatus.NotAuthorized);
        denied.Exchange.ConsentDecisionReason.Should().Be(nameof(ConsentAuthorizationReason.Revoked));
    }

    [Fact]
    [Trait("Scenario", "P2P-03")]
    [Trait("Backend", "Replace")]
    public async Task P2P03_Replace_RevocationBetweenMatchAndExport_StopsBeforeTheDataRequest()
    {
        // The documented policy: consent is re-checked at each disclosure
        // boundary. A member who revokes while the match is in flight does not
        // have their record pulled.
        var harness = OutboundWith(Consent(ConsentPurposeOfUse.PayerToPayerExchange));
        harness.Peer.OnMatch = harness.Consents.Revoke;

        var result = await harness.Service.InitiateAsync(OutboundRequest());

        result.Succeeded.Should().BeFalse();
        result.Exchange.Status.Should().Be(PayerToPayerOutboundStatus.NotAuthorized);
        result.Exchange.ConsentDecisionReason.Should().Be(nameof(ConsentAuthorizationReason.Revoked));
        harness.Peer.Calls.Should().Contain("member-match", "the match had already gone out");
        harness.Peer.Calls.Should().NotContain("member-data-export",
            "the export must not follow a revocation that landed mid-exchange");
    }

    [Fact]
    [Trait("Scenario", "P2P-03")]
    [Trait("Backend", "Replace")]
    public async Task P2P03_Replace_RetryAfterRevocationIsDenied_EvenThoughTheExchangeExists()
    {
        // A retry re-asks the registry; it does not inherit the authorization the
        // first attempt ran under.
        var harness = OutboundWith(Consent(ConsentPurposeOfUse.PayerToPayerExchange));
        harness.Peer.FailExport = true;

        var failed = await harness.Service.InitiateAsync(OutboundRequest());
        failed.Succeeded.Should().BeFalse();

        harness.Consents.Revoke();
        harness.Peer.FailExport = false;
        var retried = await harness.Service.InitiateAsync(OutboundRequest());

        retried.Exchange.Status.Should().Be(PayerToPayerOutboundStatus.NotAuthorized);
        retried.Exchange.ConsentDecisionReason.Should().Be(nameof(ConsentAuthorizationReason.Revoked));
    }

    // ── Registry semantics ──────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-03")]
    [Trait("Backend", "Replace")]
    public void P2P03_Replace_TheRegistryHoldsBothPurposesUnderOneLifecycle()
    {
        // One registry, one lifecycle, purposes distinguished on the record —
        // not two stores bolted together.
        var p2p = new global::ConsentService.Models.Consent
        {
            TenantId = AcceptanceContext.TenantId,
            MemberId = Member,
            ConsentType = global::ConsentService.Models.ConsentType.GeneralAuthorization,
            PurposeOfUse = ConsentPurposeOfUse.PayerToPayerExchange,
            Status = global::ConsentService.Models.ConsentStatus.Active,
            GrantedBy = Member,
        };
        var providerAccess = new global::ConsentService.Models.Consent
        {
            TenantId = AcceptanceContext.TenantId,
            MemberId = Member,
            ConsentType = global::ConsentService.Models.ConsentType.GeneralAuthorization,
            PurposeOfUse = ConsentPurposeOfUse.ProviderAccess,
            Status = global::ConsentService.Models.ConsentStatus.Active,
            GrantedBy = Member,
        };

        // Same aggregate, same state machine, different permission.
        p2p.ToAuthorizationSnapshot().PurposeOfUse.Should().Be(ConsentPurposeOfUse.PayerToPayerExchange);
        providerAccess.ToAuthorizationSnapshot().PurposeOfUse.Should().Be(ConsentPurposeOfUse.ProviderAccess);

        // And the P2P question is answered only by the P2P record.
        var decision = ConsentAuthorizationPolicy.Evaluate(
            AcceptanceContext.TenantId, Member, ConsentPurposeOfUse.PayerToPayerExchange,
            [providerAccess.ToAuthorizationSnapshot()], DateTime.UtcNow);
        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(ConsentAuthorizationReason.NoConsentForPurpose);
    }

    [Fact]
    [Trait("Scenario", "P2P-03")]
    [Trait("Backend", "Replace")]
    public async Task P2P03_Replace_ConsentIsNeverAcceptedFromACaller()
    {
        // Structural. CHO's own outbound initiation takes no caller-asserted
        // authorization: the member's opt-in is read from the registry. Inbound,
        // the only consent a requesting payer can supply is an HRex Consent
        // RESOURCE, validated and bound to that payer (ADR 022), never a flag.
        var outboundFields = typeof(PayerToPayerOutboundRequest).GetProperties()
            .Concat(typeof(FhirService.Controllers.PayerToPayerInitiateRequestDto).GetProperties())
            .Select(p => p.Name);
        outboundFields.Should().NotContain(n =>
            n.Contains("consent", StringComparison.OrdinalIgnoreCase)
            || n.Contains("optIn", StringComparison.OrdinalIgnoreCase)
            || n.Contains("authorized", StringComparison.OrdinalIgnoreCase)
            || n.Contains("purpose", StringComparison.OrdinalIgnoreCase));

        var inbound = typeof(PayerToPayerExchangeRequest).GetProperties()
            .Concat(typeof(FhirService.Controllers.PayerToPayerExportRequestDto).GetProperties())
            .Concat(typeof(MemberMatchRequest).GetProperties())
            .Concat(typeof(FhirService.Controllers.MemberMatchRequestDto).GetProperties())
            .ToList();
        inbound.Where(p => p.Name.Contains("consent", StringComparison.OrdinalIgnoreCase))
            .Should().OnlyContain(p => p.PropertyType == typeof(System.Text.Json.Nodes.JsonObject));
        inbound.Select(p => p.Name).Should().NotContain(n =>
            n.Contains("optIn", StringComparison.OrdinalIgnoreCase)
            || n.Contains("authorized", StringComparison.OrdinalIgnoreCase)
            || n.Contains("purpose", StringComparison.OrdinalIgnoreCase));

        // And an unreadable registry denies rather than defaults open.
        var gate = new ConsentRegistryPayerToPayerConsentGate(
            new FhirService.Services.Consent.RegistryConsentEvaluator(
                new ThrowingConsentSource(),
                AcceptanceContext.Logger<FhirService.Services.Consent.RegistryConsentEvaluator>()));

        var decision = await gate.EvaluateAsync(AcceptanceContext.TenantId, Member);
        decision.Allowed.Should().BeFalse();
    }

    // ── Test doubles ────────────────────────────────────────────────────────────

    /// <summary>A consent source whose records can change mid-test (grant, revoke).</summary>
    private sealed class MutableConsentSource : IPayerToPayerConsentSource
    {
        private List<ConfiguredConsentRecord> _records;

        public MutableConsentSource(IEnumerable<ConfiguredConsentRecord> records)
            => _records = records.ToList();

        /// <summary>The member revokes every consent on record.</summary>
        public void Revoke() => _records = _records
            .Select(r => new ConfiguredConsentRecord
            {
                MemberId = r.MemberId,
                ConsentId = r.ConsentId,
                PurposeOfUse = r.PurposeOfUse,
                Status = ConsentLifecycleStatus.Revoked,
                EffectiveAt = r.EffectiveAt,
                ExpiresAt = r.ExpiresAt,
            })
            .ToList();

        public Task<IReadOnlyList<ConsentAuthorizationSnapshot>> GetConsentsAsync(
            string tenantId, string memberId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ConsentAuthorizationSnapshot>>(_records
                .Where(r => string.Equals(r.MemberId, memberId, StringComparison.Ordinal))
                .Select(r => new ConsentAuthorizationSnapshot
                {
                    TenantId = tenantId,
                    MemberId = memberId,
                    ConsentId = r.ConsentId!,
                    PurposeOfUse = r.PurposeOfUse,
                    Status = r.Status,
                    EffectiveAt = r.EffectiveAt,
                    ExpiresAt = r.ExpiresAt,
                })
                .ToList());
    }

    /// <summary>A registry that is down. Must deny, never default open.</summary>
    private sealed class ThrowingConsentSource : IPayerToPayerConsentSource
    {
        public Task<IReadOnlyList<ConsentAuthorizationSnapshot>> GetConsentsAsync(
            string tenantId, string memberId, CancellationToken ct = default)
            => throw new InvalidOperationException("consent registry unavailable");
    }

    /// <summary>A prior payer that records what CHO disclosed to it, and when.</summary>
    private sealed class RecordingPeer : IPayerToPayerRemoteClient
    {
        public List<string> Calls { get; } = [];
        public List<System.Text.Json.Nodes.JsonObject?> Consents { get; } = [];

        /// <summary>Runs when the match is issued — lets a test revoke mid-exchange.</summary>
        public Action? OnMatch { get; set; }

        public bool FailExport { get; set; }

        public Task<RemoteCallResponse> MatchMemberAsync(
            PayerToPayerEndpoint endpoint, RemoteMemberMatchRequest request, CancellationToken ct = default)
        {
            Calls.Add("member-match");
            Consents.Add(request.Consent);
            OnMatch?.Invoke();
            return Task.FromResult(RemoteCallResponse.Success(ConsentTestPackages.MatchBundle(RemoteMemberId)));
        }

        public Task<RemoteCallResponse> RequestMemberDataAsync(
            PayerToPayerEndpoint endpoint, RemoteMemberDataRequest request, CancellationToken ct = default)
        {
            Calls.Add("member-data-export");
            Consents.Add(request.Consent);
            return Task.FromResult(FailExport
                ? RemoteCallResponse.Failure(RemoteCallOutcome.Unavailable)
                : RemoteCallResponse.Success(ConsentTestPackages.ExportBundle(RemoteMemberId)));
        }
    }
}

/// <summary>Minimal real FHIR payloads for the consent scenarios.</summary>
internal static class ConsentTestPackages
{
    private static readonly System.Text.Json.JsonSerializerOptions FhirJson =
        new System.Text.Json.JsonSerializerOptions().ForFhir(Hl7.Fhir.Model.ModelInfo.ModelInspector);

    public static string MatchBundle(string memberId) => Serialize(new Hl7.Fhir.Model.Bundle
    {
        Type = Hl7.Fhir.Model.Bundle.BundleType.Collection,
        Entry = [Entry(new Hl7.Fhir.Model.Patient { Id = memberId, BirthDate = "1955-07-14" })],
    });

    public static string ExportBundle(string memberId) => Serialize(new Hl7.Fhir.Model.Bundle
    {
        Type = Hl7.Fhir.Model.Bundle.BundleType.Collection,
        Entry =
        [
            Entry(new Hl7.Fhir.Model.Patient { Id = memberId, BirthDate = "1955-07-14" }),
            Entry(new Hl7.Fhir.Model.ExplanationOfBenefit
            {
                Id = "PRIOR-EOB-1",
                Status = Hl7.Fhir.Model.ExplanationOfBenefit.ExplanationOfBenefitStatus.Active,
                Patient = new Hl7.Fhir.Model.ResourceReference($"Patient/{memberId}"),
            }),
        ],
    });

    private static Hl7.Fhir.Model.Bundle.EntryComponent Entry(Hl7.Fhir.Model.Resource resource) =>
        new() { FullUrl = $"{resource.TypeName}/{resource.Id}", Resource = resource };

    private static string Serialize(Hl7.Fhir.Model.Bundle bundle) =>
        System.Text.Json.JsonSerializer.Serialize(bundle, FhirJson);
}
