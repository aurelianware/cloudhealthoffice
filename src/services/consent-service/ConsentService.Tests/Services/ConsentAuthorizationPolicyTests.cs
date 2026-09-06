using CloudHealthOffice.Consent.Contracts;
using ConsentService.Models;
using FluentAssertions;
using Xunit;

namespace ConsentService.Tests.Services;

/// <summary>
/// The authorization policy is the single answer to "has this member authorized
/// this purpose?" — the inbound and outbound Payer-to-Payer paths both evaluate
/// it, so a gap here is a gap in both directions at once. Every rule is
/// fail-closed and every rule is pinned below.
/// </summary>
public class ConsentAuthorizationPolicyTests
{
    private const string Tenant = "demo-tenant";
    private const string Member = "pat-001";
    private static readonly DateTime Now = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

    private static ConsentAuthorizationSnapshot Snapshot(
        ConsentPurposeOfUse purpose = ConsentPurposeOfUse.PayerToPayerExchange,
        ConsentLifecycleStatus status = ConsentLifecycleStatus.Active,
        string tenant = Tenant,
        string member = Member,
        DateTime? effectiveAt = null,
        DateTime? expiresAt = null,
        string consentId = "consent-1",
        long? version = null) => new()
    {
        TenantId = tenant,
        MemberId = member,
        ConsentId = consentId,
        PurposeOfUse = purpose,
        Status = status,
        EffectiveAt = effectiveAt,
        ExpiresAt = expiresAt,
        Version = version,
    };

    private static ConsentDecision Evaluate(
        params ConsentAuthorizationSnapshot[] consents)
        => ConsentAuthorizationPolicy.Evaluate(
            Tenant, Member, ConsentPurposeOfUse.PayerToPayerExchange, consents, Now);

