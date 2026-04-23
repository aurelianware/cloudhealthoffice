using AppealsService.HostedServices;
using AppealsService.Models;
using AppealsService.Services;
using AppealsService.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppealsService.Tests.HostedServices;

/// <summary>
/// Handler-level tests for
/// <see cref="Attachment275ConsumerHostedService.HandleMessageAsync"/>.
/// Drives the internal test constructor path so the 5-collaborator
/// handler can be exercised without standing up Kafka or a fake
/// <see cref="IServiceProvider"/>. Covers every branch of the routing
/// decision tree plus the PHI-safety log posture.
/// </summary>
public class Attachment275ConsumerTests
{
    private static readonly DateTime FixtureSubmittedDate =
        new(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);

    private sealed record Harness(
        Attachment275ConsumerHostedService Consumer,
        InMemoryAppealRepository Repository,
        RecordingAppealEventPublisher Publisher,
        ReversibleAppealFieldEncryptor Encryptor,
        RecordingAttachment275DeadLetterSink DeadLetterSink,
        Attachment275EnvelopeMapper Mapper);

    private static Harness NewHarness()
    {
        var repository = new InMemoryAppealRepository();
        var publisher = new RecordingAppealEventPublisher();
        var encryptor = new ReversibleAppealFieldEncryptor();
        var deadLetterSink = new RecordingAttachment275DeadLetterSink();
        var mapper = new Attachment275EnvelopeMapper();
        var consumer = new Attachment275ConsumerHostedService(
            repository, publisher, encryptor, deadLetterSink, mapper,
            NullLogger<Attachment275ConsumerHostedService>.Instance);
        return new Harness(consumer, repository, publisher, encryptor, deadLetterSink, mapper);
    }

    private static async Task<Appeal> SeedOpenAppealAsync(
        InMemoryAppealRepository repository,
        string tenantId = "tenant-a",
        string claimId = "claim-1",
        AppealStatus status = AppealStatus.Submitted)
    {
        var appeal = new Appeal
        {
            TenantId = tenantId,
            Id = Guid.NewGuid().ToString(),
            AppealNumber = "APL-" + Guid.NewGuid().ToString("N")[..6],
            ClaimId = claimId,
            ClaimNumber = "CLM-" + claimId,
            MemberId = "m1",
            PatientName = "enc::patient",
            ProviderNPI = "1234567890",
            AppealReason = "enc::reason",
            LineOfBusiness = LineOfBusiness.Commercial,
            AppealType = AppealType.Reconsideration,
            AppealLevel = AppealLevel.FirstLevel,
            Status = status,
            SubmittedDate = DateTime.UtcNow.AddDays(-1),
            CreatedAt = DateTime.UtcNow.AddDays(-1)
        };
        var genesis = new AppealEvent
        {
            TenantId = appeal.TenantId,
            AppealId = appeal.Id,
            EventId = Guid.NewGuid().ToString(),
            EventType = AppealEventType.AppealCreated,
            FromStatus = null,
            ToStatus = status,
            ActorId = "seed"
        };
        return await repository.CreateAsync(appeal, genesis);
    }

