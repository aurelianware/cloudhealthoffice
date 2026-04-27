using System.Text.Json;
using System.Text.Json.Nodes;
using CloudHealthOffice.Infrastructure.Json;
using ProviderService.Models;
using ProviderService.Models.CredentialingPayloads;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Services;

/// <summary>
/// Pure-function coverage for <see cref="CredentialingProjector"/> across
/// the canonical event sequences. The projector is the single authority on
/// the events → CredentialingStatus mapping; these tests anchor that
/// mapping so future contributors can refactor with confidence.
/// </summary>
public class CredentialingProjectorTests
{
    private static readonly DateTimeOffset NowAsOf = DateTimeOffset.Parse("2026-04-27T12:00:00Z");
    private const string TenantId = "tenant-a";
    private const string ProviderId = "p-001";

    private readonly CredentialingProjector _projector = new();

    [Fact]
    public void Empty_chain_projects_unknown()
    {
        var result = _projector.Project(Array.Empty<CredentialingEvent>(), NowAsOf);
        result.Status.Should().Be(CredentialingStatus.Unknown);
        result.EventCount.Should().Be(0);
    }

    [Fact]
    public void Single_ApplicationSubmitted_projects_pending()
    {
        var events = new[]
        {
            App(1, NowAsOf.AddDays(-30)),
        };
        var result = _projector.Project(events, NowAsOf);
        result.Status.Should().Be(CredentialingStatus.Pending);
        result.CurrentApplicationEventId.Should().Be(events[0].EventId);
    }

    [Fact]
    public void ApplicationSubmitted_then_PSV_stays_pending()
    {
        var app = App(1, NowAsOf.AddDays(-30));
        var psv = Psv(2, app.EventId, NowAsOf.AddDays(-25));
        var result = _projector.Project(new[] { app, psv }, NowAsOf);
        result.Status.Should().Be(CredentialingStatus.Pending);
        result.CurrentApplicationEventId.Should().Be(app.EventId);
    }

    [Fact]
    public void ApplicationSubmitted_then_DecisionApproved_projects_approved_with_dates()
    {
        var app = App(1, NowAsOf.AddDays(-30));
        var decided = Decision(2, app.EventId, NowAsOf.AddDays(-1),
            CredentialingDecision.Approved,
            credentialingDate: NowAsOf.AddDays(-1).UtcDateTime,
            recredentialingDueDate: NowAsOf.AddYears(2).UtcDateTime,
            authority: DecisionAuthorityType.CredentialingCommittee);

        var result = _projector.Project(new[] { app, decided }, NowAsOf);
        result.Status.Should().Be(CredentialingStatus.Approved);
        result.CredentialingDate.Should().Be(NowAsOf.AddDays(-1).UtcDateTime);
        result.RecredentialingDueDate.Should().Be(NowAsOf.AddYears(2).UtcDateTime);
        result.CurrentApplicationEventId.Should().BeNull();
        result.LastDecisionAuthorityType.Should().Be(DecisionAuthorityType.CredentialingCommittee);
    }

    [Fact]
    public void ApplicationSubmitted_then_DecisionDenied_projects_denied()
    {
        var app = App(1, NowAsOf.AddDays(-30));
        var decided = Decision(2, app.EventId, NowAsOf.AddDays(-1),
            CredentialingDecision.Denied);
        var result = _projector.Project(new[] { app, decided }, NowAsOf);
        result.Status.Should().Be(CredentialingStatus.Denied);
        result.CredentialingDate.Should().BeNull();
    }

    [Fact]
    public void Approved_with_past_RecredDue_projects_expired()
    {
        var app = App(1, NowAsOf.AddYears(-3));
        var decided = Decision(2, app.EventId, NowAsOf.AddYears(-3),
            CredentialingDecision.Approved,
            credentialingDate: NowAsOf.AddYears(-3).UtcDateTime,
            recredentialingDueDate: NowAsOf.AddDays(-1).UtcDateTime);

        var result = _projector.Project(new[] { app, decided }, NowAsOf);
        result.Status.Should().Be(CredentialingStatus.Expired);
        result.CredentialingDate.Should().Be(NowAsOf.AddYears(-3).UtcDateTime);
    }

    [Fact]
    public void Approved_then_RecredentialingTriggered_projects_pending()
    {
        var app = App(1, NowAsOf.AddYears(-2));
        var decided = Decision(2, app.EventId, NowAsOf.AddYears(-2),
            CredentialingDecision.Approved,
            credentialingDate: NowAsOf.AddYears(-2).UtcDateTime,
            recredentialingDueDate: NowAsOf.AddYears(1).UtcDateTime);
        var trigger = RecredentialTrigger(3, NowAsOf, decided.EventId);

        var result = _projector.Project(new[] { app, decided, trigger }, NowAsOf);
        result.Status.Should().Be(CredentialingStatus.Pending);
        // No open application yet — trigger fired but submission hasn't.
        result.CurrentApplicationEventId.Should().BeNull();
    }