    [Fact]
    public void AnActiveConsentForThePurpose_Authorizes()
    {
        var decision = Evaluate(Snapshot());

        decision.Allowed.Should().BeTrue();
        decision.Reason.Should().Be(ConsentAuthorizationReason.Granted);
        decision.ConsentId.Should().Be("consent-1", "the decision names its evidence");
        decision.PurposeOfUse.Should().Be(ConsentPurposeOfUse.PayerToPayerExchange);
        decision.EvaluatedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void NoConsentAtAll_Denies()
        => Evaluate().Reason.Should().Be(ConsentAuthorizationReason.NoConsentOnRecord);

    [Theory]
    // The separation this whole model exists for: another purpose is not this one.
    [InlineData(ConsentPurposeOfUse.ProviderAccess)]
    // And a record written before the purpose axis existed authorizes nothing.
    [InlineData(ConsentPurposeOfUse.Unspecified)]
    public void AConsentForAnotherPurpose_Denies(ConsentPurposeOfUse purpose)
    {
        var decision = Evaluate(Snapshot(purpose));

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(ConsentAuthorizationReason.NoConsentForPurpose);
    }

    [Theory]
    [InlineData(ConsentLifecycleStatus.Draft, ConsentAuthorizationReason.NotActivated)]
    [InlineData(ConsentLifecycleStatus.Revoked, ConsentAuthorizationReason.Revoked)]
    [InlineData(ConsentLifecycleStatus.Expired, ConsentAuthorizationReason.Expired)]
    public void AConsentNotInForce_DeniesWithItsOwnReason(
        ConsentLifecycleStatus status, ConsentAuthorizationReason expected)
    {
        var decision = Evaluate(Snapshot(status: status));

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(expected);
        decision.ConsentId.Should().Be("consent-1", "the refusal names the record it read");
    }

    [Fact]
    public void AnActiveConsentPastItsExpiry_Denies()
    {
        // The period is applied here rather than trusted from the stored status,
        // so a record that lapsed since it was last written cannot authorize.
        var decision = Evaluate(Snapshot(expiresAt: Now.AddMinutes(-1)));

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(ConsentAuthorizationReason.Expired);
    }

    [Fact]
    public void AnActiveConsentThatHasNotStarted_Denies()
    {
        var decision = Evaluate(Snapshot(effectiveAt: Now.AddDays(1)));

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(ConsentAuthorizationReason.NotYetEffective);
    }

    [Fact]
    public void AConsentInForceRightNow_Authorizes()
        => Evaluate(Snapshot(effectiveAt: Now.AddDays(-1), expiresAt: Now.AddDays(1)))
            .Allowed.Should().BeTrue();

    [Theory]
    [InlineData("other-tenant", Member)]
    [InlineData(Tenant, "pat-002")]
    public void AConsentBelongingToSomeoneElse_IsNotEvidenceAboutThisMember(string tenant, string member)
    {
        // Even if a source hands it over, the policy re-checks whose consent it
        // is — tenant isolation is not delegated to the caller's query.
        var decision = Evaluate(Snapshot(tenant: tenant, member: member));

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(ConsentAuthorizationReason.NoConsentOnRecord);
    }

    [Fact]
    public void ARevokedConsentIsNotRescuedByAnotherPurposeStillBeingActive()
    {
        var decision = Evaluate(
            Snapshot(ConsentPurposeOfUse.PayerToPayerExchange, ConsentLifecycleStatus.Revoked),
            Snapshot(ConsentPurposeOfUse.ProviderAccess, consentId: "consent-provider"));

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(ConsentAuthorizationReason.Revoked);
    }

    [Fact]
    public void AmongSeveralInForceConsents_TheLongestRunningWins()
    {
        // Deterministic choice, so the decision names the authorization that
        // actually covers the operation rather than whichever came back first.
        var decision = Evaluate(
            Snapshot(expiresAt: Now.AddDays(1), consentId: "expires-soon"),
            Snapshot(expiresAt: null, consentId: "unbounded"));

        decision.Allowed.Should().BeTrue();
        decision.ConsentId.Should().Be("unbounded");
    }

    [Fact]
    public void ARegrantAfterRevocation_Authorizes()
    {
        // Lifecycle: the member said no, then yes. The active record governs.
        var decision = Evaluate(
            Snapshot(status: ConsentLifecycleStatus.Revoked, consentId: "old"),
            Snapshot(status: ConsentLifecycleStatus.Active, consentId: "new", version: 2));

        decision.Allowed.Should().BeTrue();
        decision.ConsentId.Should().Be("new");
    }

    [Fact]
    public void AskingAboutNoPurposeAtAll_IsAlwaysDenied()
    {
        var decision = ConsentAuthorizationPolicy.Evaluate(
            Tenant, Member, ConsentPurposeOfUse.Unspecified,
            [Snapshot(ConsentPurposeOfUse.Unspecified)], Now);

        decision.Allowed.Should().BeFalse();
    }

    // ── Registry projection ─────────────────────────────────────────────────────

    [Fact]
    public void TheRegistryProjectsOnlyAuthorizationFields_NoNarrative()
    {
        var consent = new Consent
        {
            TenantId = Tenant,
            MemberId = Member,
            ConsentType = ConsentType.GeneralAuthorization,
            PurposeOfUse = ConsentPurposeOfUse.PayerToPayerExchange,
            Status = ConsentStatus.Active,
            EffectiveAt = Now.AddDays(-1),
            ExpiresAt = Now.AddYears(1),
            GrantedBy = "member",
            Reason = "narrative the member wrote",
            GrantedToName = "a named party",
            Purpose = "free text purpose",
        };

        var snapshot = consent.ToAuthorizationSnapshot();

        snapshot.TenantId.Should().Be(Tenant);
        snapshot.MemberId.Should().Be(Member);
        snapshot.ConsentId.Should().Be(consent.Id);
        snapshot.PurposeOfUse.Should().Be(ConsentPurposeOfUse.PayerToPayerExchange);
        snapshot.Status.Should().Be(ConsentLifecycleStatus.Active);

        // The narrative fields have no home on the snapshot at all — the
        // authorization projection cannot carry them across a service boundary.
        typeof(ConsentAuthorizationSnapshot).GetProperties().Select(p => p.Name)
            .Should().NotContain(["Reason", "GrantedToName", "GrantedToContact", "Purpose", "GrantedBy"]);
    }

    [Fact]
    public void ANewConsentDefaultsToNoPurpose_SoNothingIsGrantedByAccident()
        => new Consent().PurposeOfUse.Should().Be(ConsentPurposeOfUse.Unspecified);

    [Theory]
    [InlineData(ConsentStatus.Draft, ConsentLifecycleStatus.Draft)]
    [InlineData(ConsentStatus.Active, ConsentLifecycleStatus.Active)]
    [InlineData(ConsentStatus.Revoked, ConsentLifecycleStatus.Revoked)]
    [InlineData(ConsentStatus.Expired, ConsentLifecycleStatus.Expired)]
    public void TheContractStatusMirrorsTheRegistryStatus(
        ConsentStatus registry, ConsentLifecycleStatus contract)
    {
        // Drift guard: the projection casts by value, so a renumbering on either
        // side would silently change what "Revoked" means to the service
        // enforcing it.
        ((int)registry).Should().Be((int)contract);
        new Consent { Status = registry }.ToAuthorizationSnapshot().Status.Should().Be(contract);
    }
}
