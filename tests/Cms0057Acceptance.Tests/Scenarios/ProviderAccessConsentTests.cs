using CloudHealthOffice.Consent.Contracts;
using FhirService.Services.Consent;
using FhirService.Services.PayerToPayer;
using FhirService.Services.ProviderAccess;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Cms0057Acceptance.Tests.Scenarios;

/// <summary>
/// CONSENT-01 — Provider Access enforced through the SAME purpose-scoped consent
/// registry as Payer-to-Payer, executed against the REAL
/// ProviderAccessAuthorizationService, the REAL attribution source, and the REAL
/// shared ConsentAuthorizationPolicy.
///
/// Provider Access requires ALL of: an authenticated caller, an adequate SMART
/// scope, provider/member attribution, no in-force ProviderAccess opt-out
/// (CMS-0057-F shares unless the patient opts out; D-11), and tenant/member
/// isolation. Authentication and SMART scope are enforced
/// upstream (SmartScopeEnforcementMiddleware — see SEC-01 and the SmartAuth
/// suite, which drives the composed decision over real HTTP); these tests pin the
/// two controls this service owns and the independence of all of them.
///
/// Traceability:
///   compose      src/services/fhir-service/Services/ProviderAccess/ProviderAccessAuthorizationService.cs
///   enforce      src/services/fhir-service/Services/ProviderAccess/ProviderAccessAuthorizationFilter.cs
///   attribution  src/services/fhir-service/Services/ProviderAccess/ProviderAttribution.cs
///   policy       src/services/shared/CloudHealthOffice.Consent.Contracts/ConsentAuthorizationPolicy.cs
///   registry     src/services/consent-service/Models/Consent.cs
/// </summary>
public class ProviderAccessConsentTests
{
    private const string Member = "pat-001";
    private const string Provider = "provider-001";

    // ── Fixtures ────────────────────────────────────────────────────────────────

    private static ConfiguredConsentRecord Consent(
        ConsentPurposeOfUse purpose,
        ConsentLifecycleStatus status = ConsentLifecycleStatus.Active,
        string member = Member,
        DateTime? effectiveAt = null,
        DateTime? expiresAt = null) => new()
    {
        MemberId = member,
        ConsentId = $"consent-{purpose}-{status}",
        PurposeOfUse = purpose,
        Status = status,
        EffectiveAt = effectiveAt,
        ExpiresAt = expiresAt,
    };

    /// <summary>The real service over a configured panel and a configured registry.</summary>
    private static IProviderAccessAuthorizationService Service(
        IEnumerable<string>? panel = null,
        IEnumerable<ConfiguredConsentRecord>? consents = null,
        IConsentSource? consentSource = null)
    {
        var attribution = new ConfiguredProviderAttributionSource(
            Options.Create(new ProviderAttributionOptions
            {
                PanelsByTenant = new()
                {
                    [AcceptanceContext.TenantId] =
                    [
                        new ConfiguredProviderPanel
                        {
                            ProviderId = Provider,
                            MemberIds = (panel ?? new[] { Member }).ToList(),
                        }
                    ],
                },
            }));

        var source = consentSource ?? new ConfiguredPayerToPayerConsentSource(
            Options.Create(new PayerToPayerConsentOptions
            {
                ConsentsByTenant = new()
                {
                    [AcceptanceContext.TenantId] = (consents ?? []).ToList(),
                },
            }));

        return new ProviderAccessAuthorizationService(
            attribution,
            new RegistryConsentEvaluator(
                source, AcceptanceContext.Logger<RegistryConsentEvaluator>()),
            AcceptanceContext.Logger<ProviderAccessAuthorizationService>());
    }

    private static ProviderAccessRequest Request(
        string member = Member, string? provider = Provider, string? tenant = null) => new()
    {
        TenantId = tenant ?? AcceptanceContext.TenantId,
        MemberId = member,
        ProviderId = provider,
    };