    [Fact]
    public void RecredentialTriggered_then_NewSubmission_then_NewApproval_projects_approved_with_new_dates()
    {
        var app1 = App(1, NowAsOf.AddYears(-3));
        var dec1 = Decision(2, app1.EventId, NowAsOf.AddYears(-3),
            CredentialingDecision.Approved,
            credentialingDate: NowAsOf.AddYears(-3).UtcDateTime,
            recredentialingDueDate: NowAsOf.AddYears(-1).UtcDateTime);
        var trigger = RecredentialTrigger(3, NowAsOf.AddDays(-30), dec1.EventId);
        var app2 = App(4, NowAsOf.AddDays(-25));
        var dec2 = Decision(5, app2.EventId, NowAsOf.AddDays(-1),
            CredentialingDecision.Approved,
            credentialingDate: NowAsOf.AddDays(-1).UtcDateTime,
            recredentialingDueDate: NowAsOf.AddYears(2).UtcDateTime);

        var result = _projector.Project(new[] { app1, dec1, trigger, app2, dec2 }, NowAsOf);
        result.Status.Should().Be(CredentialingStatus.Approved);
        result.CredentialingDate.Should().Be(NowAsOf.AddDays(-1).UtcDateTime);
        result.RecredentialingDueDate.Should().Be(NowAsOf.AddYears(2).UtcDateTime);
    }

    [Fact]
    public void Submission_then_Withdrawal_with_no_predecessor_reverts_to_unknown()
    {
        var app = App(1, NowAsOf.AddDays(-10));
        var withdraw = Withdraw(2, app.EventId, NowAsOf.AddDays(-1));
        var result = _projector.Project(new[] { app, withdraw }, NowAsOf);
        result.Status.Should().Be(CredentialingStatus.Unknown);
        result.CurrentApplicationEventId.Should().BeNull();
    }

    [Fact]
    public void Approved_then_RecredTrigger_then_NewSubmission_then_Withdrawal_reverts_to_predecessor_approval()
    {
        var app1 = App(1, NowAsOf.AddYears(-2));
        var dec1 = Decision(2, app1.EventId, NowAsOf.AddYears(-2),
            CredentialingDecision.Approved,
            credentialingDate: NowAsOf.AddYears(-2).UtcDateTime,
            recredentialingDueDate: NowAsOf.AddYears(1).UtcDateTime);
        var trigger = RecredentialTrigger(3, NowAsOf.AddDays(-10), dec1.EventId);
        var app2 = App(4, NowAsOf.AddDays(-9));
        var withdraw = Withdraw(5, app2.EventId, NowAsOf.AddDays(-1));

        var result = _projector.Project(new[] { app1, dec1, trigger, app2, withdraw }, NowAsOf);
        result.Status.Should().Be(CredentialingStatus.Approved);
        result.CredentialingDate.Should().Be(NowAsOf.AddYears(-2).UtcDateTime);
        result.RecredentialingDueDate.Should().Be(NowAsOf.AddYears(1).UtcDateTime);
    }

    [Fact]
    public void Synthesized_application_alone_does_not_appear_as_open_chain()
    {
        // Defense in depth: an orphaned synthesized application (which
        // should never happen — they're paired with a DecisionRecorded by
        // construction) must NOT present as Pending.
        var synthesized = App(1, NowAsOf.AddDays(-1), synthesized: true);
        var result = _projector.Project(new[] { synthesized }, NowAsOf);
        result.Status.Should().Be(CredentialingStatus.Unknown);
        result.CurrentApplicationEventId.Should().BeNull();
    }

    [Fact]
    public void Synthesized_application_paired_with_decision_projects_approved()
    {
        var synthesized = App(1, NowAsOf.AddDays(-1), synthesized: true);
        var decided = Decision(2, synthesized.EventId, NowAsOf.AddDays(-1),
            CredentialingDecision.Approved,
            credentialingDate: NowAsOf.AddDays(-1).UtcDateTime,
            recredentialingDueDate: NowAsOf.AddYears(2).UtcDateTime,
            authority: DecisionAuthorityType.DelegatedAuthority);
        var result = _projector.Project(new[] { synthesized, decided }, NowAsOf);
        result.Status.Should().Be(CredentialingStatus.Approved);
        result.LastDecisionAuthorityType.Should().Be(DecisionAuthorityType.DelegatedAuthority);
    }

    // ----- helpers ------------------------------------------------------

