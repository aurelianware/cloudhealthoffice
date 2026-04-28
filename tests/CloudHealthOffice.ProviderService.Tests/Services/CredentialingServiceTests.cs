using Microsoft.Extensions.Logging.Abstractions;
using ProviderService.Models;
using ProviderService.Models.CredentialingPayloads;
using ProviderService.Services;
using CloudHealthOffice.ProviderService.Tests.Fakes;

namespace CloudHealthOffice.ProviderService.Tests.Services;

/// <summary>
/// Orchestration coverage for <see cref="CredentialingService"/>: each
/// workflow method validates the chain pre-state, publishes a typed
/// event, and (for status-changing events) patches the flat-field
/// projection on Provider.
/// </summary>
public class CredentialingServiceTests
{
    private const string TenantId = "tenant-a";
    private const string ProviderId = "p-001";

    private readonly InMemoryCredentialingEventRepository _eventRepository = new();
    private readonly FakeCredentialingEventPublisher _publisher;
    private readonly InMemoryProviderRepository _providerRepository;
    private readonly CredentialingService _service;

    public CredentialingServiceTests()
    {
        _publisher = new FakeCredentialingEventPublisher(_eventRepository);
        _providerRepository = new InMemoryProviderRepository { TenantId = TenantId };
        SeedActiveProvider();
        _service = new CredentialingService(
            _eventRepository,
            _publisher,
            _providerRepository,
            new CredentialingProjector(),
            NullLogger<CredentialingService>.Instance);
    }

    [Fact]
    public async Task SubmitApplicationAsync_appends_event_and_patches_projection_to_pending()
    {
        var submittedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var evt = await _service.SubmitApplicationAsync(
            TenantId, ProviderId,
            new SubmitApplicationRequest { SubmittedAt = submittedAt, ApplicationSource = "Manual" },
            actorId: "actor-1", correlationId: null);

        evt.EventType.Should().Be(CredentialingEventType.ApplicationSubmitted);
        evt.Version.Should().Be(1);
        _providerRepository.CredentialingProjectionPatches.Should().HaveCount(1);
        _providerRepository.CredentialingProjectionPatches[0].Status.Should().Be(CredentialingStatus.Pending);
    }

    [Fact]
    public async Task SubmitApplicationAsync_with_open_application_throws()
    {
        await _service.SubmitApplicationAsync(TenantId, ProviderId,
            new SubmitApplicationRequest { ApplicationSource = "Manual" }, "actor-1", null);

        var act = () => _service.SubmitApplicationAsync(TenantId, ProviderId,
            new SubmitApplicationRequest
            {
                ApplicationSource = "Manual",
                SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(1),
            },
            "actor-1", null);
        await act.Should().ThrowAsync<CredentialingValidationException>();
    }

    [Fact]
    public async Task RecordPrimarySourceVerificationAsync_requires_open_application()
    {
        var act = () => _service.RecordPrimarySourceVerificationAsync(
            TenantId, ProviderId,
            new RecordPrimarySourceVerificationRequest
            {
                VerificationVendor = "CAQH",
                VerifiedItems = new[] { "License" },
            },
            "actor-1", null);
        await act.Should().ThrowAsync<CredentialingValidationException>();
    }

    [Fact]
    public async Task RecordPrimarySourceVerificationAsync_does_not_patch_projection()
    {
        await _service.SubmitApplicationAsync(TenantId, ProviderId,
            new SubmitApplicationRequest { ApplicationSource = "Manual" }, "actor-1", null);
        var patchesBefore = _providerRepository.CredentialingProjectionPatches.Count;

        await _service.RecordPrimarySourceVerificationAsync(TenantId, ProviderId,
            new RecordPrimarySourceVerificationRequest
            {
                VerificationVendor = "CAQH",
                VerifiedItems = new[] { "License", "DEA" },
                VerifiedAt = DateTimeOffset.UtcNow.AddMinutes(1),
            },
            "actor-1", null);

        _providerRepository.CredentialingProjectionPatches.Should().HaveCount(patchesBefore);
    }

    [Fact]
    public async Task RecordDecisionAsync_requires_open_application_when_authority_is_committee()
    {
        var act = () => _service.RecordDecisionAsync(TenantId, ProviderId,
            new RecordDecisionRequest
            {
                Decision = CredentialingDecision.Approved,
                DecisionAuthorityType = DecisionAuthorityType.CredentialingCommittee,
                DecisionAuthorityId = "committee-x",
                CommitteeMembers = new[] { "m1", "m2" },
                DecisionMinuteReference = "minutes/abc",
                CredentialingDate = DateTime.UtcNow,
                RecredentialingDueDate = DateTime.UtcNow.AddYears(2),
            },
            "actor-1", null);
        await act.Should().ThrowAsync<CredentialingValidationException>()
            .Where(ex => ex.Message.Contains("no open credentialing application"));
    }

