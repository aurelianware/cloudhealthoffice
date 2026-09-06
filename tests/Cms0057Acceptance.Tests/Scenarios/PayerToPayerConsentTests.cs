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
/// P2P-03 — Payer-to-Payer opt-in enforcement as a first-class consent purpose.
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

    // ── Inbound respond ─────────────────────────────────────────────────────────

    private static PayerToPayerExchangeService InboundWith(params ConfiguredConsentRecord[] consents)
    {
        var provider = new MockPatientAccessDataProvider();
        var source = new PatientAccessPayerToPayerMemberSource(
            provider, Options.Create(new FhirAdapterOptions { TenantId = AcceptanceContext.TenantId }));

        return new PayerToPayerExchangeService(
            new PayerToPayerMemberResolver(source), source,
            AcceptanceContext.ConsentGateFor(consents),
            new PayerToPayerExportBuilder(),
            AcceptanceContext.Logger<PayerToPayerExchangeService>());
    }

    private static PayerToPayerExchangeRequest InboundRequest(DateTime? exchangeDateUtc = null) => new()
    {
        TenantId = AcceptanceContext.TenantId,
        ReceivingPayerId = "receiving-payer-001",
        MemberId = Member,
        ExchangeDateUtc = exchangeDateUtc ?? new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    [Trait("Scenario", "P2P-03")]
    [Trait("Backend", "Replace")]
    public async Task P2P03_Replace_ActivePayerToPayerConsent_AuthorizesInboundRespond()
    {
        var result = await InboundWith(Consent(ConsentPurposeOfUse.PayerToPayerExchange))
            .RespondAsync(InboundRequest());

        result.Outcome.Should().Be(PayerToPayerOutcome.Exported);
        result.Bundle.Should().NotBeNull();

        // The audit names WHICH authorization allowed the disclosure.
        result.Audit.AuthorizingConsentId.Should().Be("consent-PayerToPayerExchange-Active");
        result.Audit.ConsentDecisionReason.Should().Be(nameof(ConsentAuthorizationReason.Granted));
    }

    [Fact]
    [Trait("Scenario", "P2P-03")]
    [Trait("Backend", "Replace")]
    public async Task P2P03_Replace_ProviderAccessConsentAlone_DoesNotAuthorizeInboundRespond()
    {
        // The member authorized their providers to see their data. That is not
        // permission to hand the record to another payer.
        var result = await InboundWith(Consent(ConsentPurposeOfUse.ProviderAccess))
            .RespondAsync(InboundRequest());

        result.Outcome.Should().Be(PayerToPayerOutcome.NotAuthorized);
        result.Bundle.Should().BeNull();
        result.Audit.ConsentDecisionReason.Should().Be(nameof(ConsentAuthorizationReason.NoConsentForPurpose));
    }

    [Fact]
    [Trait("Scenario", "P2P-03")]
    [Trait("Backend", "Replace")]
    public async Task P2P03_Replace_ConsentWithNoPurposeRecorded_DoesNotAuthorizeInboundRespond()
    {
        // A generic Active consent — the pre-purpose record shape — authorizes
        // nothing here. This is the ambiguity the purpose axis exists to remove,
        // and the migration position: history is not reinterpreted.
        var result = await InboundWith(Consent(ConsentPurposeOfUse.Unspecified))
            .RespondAsync(InboundRequest());

        result.Outcome.Should().Be(PayerToPayerOutcome.NotAuthorized);
        result.Audit.ConsentDecisionReason.Should().Be(nameof(ConsentAuthorizationReason.NoConsentForPurpose));
    }

    [Fact]
    [Trait("Scenario", "P2P-03")]
    [Trait("Backend", "Replace")]
    public async Task P2P03_Replace_BackDatedExchangeDate_DoesNotAuthorizeLapsedConsent()
    {
        // ExchangeDateUtc arrives ON THE REQUEST and anchors the five-year
        // lookback window. It must not also decide WHEN authorization is judged.
        // A consent still persisted Active but whose period has run out is the
        // case that bites: expiry is compared against the evaluation instant, so
        // a receiving payer that back-dates ExchangeDateUtc (or a delayed or
        // replayed request) would be judged against consent as it stood while it
        // was still in force. The authorization instant is the disclosure
        // attempt — now — and CHO picks it, not the caller.
        var request = InboundRequest(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var lapsedSinceThen = Consent(
            ConsentPurposeOfUse.PayerToPayerExchange,
            effectiveAt: new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            expiresAt: new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var result = await InboundWith(lapsedSinceThen).RespondAsync(request);

        result.Outcome.Should().Be(PayerToPayerOutcome.NotAuthorized);
        result.Bundle.Should().BeNull();
        result.Audit.ConsentDecisionReason.Should().Be(nameof(ConsentAuthorizationReason.Expired));
    }

    [Fact]
    [Trait("Scenario", "P2P-03")]
    [Trait("Backend", "Replace")]
    public async Task P2P03_Replace_FutureDatedExchangeDate_DoesNotAuthorizeNotYetEffectiveConsent()
    {
        // The mirror image: a forward-dated ExchangeDateUtc must not reach a
        // consent that has not started yet. Same principle — the caller does not
        // choose the authorization instant in either direction.
        var request = InboundRequest(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var startsLater = Consent(
            ConsentPurposeOfUse.PayerToPayerExchange,
            effectiveAt: DateTime.UtcNow.AddYears(2));

        var result = await InboundWith(startsLater).RespondAsync(request);

        result.Outcome.Should().Be(PayerToPayerOutcome.NotAuthorized);
        result.Audit.ConsentDecisionReason.Should().Be(nameof(ConsentAuthorizationReason.NotYetEffective));
    }

    [Theory]
    [Trait("Scenario", "P2P-03")]
    [Trait("Backend", "Replace")]
    [InlineData(ConsentLifecycleStatus.Draft, nameof(ConsentAuthorizationReason.NotActivated))]
    [InlineData(ConsentLifecycleStatus.Revoked, nameof(ConsentAuthorizationReason.Revoked))]
    [InlineData(ConsentLifecycleStatus.Expired, nameof(ConsentAuthorizationReason.Expired))]
    public async Task P2P03_Replace_ConsentNotInForce_DeniesInboundRespondWithItsOwnReason(
        ConsentLifecycleStatus status, string expectedReason)
    {
        var result = await InboundWith(Consent(ConsentPurposeOfUse.PayerToPayerExchange, status))
            .RespondAsync(InboundRequest());

        result.Outcome.Should().Be(PayerToPayerOutcome.NotAuthorized);
        result.Bundle.Should().BeNull();
        // "They revoked" and "it lapsed" and "never activated" are different
        // facts an operator needs to tell apart.
        result.Audit.ConsentDecisionReason.Should().Be(expectedReason);
    }

    [Fact]
    [Trait("Scenario", "P2P-03")]
    [Trait("Backend", "Replace")]
    public async Task P2P03_Replace_ConsentThatHasLapsed_DeniesEvenWhilePersistedActive()
    {
        // The registry may not have written the expiry transition yet. The policy
        // applies the period itself, so a lapsed authorization cannot be used in
        // the window before the store catches up.
        var lapsed = Consent(
            ConsentPurposeOfUse.PayerToPayerExchange,
            ConsentLifecycleStatus.Active,
            expiresAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var result = await InboundWith(lapsed).RespondAsync(InboundRequest());

        result.Outcome.Should().Be(PayerToPayerOutcome.NotAuthorized);
        result.Audit.ConsentDecisionReason.Should().Be(nameof(ConsentAuthorizationReason.Expired));
    }

    [Fact]
    [Trait("Scenario", "P2P-03")]
    [Trait("Backend", "Replace")]
    public async Task P2P03_Replace_ConsentForAnotherMember_DoesNotAuthorizeThisMember()
    {
        var result = await InboundWith(
                Consent(ConsentPurposeOfUse.PayerToPayerExchange, member: "pat-002"))
            .RespondAsync(InboundRequest());

        result.Outcome.Should().Be(PayerToPayerOutcome.NotAuthorized);
        result.Audit.ConsentDecisionReason.Should().Be(nameof(ConsentAuthorizationReason.NoConsentOnRecord));
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
        // Structural: no request type on either direction carries an
        // authorization the caller can assert.
        var requestFields = typeof(PayerToPayerExchangeRequest).GetProperties()
            .Concat(typeof(PayerToPayerOutboundRequest).GetProperties())
            .Concat(typeof(FhirService.Controllers.PayerToPayerExportRequestDto).GetProperties())
            .Concat(typeof(FhirService.Controllers.PayerToPayerInitiateRequestDto).GetProperties())
            .Select(p => p.Name);

        requestFields.Should().NotContain(n =>
            n.Contains("consent", StringComparison.OrdinalIgnoreCase)
            || n.Contains("optIn", StringComparison.OrdinalIgnoreCase)
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

        /// <summary>Runs when the match is issued — lets a test revoke mid-exchange.</summary>
        public Action? OnMatch { get; set; }

        public bool FailExport { get; set; }

        public Task<RemoteCallResponse> MatchMemberAsync(
            PayerToPayerEndpoint endpoint, RemoteMemberMatchRequest request, CancellationToken ct = default)
        {
            Calls.Add("member-match");
            OnMatch?.Invoke();
            return Task.FromResult(RemoteCallResponse.Success(ConsentTestPackages.MatchBundle(RemoteMemberId)));
        }

        public Task<RemoteCallResponse> RequestMemberDataAsync(
            PayerToPayerEndpoint endpoint, RemoteMemberDataRequest request, CancellationToken ct = default)
        {
            Calls.Add("member-data-export");
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
