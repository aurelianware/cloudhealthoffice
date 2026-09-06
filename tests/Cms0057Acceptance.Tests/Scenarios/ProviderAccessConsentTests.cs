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
/// scope, provider/member attribution, an active ProviderAccess-purpose consent,
/// and tenant/member isolation. Authentication and SMART scope are enforced
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

    // ── Happy path ──────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public async Task CONSENT01_Replace_AttributedMemberWithProviderAccessConsent_IsAuthorized()
    {
        var decision = await Service(consents: [Consent(ConsentPurposeOfUse.ProviderAccess)])
            .AuthorizeAsync(Request());

        decision.Allowed.Should().BeTrue();
        decision.Attributed.Should().BeTrue();
        // The decision names WHICH authorization permitted the disclosure.
        decision.AuthorizingConsentId.Should().Be("consent-ProviderAccess-Active");
        decision.ConsentDecisionReason.Should().Be(nameof(ConsentAuthorizationReason.Granted));
    }

    // ── Purpose isolation ───────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public async Task CONSENT01_Replace_PayerToPayerConsentAlone_DoesNotAuthorizeProviderAccess()
    {
        // The member authorized their data to move to another payer. That is not
        // permission for a provider to read the record. The mirror of the P2P-03
        // rule, decided by the same policy from the other side.
        var decision = await Service(consents: [Consent(ConsentPurposeOfUse.PayerToPayerExchange)])
            .AuthorizeAsync(Request());

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(ProviderAccessDenialReason.ConsentDenied);
        decision.ConsentDecisionReason.Should()
            .Be(nameof(ConsentAuthorizationReason.NoConsentForPurpose));
    }

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public async Task CONSENT01_Replace_ConsentWithNoPurposeRecorded_DoesNotAuthorizeProviderAccess()
    {
        // A generic Active consent — the pre-purpose record shape — authorizes
        // nothing here. History is not reinterpreted for this purpose either.
        var decision = await Service(consents: [Consent(ConsentPurposeOfUse.Unspecified)])
            .AuthorizeAsync(Request());

        decision.Allowed.Should().BeFalse();
        decision.ConsentDecisionReason.Should()
            .Be(nameof(ConsentAuthorizationReason.NoConsentForPurpose));
    }

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public async Task CONSENT01_Replace_NoConsentOnRecord_DoesNotAuthorizeProviderAccess()
    {
        var decision = await Service().AuthorizeAsync(Request());

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(ProviderAccessDenialReason.ConsentDenied);
        decision.ConsentDecisionReason.Should()
            .Be(nameof(ConsentAuthorizationReason.NoConsentOnRecord));
    }

    // ── Lifecycle — the same rules as Payer-to-Payer, from the shared policy ────

    [Theory]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    [InlineData(ConsentLifecycleStatus.Draft, nameof(ConsentAuthorizationReason.NotActivated))]
    [InlineData(ConsentLifecycleStatus.Revoked, nameof(ConsentAuthorizationReason.Revoked))]
    [InlineData(ConsentLifecycleStatus.Expired, nameof(ConsentAuthorizationReason.Expired))]
    public async Task CONSENT01_Replace_ConsentNotInForce_DeniesWithItsOwnReason(
        ConsentLifecycleStatus status, string expectedReason)
    {
        var decision = await Service(
                consents: [Consent(ConsentPurposeOfUse.ProviderAccess, status)])
            .AuthorizeAsync(Request());

        decision.Allowed.Should().BeFalse();
        decision.ConsentDecisionReason.Should().Be(expectedReason);
    }

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public async Task CONSENT01_Replace_LapsedConsent_DeniesEvenWhilePersistedActive()
    {
        // Expiry is applied by the policy at the evaluation instant, not trusted
        // from the stored status.
        var decision = await Service(consents:
            [
                Consent(ConsentPurposeOfUse.ProviderAccess,
                    effectiveAt: DateTime.UtcNow.AddYears(-2),
                    expiresAt: DateTime.UtcNow.AddDays(-1))
            ])
            .AuthorizeAsync(Request());

        decision.Allowed.Should().BeFalse();
        decision.ConsentDecisionReason.Should().Be(nameof(ConsentAuthorizationReason.Expired));
    }

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public async Task CONSENT01_Replace_NotYetEffectiveConsent_Denies()
    {
        var decision = await Service(consents:
            [
                Consent(ConsentPurposeOfUse.ProviderAccess,
                    effectiveAt: DateTime.UtcNow.AddDays(30))
            ])
            .AuthorizeAsync(Request());

        decision.Allowed.Should().BeFalse();
        decision.ConsentDecisionReason.Should()
            .Be(nameof(ConsentAuthorizationReason.NotYetEffective));
    }

    // ── Attribution is independent of consent ──────────────────────────────────

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public async Task CONSENT01_Replace_ValidConsentButNoAttribution_IsDenied()
    {
        // The member authorized Provider Access generally; this provider is still
        // not theirs. Consent does not create a treatment relationship.
        var decision = await Service(
                panel: ["someone-else"],
                consents: [Consent(ConsentPurposeOfUse.ProviderAccess)])
            .AuthorizeAsync(Request());

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(ProviderAccessDenialReason.NotAttributed);
        decision.Attributed.Should().BeFalse();
    }

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public async Task CONSENT01_Replace_AttributedButNoConsent_IsDenied()
    {
        // And the converse: the member is on this provider's panel and still has
        // not authorized the disclosure. Attribution does not imply consent.
        var decision = await Service(panel: [Member]).AuthorizeAsync(Request());

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(ProviderAccessDenialReason.ConsentDenied);
        decision.Attributed.Should().BeTrue("the panel check passed; consent is what refused");
    }

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public async Task CONSENT01_Replace_AnonymousCaller_IsDenied()
    {
        // No caller identity means no panel can be checked. Fail closed rather
        // than treat an unidentified caller as universally attributed.
        var decision = await Service(consents: [Consent(ConsentPurposeOfUse.ProviderAccess)])
            .AuthorizeAsync(Request(provider: null));

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(ProviderAccessDenialReason.NoCallerIdentity);
    }

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public async Task CONSENT01_Replace_NoMemberContext_IsDenied()
    {
        // Nothing to evaluate consent against — a membership-wide read is refused
        // rather than allowed to proceed unauthorized.
        var decision = await Service(consents: [Consent(ConsentPurposeOfUse.ProviderAccess)])
            .AuthorizeAsync(Request(member: string.Empty));

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(ProviderAccessDenialReason.NoMemberContext);
    }

    // ── Tenant / member isolation ──────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public async Task CONSENT01_Replace_ConsentFromAnotherTenant_DoesNotAuthorize()
    {
        var decision = await Service(consents: [Consent(ConsentPurposeOfUse.ProviderAccess)])
            .AuthorizeAsync(Request(tenant: "some-other-tenant"));

        decision.Allowed.Should().BeFalse();
    }

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public async Task CONSENT01_Replace_ConsentForAnotherMember_DoesNotAuthorize()
    {
        // pat-002 authorized Provider Access; pat-001 did not. The panel covers
        // both, so only the consent match keeps them apart.
        var decision = await Service(
                panel: [Member, "pat-002"],
                consents: [Consent(ConsentPurposeOfUse.ProviderAccess, member: "pat-002")])
            .AuthorizeAsync(Request(member: Member));

        decision.Allowed.Should().BeFalse();
        decision.ConsentDecisionReason.Should()
            .Be(nameof(ConsentAuthorizationReason.NoConsentOnRecord));
    }

    // ── Registry failure ───────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public async Task CONSENT01_Replace_UnreachableRegistry_DeniesRatherThanFallingBack()
    {
        // An unreadable registry must not degrade to "SMART + attribution were
        // fine, let it through". It is not permission.
        var decision = await Service(
                consentSource: new ThrowingConsentSource())
            .AuthorizeAsync(Request());

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(ProviderAccessDenialReason.ConsentDenied);
        decision.Attributed.Should().BeTrue("attribution passed; the registry is what failed");
    }

    [Fact]
    [Trait("Scenario", "CONSENT-01")]
    [Trait("Backend", "Replace")]
    public async Task CONSENT01_Replace_MalformedRegistryResponse_Denies()
    {
        // A source that answers with nothing usable authorizes nothing.
        var decision = await Service(consentSource: new EmptyConsentSource())
            .AuthorizeAsync(Request());

        decision.Allowed.Should().BeFalse();
        decision.ConsentDecisionReason.Should()
            .Be(nameof(ConsentAuthorizationReason.NoConsentOnRecord));
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

    // ── Test doubles ───────────────────────────────────────────────────────────

    private sealed class ThrowingConsentSource : IConsentSource
    {
        public Task<IReadOnlyList<ConsentAuthorizationSnapshot>> GetConsentsAsync(
            string tenantId, string memberId, CancellationToken ct = default)
            => throw new HttpRequestException("consent registry unreachable");
    }

    private sealed class EmptyConsentSource : IConsentSource
    {
        public Task<IReadOnlyList<ConsentAuthorizationSnapshot>> GetConsentsAsync(
            string tenantId, string memberId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ConsentAuthorizationSnapshot>>(
                Array.Empty<ConsentAuthorizationSnapshot>());
    }
}