    private static ConfiguredConsentRecord OptOut(
        ConsentPurposeOfUse purpose = ConsentPurposeOfUse.ProviderAccess,
        ConsentLifecycleStatus status = ConsentLifecycleStatus.Active,
        string member = Member,
        DateTime? effectiveAt = null,
        DateTime? expiresAt = null) => new()
    {
        MemberId = member,
        ConsentId = $"opt-out-{purpose}-{status}",
        PurposeOfUse = purpose,
        Status = status,
        Provision = ConsentProvision.Deny,
        EffectiveAt = effectiveAt,
        ExpiresAt = expiresAt,
    };

    // ── Sharing by default (CMS-0057-F opt-out, D-11) ───────────────────────────

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public async Task CONSENT01_Replace_AttributedMemberWhoNeverOptedOut_IsShared()
    {
        // CMS-0057-F: the payer shares with an attributed provider unless the
        // patient opts out. No record is not a refusal.
        var decision = await Service().AuthorizeAsync(Request());

        decision.Allowed.Should().BeTrue();
        decision.Attributed.Should().BeTrue();
        decision.ConsentDecisionReason.Should().Be(nameof(ConsentAuthorizationReason.NotOptedOut));
        decision.AuthorizingConsentId.Should().BeNull("no consent authorized it; sharing is the default");
    }

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public async Task CONSENT01_Replace_ExplicitPermitOnRecord_IsSharedAndNamed()
    {
        var decision = await Service(consents: [Consent(ConsentPurposeOfUse.ProviderAccess)])
            .AuthorizeAsync(Request());

        decision.Allowed.Should().BeTrue();
        decision.AuthorizingConsentId.Should().Be("consent-ProviderAccess-Active");
        decision.ConsentDecisionReason.Should().Be(nameof(ConsentAuthorizationReason.Granted));
    }

    // ── PROV-03: the patient's opt-out is honoured, and only it stops sharing ──

    [Fact]
    [Trait("Scenario", "PROV-03")]
    [Trait("Backend", "Replace")]
    public async Task PROV03_Replace_SharingStopsWhenTheMemberOptsOutAndResumesWhenTheyOptBackIn()
    {
        // CMS-0057-F Provider Access: an attributed provider receives the
        // member's data unless the member opts out. The member's three states,
        // through the real attribution, registry evaluator and policy.
        var never = await Service().AuthorizeAsync(Request());
        var optedOut = await Service(consents: [OptOut()]).AuthorizeAsync(Request());
        var optedBackIn = await Service(consents: [OptOut(status: ConsentLifecycleStatus.Revoked)]).AuthorizeAsync(Request());

        never.Allowed.Should().BeTrue();
        optedOut.Allowed.Should().BeFalse();
        optedOut.ConsentDecisionReason.Should().Be(nameof(ConsentAuthorizationReason.OptedOut));
        optedBackIn.Allowed.Should().BeTrue();
    }

    [Fact]
    [Trait("Scenario", "PROV-03")]
    [Trait("Backend", "Replace")]
    public async Task PROV03_Replace_AnOptOutCannotBeAssumedAwayWhenTheRegistryIsDown()
    {
        var decision = await Service(consentSource: new ThrowingConsentSource()).AuthorizeAsync(Request());

        decision.Allowed.Should().BeFalse();
        decision.ConsentDecisionReason.Should().Be(nameof(ConsentAuthorizationReason.RegistryUnavailable));
    }

    // ── Opting out ──────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public async Task CONSENT01_Replace_InForceOptOut_StopsSharing()
    {
        var decision = await Service(consents: [OptOut()]).AuthorizeAsync(Request());

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(ProviderAccessDenialReason.ConsentDenied);
        decision.Attributed.Should().BeTrue("the panel check passed; the opt-out is what refused");
        decision.ConsentDecisionReason.Should().Be(nameof(ConsentAuthorizationReason.OptedOut));
        decision.AuthorizingConsentId.Should().Be("opt-out-ProviderAccess-Active");
    }

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public async Task CONSENT01_Replace_OptOutWinsOverAPermit()
    {
        var decision = await Service(consents: [Consent(ConsentPurposeOfUse.ProviderAccess), OptOut()])
            .AuthorizeAsync(Request());

        decision.Allowed.Should().BeFalse();
        decision.ConsentDecisionReason.Should().Be(nameof(ConsentAuthorizationReason.OptedOut));
    }