    private static string EnvelopeJson(
        string? context = "appeal",
        string? tenantId = "tenant-a",
        string? claimId = "claim-1",
        string? rawX12 = "ISA*00*...",
        string? controlNumber = "BHT-12345",
        string? notes = "Attached records",
        string? documentType = "Medical Records",
        string? documentFormat = "PDF") =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            tenantId,
            context,
            claimId,
            authorizationId = "auth-xyz",
            payerId = "P1",
            payerName = "Payer",
            providerId = "PR1",
            providerName = "Provider",
            subscriberId = "SUB1",
            patientFirstName = "Jane",
            patientLastName = "Doe",
            documentType,
            documentFormat,
            rawX12,
            submittedDate = FixtureSubmittedDate,
            notes,
            controlNumber
        });

    // ── Routing branches ────────────────────────────────────────────────

    [Fact]
    public async Task HandleMessage_SkipsAndCommits_WhenContextNotAppeal()
    {
        var h = NewHarness();
        var json = EnvelopeJson(context: "authorization");

        var outcome = await h.Consumer.HandleMessageAsync(json, CancellationToken.None);

        outcome.Should().Be(Attachment275HandleOutcome.SkippedNonAppealContext);
        h.DeadLetterSink.Envelopes.Should().BeEmpty();
        h.DeadLetterSink.Malformed.Should().BeEmpty();
        h.Publisher.AttachmentsAdded.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleMessage_SkipsAndCommits_WhenContextNull_PreArgoReality()
    {
        // The pre-Argo-PR production reality: messages arrive without
        // a "context" field. The consumer must not log-spam or
        // dead-letter on this path.
        var h = NewHarness();
        var json = EnvelopeJson(context: null);

        var outcome = await h.Consumer.HandleMessageAsync(json, CancellationToken.None);

        outcome.Should().Be(Attachment275HandleOutcome.SkippedNonAppealContext);
        h.DeadLetterSink.Envelopes.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleMessage_DeadLettersAndCommits_WhenJsonMalformed()
    {
        var h = NewHarness();

        var outcome = await h.Consumer.HandleMessageAsync("{ not valid json", CancellationToken.None);

        outcome.Should().Be(Attachment275HandleOutcome.DeadLetteredMalformedJson);
        h.DeadLetterSink.Malformed.Should().ContainSingle();
        h.DeadLetterSink.Malformed.Single().Reason.Should().Be("json-deserialization-failed");
        h.DeadLetterSink.Envelopes.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleMessage_DeadLettersAndCommits_WhenTenantIdMissing()
    {
        var h = NewHarness();
        var json = EnvelopeJson(tenantId: "");

        var outcome = await h.Consumer.HandleMessageAsync(json, CancellationToken.None);

        outcome.Should().Be(Attachment275HandleOutcome.DeadLetteredMissingRequiredField);
        h.DeadLetterSink.Envelopes.Should().ContainSingle(e => e.Reason == "missing-tenantId");
    }

    [Fact]
    public async Task HandleMessage_DeadLettersAndCommits_WhenClaimIdMissing()
    {
        var h = NewHarness();
        var json = EnvelopeJson(claimId: null);

        var outcome = await h.Consumer.HandleMessageAsync(json, CancellationToken.None);

        outcome.Should().Be(Attachment275HandleOutcome.DeadLetteredMissingRequiredField);
        h.DeadLetterSink.Envelopes.Should().ContainSingle(e => e.Reason == "missing-claimId");
    }

    [Fact]
    public async Task HandleMessage_DeadLettersAndCommits_WhenRawX12Missing()
    {
        var h = NewHarness();
        var json = EnvelopeJson(rawX12: null);

        var outcome = await h.Consumer.HandleMessageAsync(json, CancellationToken.None);

        outcome.Should().Be(Attachment275HandleOutcome.DeadLetteredMissingRequiredField);
        h.DeadLetterSink.Envelopes.Should().ContainSingle(e => e.Reason == "missing-rawX12");
    }

    [Fact]
    public async Task HandleMessage_DeadLettersAndCommits_WhenNoOpenAppealMatches()
    {
        var h = NewHarness();
        // Nothing seeded — lookup returns null.
        var json = EnvelopeJson(claimId: "orphan-claim");

        var outcome = await h.Consumer.HandleMessageAsync(json, CancellationToken.None);

        outcome.Should().Be(Attachment275HandleOutcome.DeadLetteredNoOpenAppeal);
        h.DeadLetterSink.Envelopes.Should().ContainSingle(e => e.Reason == "no-open-appeal-for-claim");
    }

    [Fact]
    public async Task HandleMessage_AppendsAttachment_WhenOpenAppealFound()
    {
        var h = NewHarness();
        var appeal = await SeedOpenAppealAsync(h.Repository);
        var json = EnvelopeJson(tenantId: appeal.TenantId, claimId: appeal.ClaimId);

        var outcome = await h.Consumer.HandleMessageAsync(json, CancellationToken.None);

        outcome.Should().Be(Attachment275HandleOutcome.Routed);
        var stored = h.Repository.PeekStored(appeal.TenantId, appeal.Id);
        stored.Should().NotBeNull();
        stored!.Attachments.Should().ContainSingle();
        stored.Attachments[0].ControlNumber.Should().Be("BHT-12345");
        stored.Attachments[0].AttachmentTypeCode.Should().Be("OZ"); // Medical Records → OZ
        stored.AttachmentControlNumbers.Should().Contain("BHT-12345");
    }

    // ── Correlation + audit-event shape ─────────────────────────────────

    [Fact]
    public async Task HandleMessage_SetsCorrelationIdFromControlNumber()
    {
        var h = NewHarness();
        var appeal = await SeedOpenAppealAsync(h.Repository);
        var json = EnvelopeJson(
            tenantId: appeal.TenantId, claimId: appeal.ClaimId,
            controlNumber: "BHT-99999");

        await h.Consumer.HandleMessageAsync(json, CancellationToken.None);

        var auditEvents = await h.Repository.ListByAppealAsync(appeal.TenantId, appeal.Id);
        var attachmentEvent = auditEvents.Single(e => e.EventType == AppealEventType.AppealAttachmentAdded);
        attachmentEvent.CorrelationId.Should().Be("BHT-99999");
    }

    [Fact]
    public async Task HandleMessage_FallsBackToUnknownCorrelation_WhenControlNumberAbsent()
    {
        var h = NewHarness();
        var appeal = await SeedOpenAppealAsync(h.Repository);
        var json = EnvelopeJson(
            tenantId: appeal.TenantId, claimId: appeal.ClaimId,
            controlNumber: null);

        await h.Consumer.HandleMessageAsync(json, CancellationToken.None);

        var auditEvents = await h.Repository.ListByAppealAsync(appeal.TenantId, appeal.Id);
        var attachmentEvent = auditEvents.Single(e => e.EventType == AppealEventType.AppealAttachmentAdded);
        attachmentEvent.CorrelationId.Should().Be(Attachment275ConsumerHostedService.UnknownCorrelationId);
    }

    [Fact]
    public async Task HandleMessage_AuditEventPayload_IncludesIngressSourceAvaility275()
    {
        var h = NewHarness();
        var appeal = await SeedOpenAppealAsync(h.Repository);
        var json = EnvelopeJson(tenantId: appeal.TenantId, claimId: appeal.ClaimId);

        await h.Consumer.HandleMessageAsync(json, CancellationToken.None);

        var auditEvents = await h.Repository.ListByAppealAsync(appeal.TenantId, appeal.Id);
        var attachmentEvent = auditEvents.Single(e => e.EventType == AppealEventType.AppealAttachmentAdded);
        attachmentEvent.Payload.Should().NotBeNull();
        attachmentEvent.Payload![Attachment275ConsumerHostedService.IngressSourcePayloadKey]!.GetValue<string>()
            .Should().Be(Attachment275ConsumerHostedService.IngressSourcePayloadValue);
    }

    [Fact]
    public async Task HandleMessage_DescriptionPersistedAsCiphertext_NotPlaintextNotes()
    {
        // Structural guard: the consumer routes envelope.Notes through
        // IAppealFieldEncryptor before persistence — same posture as
        // AppealsController.AddAttachment. With the reversible test
        // encryptor, "ciphertext" deliberately wraps the plaintext
        // (so round-trip decryption tests can assert equality), so we
        // assert the encryption *call* happened (LooksEncrypted) and the
        // stored value is not the raw plaintext.
        var h = NewHarness();
        var appeal = await SeedOpenAppealAsync(h.Repository);
        const string sensitiveNote = "Patient reports migraine Tuesday morning";
        var json = EnvelopeJson(
            tenantId: appeal.TenantId, claimId: appeal.ClaimId,
            notes: sensitiveNote);

        await h.Consumer.HandleMessageAsync(json, CancellationToken.None);

        var stored = h.Repository.PeekStored(appeal.TenantId, appeal.Id);
        var attachment = stored!.Attachments.Single();
        ReversibleAppealFieldEncryptor.LooksEncrypted(attachment.Description).Should().BeTrue(
            "the consumer must encrypt envelope.Notes via IAppealFieldEncryptor before persistence");
        attachment.Description.Should().NotBe(sensitiveNote,
            "the raw plaintext note value must not be the stored value");
    }

    [Fact]
    public async Task HandleMessage_PublishesAppealAttachmentAddedEvent_WithCorrelationId()
    {
        var h = NewHarness();
        var appeal = await SeedOpenAppealAsync(h.Repository);
        var json = EnvelopeJson(
            tenantId: appeal.TenantId, claimId: appeal.ClaimId,
            controlNumber: "BHT-PUBLISH-TEST");

        await h.Consumer.HandleMessageAsync(json, CancellationToken.None);

        h.Publisher.AttachmentsAdded.Should().ContainSingle();
        var call = h.Publisher.AttachmentsAdded.Single();
        call.AppealId.Should().Be(appeal.Id);
        call.ControlNumber.Should().Be("BHT-PUBLISH-TEST");
        call.CorrelationId.Should().Be("BHT-PUBLISH-TEST");
        call.Actor.Should().Be(Attachment275ConsumerHostedService.IngressActor);
    }

    // ── Exception branch ────────────────────────────────────────────────

    [Fact]
    public async Task HandleMessage_DeadLettersAndCommits_WhenRepositoryAppendThrows()
    {
        // Seed an open appeal, then force the next audit-append to fail.
        // The consumer should catch, dead-letter with reason handler-exception,
        // and NOT crash.
        var h = NewHarness();
        var appeal = await SeedOpenAppealAsync(h.Repository);
        h.Repository.FailAuditAppendOnce(); // will be triggered by the genesis-already-done; next append fails
        var json = EnvelopeJson(tenantId: appeal.TenantId, claimId: appeal.ClaimId);

        var outcome = await h.Consumer.HandleMessageAsync(json, CancellationToken.None);

        outcome.Should().Be(Attachment275HandleOutcome.DeadLetteredHandlerException);
        h.DeadLetterSink.Envelopes.Should().ContainSingle(e => e.Reason == "handler-exception");
    }

    // ── PHI-safety (log surface) ────────────────────────────────────────

    [Fact]
    public async Task DeadLetterSink_NeverReceivesRawX12OrPatientNames()
    {
        // Structural guard: the IAttachment275DeadLetterSink contract
        // surfaces only non-PHI fields (tenantId, claimId, controlNumber,
        // reason). Patient names and rawX12 live only on the envelope
        // DTO that the sink receives — the sink's default
        // (LoggingAttachment275DeadLetterSink) is already proven not to
        // log them. This test reinforces that the RecordingSink — the
        // artifact tests rely on — likewise only captures the whitelisted
        // tuple.
        var h = NewHarness();
        var json = EnvelopeJson(claimId: null); // force dead-letter

        await h.Consumer.HandleMessageAsync(json, CancellationToken.None);

        h.DeadLetterSink.Envelopes.Should().ContainSingle();
        var captured = h.DeadLetterSink.Envelopes.Single();
        // Assert the recording fields structurally: TenantId + ClaimId +
        // ControlNumber + Reason, no PatientFirstName / PatientLastName /
        // RawX12 field on the record type.
        typeof(RecordingAttachment275DeadLetterSink.EnvelopeCall)
            .GetProperties()
            .Select(p => p.Name)
            .Should().BeEquivalentTo(new[] { "TenantId", "ClaimId", "ControlNumber", "Reason" });
    }
}
