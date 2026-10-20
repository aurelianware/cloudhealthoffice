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

    private static ConsentAuthorizationSnapshot OptOut(
        ConsentPurposeOfUse purpose = ConsentPurposeOfUse.ProviderAccess,
        ConsentLifecycleStatus status = ConsentLifecycleStatus.Active,
        DateTime? effectiveAt = null,
        DateTime? expiresAt = null,
        string consentId = "opt-out-1") =>
        Snapshot(purpose, status, effectiveAt: effectiveAt, expiresAt: expiresAt, consentId: consentId)
            with { Provision = ConsentProvision.Deny };

    private static ConsentDecision EvaluateOptOut(params ConsentAuthorizationSnapshot[] consents)
        => ConsentAuthorizationPolicy.EvaluateOptOut(
            Tenant, Member, ConsentPurposeOfUse.ProviderAccess, consents, Now);

    // ── Opt-in purposes and opt-out records ────────────────────────────────────

    [Fact]
    public void An_opt_out_never_authorizes_an_opt_in_purpose_and_wins_over_a_permit()
    {
        var alone = Evaluate(OptOut(ConsentPurposeOfUse.PayerToPayerExchange));
        var withPermit = Evaluate(Snapshot(), OptOut(ConsentPurposeOfUse.PayerToPayerExchange));

        alone.Allowed.Should().BeFalse();
        withPermit.Allowed.Should().BeFalse();
        withPermit.Reason.Should().Be(ConsentAuthorizationReason.OptedOut);
        withPermit.ConsentId.Should().Be("opt-out-1");
    }

    [Fact]
    public void A_lapsed_opt_out_leaves_an_opt_in_permit_in_force()
        => Evaluate(Snapshot(), OptOut(ConsentPurposeOfUse.PayerToPayerExchange, ConsentLifecycleStatus.Revoked))
            .Allowed.Should().BeTrue();

    // ── Opt-out purposes ───────────────────────────────────────────────────────

    [Fact]
    public void Opt_out_with_nothing_on_record_allows_and_names_no_consent()
    {
        var decision = EvaluateOptOut();

        decision.Allowed.Should().BeTrue();
        decision.Reason.Should().Be(ConsentAuthorizationReason.NotOptedOut);
        decision.ConsentId.Should().BeNull();
    }

    [Fact]
    public void Opt_out_in_force_denies_and_wins_over_a_permit()
    {
        var decision = EvaluateOptOut(Snapshot(ConsentPurposeOfUse.ProviderAccess, consentId: "permit"), OptOut());

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(ConsentAuthorizationReason.OptedOut);
        decision.ConsentId.Should().Be("opt-out-1");
    }

    [Theory]
    [InlineData(ConsentLifecycleStatus.Draft)]
    [InlineData(ConsentLifecycleStatus.Revoked)]
    [InlineData(ConsentLifecycleStatus.Expired)]
    public void Opt_out_not_active_does_not_deny(ConsentLifecycleStatus status)
        => EvaluateOptOut(OptOut(status: status)).Allowed.Should().BeTrue();

    [Fact]
    public void Opt_out_applies_only_within_its_period()
    {
        EvaluateOptOut(OptOut(expiresAt: Now)).Allowed.Should().BeTrue("expiry is exclusive of the instant");
        EvaluateOptOut(OptOut(effectiveAt: Now.AddSeconds(1))).Allowed.Should().BeTrue();
        EvaluateOptOut(OptOut(effectiveAt: Now)).Allowed.Should().BeFalse();
    }

    [Fact]
    public void A_revoked_permit_is_a_refusal_until_a_permit_is_in_force_again()
    {
        var revoked = Snapshot(ConsentPurposeOfUse.ProviderAccess, ConsentLifecycleStatus.Revoked, consentId: "old");

        EvaluateOptOut(revoked).Reason.Should().Be(ConsentAuthorizationReason.Revoked);
        EvaluateOptOut(revoked, Snapshot(ConsentPurposeOfUse.ProviderAccess, consentId: "new"))
            .Reason.Should().Be(ConsentAuthorizationReason.Granted);
    }

    [Fact]
    public void Opt_out_of_another_purpose_tenant_or_member_is_ignored()
    {
        EvaluateOptOut(
                OptOut(ConsentPurposeOfUse.PayerToPayerExchange),
                OptOut(ConsentPurposeOfUse.Unspecified),
                OptOut() with { TenantId = "other" },
                OptOut() with { MemberId = "other" })
            .Allowed.Should().BeTrue();
    }

    [Fact]
    public void Opt_out_of_no_purpose_or_without_tenant_or_member_denies()
    {
        ConsentAuthorizationPolicy.EvaluateOptOut(Tenant, Member, ConsentPurposeOfUse.Unspecified, [], Now)
            .Allowed.Should().BeFalse();
        ConsentAuthorizationPolicy.EvaluateOptOut("", Member, ConsentPurposeOfUse.ProviderAccess, [], Now)
            .Allowed.Should().BeFalse();
        ConsentAuthorizationPolicy.EvaluateOptOut(Tenant, " ", ConsentPurposeOfUse.ProviderAccess, [], Now)
            .Allowed.Should().BeFalse();
    }

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
    // ── Deterministic selection under ties (Copilot review, PR #1152) ──────────

    [Fact]
    public void Grant_WhenExpiryAndVersionTie_NamesTheSameConsentWhateverTheSourceOrder()
    {
        // Two equally valid consents: same purpose, same status, same unbounded
        // expiry, same version. Before the consent-id tie-break the winner was
        // whichever the source happened to return first, so the SAME registry
        // state could authorize under a different consent on a second read.
        var a = Snapshot(consentId: "consent-aaa");
        var b = Snapshot(consentId: "consent-bbb");

        var forward = ConsentAuthorizationPolicy.Evaluate(
            Tenant, Member, ConsentPurposeOfUse.PayerToPayerExchange, new[] { a, b }, Now);
        var reversed = ConsentAuthorizationPolicy.Evaluate(
            Tenant, Member, ConsentPurposeOfUse.PayerToPayerExchange, new[] { b, a }, Now);

        forward.Allowed.Should().BeTrue();
        reversed.Allowed.Should().BeTrue();
        forward.ConsentId.Should().Be(reversed.ConsentId);
        forward.ConsentId.Should().Be("consent-aaa");
    }

    [Fact]
    public void Deny_WhenSeveralRecordsShareANearMiss_NamesTheSameOneWhateverTheSourceOrder()
    {
        // The refusal path had the same ordering dependence: which revoked
        // consent an audit entry blamed varied with the source's order.
        var a = Snapshot(consentId: "consent-aaa", status: ConsentLifecycleStatus.Revoked);
        var b = Snapshot(consentId: "consent-bbb", status: ConsentLifecycleStatus.Revoked);

        var forward = ConsentAuthorizationPolicy.Evaluate(
            Tenant, Member, ConsentPurposeOfUse.PayerToPayerExchange, new[] { a, b }, Now);
        var reversed = ConsentAuthorizationPolicy.Evaluate(
            Tenant, Member, ConsentPurposeOfUse.PayerToPayerExchange, new[] { b, a }, Now);

        forward.Allowed.Should().BeFalse();
        forward.Reason.Should().Be(ConsentAuthorizationReason.Revoked);
        forward.ConsentId.Should().Be(reversed.ConsentId);
        forward.ConsentId.Should().Be("consent-aaa");
    }

    [Fact]
    public void Grant_ExpiryStillOutranksTheConsentIdTieBreak()
    {
        // The id is the LAST tie-break, not a new precedence: a later-expiring
        // consent still wins over an alphabetically earlier one.
        var earlyIdShortLived = Snapshot(consentId: "consent-aaa", expiresAt: Now.AddDays(1));
        var lateIdLongLived = Snapshot(consentId: "consent-zzz", expiresAt: Now.AddDays(30));

        var decision = ConsentAuthorizationPolicy.Evaluate(
            Tenant, Member, ConsentPurposeOfUse.PayerToPayerExchange,
            new[] { earlyIdShortLived, lateIdLongLived }, Now);

        decision.ConsentId.Should().Be("consent-zzz");
    }

}