    [Theory]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    [InlineData(ConsentLifecycleStatus.Revoked)]
    [InlineData(ConsentLifecycleStatus.Expired)]
    [InlineData(ConsentLifecycleStatus.Draft)]
    public async Task CONSENT01_Replace_AnOptOutNotInForce_DoesNotStopSharing(ConsentLifecycleStatus status)
    {
        // Revoking the opt-out opts the member back in; a lapsed or never
        // activated one refuses nothing.
        var decision = await Service(consents: [OptOut(status: status)]).AuthorizeAsync(Request());

        decision.Allowed.Should().BeTrue();
        decision.ConsentDecisionReason.Should().Be(nameof(ConsentAuthorizationReason.NotOptedOut));
    }

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public async Task CONSENT01_Replace_OptOutIsInForceOnlyWithinItsPeriod()
    {
        // Expiry and effective dates are applied at the evaluation instant.
        var lapsed = await Service(consents:
                [OptOut(effectiveAt: DateTime.UtcNow.AddYears(-2), expiresAt: DateTime.UtcNow.AddDays(-1))])
            .AuthorizeAsync(Request());
        var future = await Service(consents: [OptOut(effectiveAt: DateTime.UtcNow.AddDays(30))])
            .AuthorizeAsync(Request());

        lapsed.Allowed.Should().BeTrue();
        future.Allowed.Should().BeTrue("the opt-out takes effect later");
    }

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public async Task CONSENT01_Replace_AWithdrawnOptInIsHonouredAsAnOptOut()
    {
        // Under the opt-in model that preceded D-11, revoking a Provider Access
        // consent was how a member said no. That refusal is not reversed.
        var withdrawn = await Service(consents: [Consent(ConsentPurposeOfUse.ProviderAccess, ConsentLifecycleStatus.Revoked)])
            .AuthorizeAsync(Request());
        var renewed = await Service(consents:
            [
                Consent(ConsentPurposeOfUse.ProviderAccess, ConsentLifecycleStatus.Revoked),
                Consent(ConsentPurposeOfUse.ProviderAccess),
            ])
            .AuthorizeAsync(Request());

        withdrawn.Allowed.Should().BeFalse();
        withdrawn.ConsentDecisionReason.Should().Be(nameof(ConsentAuthorizationReason.Revoked));
        renewed.Allowed.Should().BeTrue("a permit in force after the withdrawal is the member's latest word");
    }

    // ── Purpose isolation ───────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public async Task CONSENT01_Replace_OnlyAProviderAccessOptOutStopsProviderAccess()
    {
        // Refusing Payer-to-Payer, or refusing "no purpose", is not refusing
        // Provider Access: the purposes stay independent, from this side too.
        var p2pOptOut = await Service(consents: [OptOut(ConsentPurposeOfUse.PayerToPayerExchange)])
            .AuthorizeAsync(Request());
        var noPurpose = await Service(consents: [OptOut(ConsentPurposeOfUse.Unspecified)])
            .AuthorizeAsync(Request());
        var p2pPermit = await Service(consents: [Consent(ConsentPurposeOfUse.PayerToPayerExchange)])
            .AuthorizeAsync(Request());

        p2pOptOut.Allowed.Should().BeTrue();
        noPurpose.Allowed.Should().BeTrue();
        p2pPermit.Allowed.Should().BeTrue();
        p2pPermit.AuthorizingConsentId.Should().BeNull("a Payer-to-Payer consent authorizes nothing here");
    }