    [Fact]
    public async Task RecordDecisionAsync_committee_path_requires_CommitteeMembers()
    {
        await _service.SubmitApplicationAsync(TenantId, ProviderId,
            new SubmitApplicationRequest { ApplicationSource = "Manual" }, "actor-1", null);

        var act = () => _service.RecordDecisionAsync(TenantId, ProviderId,
            new RecordDecisionRequest
            {
                Decision = CredentialingDecision.Approved,
                DecisionAuthorityType = DecisionAuthorityType.CredentialingCommittee,
                DecisionAuthorityId = "committee-x",
                DecisionMinuteReference = "minutes/abc",
                CredentialingDate = DateTime.UtcNow,
                RecredentialingDueDate = DateTime.UtcNow.AddYears(2),
            },
            "actor-1", null);
        await act.Should().ThrowAsync<CredentialingValidationException>()
            .Where(ex => ex.Message.Contains("CommitteeMembers"));
    }

    [Fact]
    public async Task RecordDecisionAsync_committee_path_requires_DecisionMinuteReference()
    {
        await _service.SubmitApplicationAsync(TenantId, ProviderId,
            new SubmitApplicationRequest { ApplicationSource = "Manual" }, "actor-1", null);

        var act = () => _service.RecordDecisionAsync(TenantId, ProviderId,
            new RecordDecisionRequest
            {
                Decision = CredentialingDecision.Approved,
                DecisionAuthorityType = DecisionAuthorityType.CredentialingCommittee,
                DecisionAuthorityId = "committee-x",
                CommitteeMembers = new[] { "m1" },
                CredentialingDate = DateTime.UtcNow,
                RecredentialingDueDate = DateTime.UtcNow.AddYears(2),
            },
            "actor-1", null);
        await act.Should().ThrowAsync<CredentialingValidationException>()
            .Where(ex => ex.Message.Contains("DecisionMinuteReference"));
    }

    [Fact]
    public async Task SubmitApplicationAsync_throws_NotFound_for_missing_provider()
    {
        var act = () => _service.SubmitApplicationAsync(
            TenantId, "missing-provider",
            new SubmitApplicationRequest { ApplicationSource = "Manual" },
            "actor-1", null);
        await act.Should().ThrowAsync<CredentialingNotFoundException>();
    }

    [Fact]
    public async Task RecordDecisionAsync_with_DelegatedAuthority_synthesizes_application_when_none_open()
    {
        var decidedAt = DateTimeOffset.UtcNow;
        var decision = await _service.RecordDecisionAsync(TenantId, ProviderId,
            new RecordDecisionRequest
            {
                Decision = CredentialingDecision.Approved,
                DecidedAt = decidedAt,
                CredentialingDate = decidedAt.UtcDateTime,
                RecredentialingDueDate = decidedAt.UtcDateTime.AddYears(2),
                DecisionAuthorityType = DecisionAuthorityType.DelegatedAuthority,
                DecisionAuthorityId = "delegated-actor",
            },
            actorId: "delegated-actor", correlationId: null);

        decision.EventType.Should().Be(CredentialingEventType.DecisionRecorded);
        // Two events appended — synthesized application + decision.
        var chain = _eventRepository.Store
            .Where(e => e.TenantId == TenantId && e.ProviderId == ProviderId)
            .OrderBy(e => e.Version)
            .ToList();
        chain.Should().HaveCount(2);
        chain[0].EventType.Should().Be(CredentialingEventType.ApplicationSubmitted);
        chain[0].EventId.Should().StartWith("synthesized-application:");
        chain[1].EventType.Should().Be(CredentialingEventType.DecisionRecorded);
        chain[1].ApplicationEventId.Should().Be(chain[0].EventId);

        _providerRepository.CredentialingProjectionPatches.Should()
            .Contain(p => p.Status == CredentialingStatus.Approved);
    }

    [Fact]
    public async Task RecordDecisionAsync_with_DelegatedAuthority_is_idempotent()
    {
        var decidedAt = DateTimeOffset.UtcNow;
        var request = new RecordDecisionRequest
        {
            Decision = CredentialingDecision.Approved,
            DecidedAt = decidedAt,
            CredentialingDate = decidedAt.UtcDateTime,
            RecredentialingDueDate = decidedAt.UtcDateTime.AddYears(2),
            DecisionAuthorityType = DecisionAuthorityType.DelegatedAuthority,
            DecisionAuthorityId = "delegated-actor",
        };

        var first = await _service.RecordDecisionAsync(TenantId, ProviderId, request, "delegated-actor", null);
        var second = await _service.RecordDecisionAsync(TenantId, ProviderId, request, "delegated-actor", null);

        first.EventId.Should().Be(second.EventId);
        first.Version.Should().Be(second.Version);
        // Chain should still be two events — synthesized + decision —
        // even after the retry.
        _eventRepository.Store
            .Count(e => e.TenantId == TenantId && e.ProviderId == ProviderId)
            .Should().Be(2);
    }