    private static CredentialingEvent App(int version, DateTimeOffset submittedAt, bool synthesized = false)
    {
        var payload = new ApplicationSubmittedPayload(
            SubmittedAt: submittedAt,
            ApplicationSource: synthesized ? "DelegatedAuthority" : "Manual",
            SupportingDocuments: null,
            SynthesizedForDelegatedAuthority: synthesized);
        return new CredentialingEvent
        {
            TenantId = TenantId,
            ProviderId = ProviderId,
            EventId = synthesized
                ? CredentialingEvent.BuildSynthesizedApplicationSubmittedEventId(ProviderId, $"decision-{version}")
                : CredentialingEvent.BuildApplicationSubmittedEventId(ProviderId, submittedAt),
            EventType = CredentialingEventType.ApplicationSubmitted,
            Version = version,
            OccurredAt = submittedAt.UtcDateTime,
            Payload = Serialize(payload),
        };
    }

    private static CredentialingEvent Psv(int version, string applicationEventId, DateTimeOffset verifiedAt)
    {
        var payload = new PrimarySourceVerificationPayload(
            VerifiedAt: verifiedAt,
            VerificationVendor: "CAQH",
            VerifiedItems: new[] { "License", "DEA" },
            Evidence: null);
        return new CredentialingEvent
        {
            TenantId = TenantId,
            ProviderId = ProviderId,
            EventId = CredentialingEvent.BuildPrimarySourceVerificationEventId(ProviderId, applicationEventId, verifiedAt),
            EventType = CredentialingEventType.PrimarySourceVerificationCompleted,
            Version = version,
            ApplicationEventId = applicationEventId,
            OccurredAt = verifiedAt.UtcDateTime,
            Payload = Serialize(payload),
        };
    }

    private static CredentialingEvent Decision(
        int version,
        string applicationEventId,
        DateTimeOffset decidedAt,
        CredentialingDecision decision,
        DateTime? credentialingDate = null,
        DateTime? recredentialingDueDate = null,
        DecisionAuthorityType authority = DecisionAuthorityType.CredentialingCommittee)
    {
        var payload = new DecisionRecordedPayload(
            Decision: decision,
            DecidedAt: decidedAt,
            CredentialingDate: credentialingDate,
            RecredentialingDueDate: recredentialingDueDate,
            DecisionAuthorityType: authority,
            DecisionAuthorityId: "actor-1",
            CommitteeMembers: authority == DecisionAuthorityType.CredentialingCommittee ? new[] { "m1", "m2" } : null,
            DecisionMinuteReference: authority == DecisionAuthorityType.CredentialingCommittee ? "minutes/abc" : null,
            DenialReason: decision == CredentialingDecision.Denied ? "test" : null);
        return new CredentialingEvent
        {
            TenantId = TenantId,
            ProviderId = ProviderId,
            EventId = CredentialingEvent.BuildDecisionRecordedEventId(ProviderId, applicationEventId, decidedAt),
            EventType = CredentialingEventType.DecisionRecorded,
            Version = version,
            ApplicationEventId = applicationEventId,
            OccurredAt = decidedAt.UtcDateTime,
            Payload = Serialize(payload),
        };
    }

    private static CredentialingEvent RecredentialTrigger(int version, DateTimeOffset triggeredAt, string predecessor)
    {
        var payload = new RecredentialingTriggeredPayload(triggeredAt, "DueDateElapsed");
        return new CredentialingEvent
        {
            TenantId = TenantId,
            ProviderId = ProviderId,
            EventId = CredentialingEvent.BuildRecredentialingTriggeredEventId(ProviderId, triggeredAt),
            EventType = CredentialingEventType.RecredentialingTriggered,
            Version = version,
            PredecessorEventId = predecessor,
            OccurredAt = triggeredAt.UtcDateTime,
            Payload = Serialize(payload),
        };
    }

    private static CredentialingEvent Withdraw(int version, string applicationEventId, DateTimeOffset withdrawnAt)
    {
        var payload = new ApplicationWithdrawnPayload(withdrawnAt, "withdrew");
        return new CredentialingEvent
        {
            TenantId = TenantId,
            ProviderId = ProviderId,
            EventId = CredentialingEvent.BuildApplicationWithdrawnEventId(ProviderId, applicationEventId, withdrawnAt),
            EventType = CredentialingEventType.ApplicationWithdrawn,
            Version = version,
            ApplicationEventId = applicationEventId,
            OccurredAt = withdrawnAt.UtcDateTime,
            Payload = Serialize(payload),
        };
    }

    private static JsonObject? Serialize<T>(T payload) where T : class
    {
        var json = JsonSerializer.Serialize(payload, CloudHealthOfficeJsonOptions.DefaultOptions);
        return JsonNode.Parse(json) as JsonObject;
    }
}