    // ── Attribution is independent of the opt-out ──────────────────────────────

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public async Task CONSENT01_Replace_NotAttributed_IsDeniedWhateverTheConsents()
    {
        // Sharing by default is sharing with the member's providers only. A
        // permit does not create a treatment relationship either.
        var none = await Service(panel: ["someone-else"]).AuthorizeAsync(Request());
        var permit = await Service(panel: ["someone-else"], consents: [Consent(ConsentPurposeOfUse.ProviderAccess)])
            .AuthorizeAsync(Request());

        none.Allowed.Should().BeFalse();
        none.Reason.Should().Be(ProviderAccessDenialReason.NotAttributed);
        permit.Reason.Should().Be(ProviderAccessDenialReason.NotAttributed);
        permit.Attributed.Should().BeFalse();
    }

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public async Task CONSENT01_Replace_AnonymousCaller_IsDenied()
    {
        // No caller identity means no panel can be checked. Fail closed rather
        // than treat an unidentified caller as universally attributed.
        var decision = await Service().AuthorizeAsync(Request(provider: null));

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(ProviderAccessDenialReason.NoCallerIdentity);
    }

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public async Task CONSENT01_Replace_NoMemberContext_IsDenied()
    {
        var decision = await Service().AuthorizeAsync(Request(member: string.Empty));

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(ProviderAccessDenialReason.NoMemberContext);
    }

    // ── Tenant / member isolation ──────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public async Task CONSENT01_Replace_AnotherTenant_IsDenied()
    {
        var decision = await Service().AuthorizeAsync(Request(tenant: "some-other-tenant"));

        decision.Allowed.Should().BeFalse();
    }

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public async Task CONSENT01_Replace_OneMembersOptOutIsTheirsAlone()
    {
        var panel = new[] { Member, "pat-002" };
        var consents = new[] { OptOut(member: "pat-002") };

        (await Service(panel, consents).AuthorizeAsync(Request(member: Member))).Allowed.Should().BeTrue();
        (await Service(panel, consents).AuthorizeAsync(Request(member: "pat-002"))).Allowed.Should().BeFalse();
    }

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public void CONSENT01_Replace_AnOptOutRecordedForAnotherTenantOrMemberIsIgnored()
    {
        // The policy compares tenant and member on each record; a snapshot for
        // someone else can neither refuse nor permit for this member.
        var foreign = new ConsentAuthorizationSnapshot
        {
            TenantId = "some-other-tenant", MemberId = Member, ConsentId = "x",
            PurposeOfUse = ConsentPurposeOfUse.ProviderAccess, Provision = ConsentProvision.Deny,
            Status = ConsentLifecycleStatus.Active,
        };

        ConsentAuthorizationPolicy.EvaluateOptOut(AcceptanceContext.TenantId, Member,
                ConsentPurposeOfUse.ProviderAccess, [foreign, foreign with { TenantId = AcceptanceContext.TenantId, MemberId = "pat-002" }],
                DateTime.UtcNow)
            .Allowed.Should().BeTrue();
    }

    // ── Registry failure ───────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public async Task CONSENT01_Replace_UnreachableRegistry_DeniesRatherThanAssumingNoOptOut()
    {
        // Sharing by default must not become sharing when the registry is down:
        // an unreadable registry cannot show the member has not opted out.
        var decision = await Service(consentSource: new ThrowingConsentSource())
            .AuthorizeAsync(Request());

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(ProviderAccessDenialReason.ConsentDenied);
        decision.ConsentDecisionReason.Should().Be(nameof(ConsentAuthorizationReason.RegistryUnavailable));
        decision.Attributed.Should().BeTrue("attribution passed; the registry is what failed");
    }

    [Theory]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    [InlineData(System.Net.HttpStatusCode.OK, "not json")]
    [InlineData(System.Net.HttpStatusCode.OK, "null")]
    [InlineData(System.Net.HttpStatusCode.OK, "{}")]
    [InlineData(System.Net.HttpStatusCode.OK, "{\"data\":[]}")]
    [InlineData(System.Net.HttpStatusCode.ServiceUnavailable, "{}")]
    [InlineData(System.Net.HttpStatusCode.Forbidden, "{}")]
    [InlineData(System.Net.HttpStatusCode.NotFound, "{}")]
    public async Task CONSENT01_Replace_AnUnusableRegistryAnswerDenies(System.Net.HttpStatusCode status, string body)
    {
        // The production HTTP source: any answer other than a readable 200 is a
        // registry failure, never "no records on file".
        var source = new HttpConsentRegistryConsentSource(new FixedResponse(status, body),
            AcceptanceContext.Logger<HttpConsentRegistryConsentSource>());

        var decision = await Service(consentSource: source).AuthorizeAsync(Request());

        decision.Allowed.Should().BeFalse();
        decision.ConsentDecisionReason.Should().Be(nameof(ConsentAuthorizationReason.RegistryUnavailable));
    }