    [Fact]
    public async Task TriggerRecredentialingAsync_requires_prior_approval()
    {
        var act = () => _service.TriggerRecredentialingAsync(TenantId, ProviderId,
            new TriggerRecredentialingRequest { Reason = "DueDateElapsed" },
            "actor-1", null);
        await act.Should().ThrowAsync<CredentialingValidationException>();
    }

    [Fact]
    public async Task TriggerRecredentialingAsync_carries_PredecessorEventId_of_last_decision()
    {
        await _service.SubmitApplicationAsync(TenantId, ProviderId,
            new SubmitApplicationRequest { ApplicationSource = "Manual" }, "actor-1", null);
        var decision = await _service.RecordDecisionAsync(TenantId, ProviderId,
            new RecordDecisionRequest
            {
                Decision = CredentialingDecision.Approved,
                CredentialingDate = DateTime.UtcNow,
                RecredentialingDueDate = DateTime.UtcNow.AddYears(2),
                DecisionAuthorityType = DecisionAuthorityType.CredentialingCommittee,
                DecisionAuthorityId = "committee-x",
                CommitteeMembers = new[] { "m1", "m2" },
                DecisionMinuteReference = "minutes/123",
            },
            "actor-1", null);

        var trigger = await _service.TriggerRecredentialingAsync(TenantId, ProviderId,
            new TriggerRecredentialingRequest
            {
                Reason = "DueDateElapsed",
                TriggeredAt = DateTimeOffset.UtcNow.AddMinutes(1),
            },
            "actor-1", null);

        trigger.EventType.Should().Be(CredentialingEventType.RecredentialingTriggered);
        trigger.PredecessorEventId.Should().Be(decision.EventId);
        _providerRepository.CredentialingProjectionPatches.Last().Status
            .Should().Be(CredentialingStatus.Pending);
    }

    [Fact]
    public async Task WithdrawApplicationAsync_with_mismatched_eventId_throws_validation()
    {
        var app = await _service.SubmitApplicationAsync(TenantId, ProviderId,
            new SubmitApplicationRequest { ApplicationSource = "Manual" }, "actor-1", null);

        var act = () => _service.WithdrawApplicationAsync(TenantId, ProviderId,
            applicationEventId: "wrong-id",
            new WithdrawApplicationRequest { Reason = "test" },
            "actor-1", null);
        await act.Should().ThrowAsync<CredentialingValidationException>();
    }

    [Fact]
    public async Task GetCurrentStatusAsync_returns_Unknown_for_provider_without_chain()
    {
        var result = await _service.GetCurrentStatusAsync(TenantId, "no-chain");
        result.Status.Should().Be(CredentialingStatus.Unknown);
        result.EventCount.Should().Be(0);
    }

    [Fact]
    public async Task GetHistoryAsync_returns_paged_descending_with_continuation()
    {
        await _service.SubmitApplicationAsync(TenantId, ProviderId,
            new SubmitApplicationRequest
            {
                ApplicationSource = "Manual",
                SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
            },
            "actor-1", null);
        await _service.RecordPrimarySourceVerificationAsync(TenantId, ProviderId,
            new RecordPrimarySourceVerificationRequest
            {
                VerificationVendor = "CAQH",
                VerifiedItems = new[] { "License" },
                VerifiedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            },
            "actor-1", null);
        await _service.RecordDecisionAsync(TenantId, ProviderId,
            new RecordDecisionRequest
            {
                Decision = CredentialingDecision.Approved,
                DecisionAuthorityType = DecisionAuthorityType.CredentialingCommittee,
                DecisionAuthorityId = "c-1",
                CommitteeMembers = new[] { "m1" },
                DecisionMinuteReference = "minutes/abc",
                CredentialingDate = DateTime.UtcNow,
                RecredentialingDueDate = DateTime.UtcNow.AddYears(2),
            },
            "actor-1", null);

        var page1 = await _service.GetHistoryAsync(TenantId, ProviderId, null, limit: 2);
        page1.Items.Should().HaveCount(2);
        page1.ContinuationToken.Should().NotBeNull();

        var page2 = await _service.GetHistoryAsync(TenantId, ProviderId, page1.ContinuationToken, limit: 2);
        page2.Items.Should().HaveCount(1);
        page2.ContinuationToken.Should().BeNull();
    }

    private void SeedActiveProvider()
    {
        var provider = new Provider
        {
            Id = ProviderId,
            ProviderId = ProviderId,
            TenantId = TenantId,
            NPI = "1234567890",
            ProviderType = ProviderType.Individual,
            FirstName = "Test",
            LastName = "Provider",
            PrimarySpecialty = "Internal Medicine",
            TaxonomyCode = "207R00000X",
            VersionId = ProviderId,
            VersionNumber = 1,
            VersionState = ProviderVersionState.Active,
        };
        _providerRepository.CreateAsync(provider).GetAwaiter().GetResult();
    }
}