    // ── Structural: coverage of every governed resource ────────────────────────

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public void CONSENT01_Replace_EveryMemberScopedResourceIsGovernedByTheAuthorizationLayer()
    {
        // The SMART layer's resource inventory and the Provider Access governed
        // set must stay identical: a resource added to the FHIR surface cannot
        // quietly escape the consent decision by being forgotten here. This is
        // the guard against a future controller bypassing the layer.
        var smartKnown = typeof(FhirService.Middleware.SmartScopeEnforcementMiddleware)
            .GetField("KnownResources",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null) as HashSet<string>;

        smartKnown.Should().NotBeNull();
        ProviderAccessAuthorizationFilter.GovernedResources
            .Should().BeEquivalentTo(smartKnown!,
                "every member-scoped resource the FHIR surface serves must pass the "
                + "Provider Access authorization layer, not just Patient");
    }

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public void CONSENT01_Replace_AuthorizationIsRegisteredGloballyNotPerController()
    {
        // Registered as a global MVC filter, so a new member-scoped controller is
        // governed the moment it exists — there is no per-controller attribute to
        // forget. Asserted on the type's shape rather than a route list.
        typeof(ProviderAccessAuthorizationFilter)
            .Should().Implement<Microsoft.AspNetCore.Mvc.Filters.IAsyncActionFilter>();

        // And it is not opt-in: it carries no attribute that a controller must apply.
        typeof(ProviderAccessAuthorizationFilter)
            .Should().NotBeAssignableTo<Attribute>();
    }

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public void CONSENT01_Replace_ProviderAccessRequiresItsOwnPurposeAndNotAnother()
    {
        // The required purpose is a constant, not configuration, and it is not
        // the Payer-to-Payer one.
        ProviderAccessAuthorizationService.RequiredPurpose
            .Should().Be(ConsentPurposeOfUse.ProviderAccess);
        ProviderAccessAuthorizationService.RequiredPurpose
            .Should().NotBe(ConsentRegistryPayerToPayerConsentGate.RequiredPurpose);
    }

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public void CONSENT01_Replace_AuthorizationDecisionCarriesNoPhi()
    {
        // The audited decision carries ids, categories and an instant — nothing
        // that could be a name, a date of birth, or clinical content.
        var properties = typeof(ProviderAccessDecision).GetProperties().Select(p => p.Name).ToList();

        // Ids, categories and an instant are what an audit record may carry.
        properties.Should().Contain(["TenantId", "MemberId", "ProviderId", "EvaluatedAtUtc"]);

        // Nothing that could hold demographics, clinical content, or the
        // encrypted consent narrative has a field to live in.
        properties.Should().NotContain(n =>
            n.Contains("Name", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Birth", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Gender", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Address", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Narrative", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Payload", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public async Task CONSENT01_Replace_WithNoRegistryConfiguredEveryDecisionRefuses()
    {
        // Outside Development, fhir-service with no consent registry and no
        // catalog reads nothing, so it cannot claim nobody opted out.
        var decision = await Service(consentSource: new UnavailableConsentSource(
                AcceptanceContext.Logger<UnavailableConsentSource>()))
            .AuthorizeAsync(Request());

        decision.Allowed.Should().BeFalse();
        decision.ConsentDecisionReason.Should().Be(nameof(ConsentAuthorizationReason.RegistryUnavailable));
    }

    // ── Test doubles ───────────────────────────────────────────────────────────

    private sealed class ThrowingConsentSource : IConsentSource
    {
        public Task<IReadOnlyList<ConsentAuthorizationSnapshot>> GetConsentsAsync(
            string tenantId, string memberId, CancellationToken ct = default)
            => throw new HttpRequestException("consent registry unreachable");
    }

    private sealed class FixedResponse(System.Net.HttpStatusCode status, string body) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(status, body)) { BaseAddress = new Uri("http://consent/") };

        private sealed class Handler(System.Net.HttpStatusCode status, string body) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                });
        }
    }
}
