using AppealsService.HostedServices;
using AppealsService.Models;
using AppealsService.Services;
using AppealsService.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppealsService.Tests.Integration;

/// <summary>
/// End-to-end integration tests for the X12 275 ingress path. Uses the
/// in-memory repository as the storage substrate (mirrors Mongo + Cosmos
/// behavior per the
/// <c>AppealRepositoryClaimLookupTests</c> contract) and drives the
/// consumer's <c>HandleMessageAsync</c> directly with the JSON shapes
/// the Argo workflow does (and will) publish.
///
/// Two fixtures:
/// <list type="bullet">
///   <item><description><see cref="AppealContextFixtureTemplate"/> — the
///   post-Argo-PR shape with <c>context: "appeal"</c>, <c>claimId</c>, and
///   <c>controlNumber</c> populated. Asserts the full happy path.</description></item>
///   <item><description><see cref="CanonicalSolicitedFixture"/> — today's
///   canonical solicited fixture (no <c>context</c>, no <c>claimId</c>).
///   Asserts the consumer skips it cleanly: no repository write, no
///   dead-letter, no audit event.</description></item>
/// </list>
/// </summary>
public class Attachment275ConsumerIntegrationTests
{
    /// <summary>
    /// Synthesized post-Argo-PR envelope. Substitutes
    /// <c>{CLAIM_ID}</c> and <c>{CONTROL_NUMBER}</c> at test time. Mirrors
    /// the canonical
    /// <c>docs/testing/test-attachment-275-solicited.json</c> shape with
    /// the three new fields the Argo workflow update will introduce
    /// (see PR 4 plan, deferred item #10).
    /// </summary>
    private const string AppealContextFixtureTemplate = """
        {
          "tenantId": "tenant-txmco01",
          "context": "appeal",
          "claimId": "{CLAIM_ID}",
          "authorizationId": "auth-20260207-001",
          "rfaiReference": "RFAI-2026-12345",
          "payerId": "BSCA123456789",
          "payerName": "Blue Shield of California",
          "providerId": "1234567890",
          "providerName": "City Medical Center",
          "subscriberId": "BSCA987654321",
          "patientFirstName": "John",
          "patientLastName": "Doe",
          "documentType": "Medical Records",
          "documentFormat": "PDF",
          "rawX12": "ISA*00*REDACTED*00*REDACTED*ZZ*SENDER*ZZ*RECEIVER*260207*1430*^*00501*000000001*0*P*:~",
          "submittedDate": "2026-02-07T14:30:00Z",
          "controlNumber": "{CONTROL_NUMBER}"
        }
        """;

    /// <summary>
    /// Canonical solicited fixture today
    /// (<c>docs/testing/test-attachment-275-solicited.json</c>) — no
    /// <c>context</c>, no <c>claimId</c>, no <c>controlNumber</c>.
    /// Represents the production envelope shape until the Argo
    /// follow-up ships.
    /// </summary>
    private const string CanonicalSolicitedFixture = """
        {
          "tenantId": "blueshield-ca",
          "authorizationId": "auth-20260207-001",
          "rfaiReference": "RFAI-2026-12345",
          "payerId": "BSCA123456789",
          "payerName": "Blue Shield of California",
          "providerId": "1234567890",
          "providerName": "City Medical Center",
          "subscriberId": "BSCA987654321",
          "patientFirstName": "John",
          "patientLastName": "Doe",
          "documentType": "Medical Records",
          "documentFormat": "PDF",
          "rawX12": "ISA*00*REDACTED~",
          "submittedDate": "2026-02-07T14:30:00Z"
        }
        """;

    private static (Attachment275ConsumerHostedService Consumer,
        InMemoryAppealRepository Repository,
        RecordingAppealEventPublisher Publisher,
        RecordingAttachment275DeadLetterSink Sink) BuildHarness()
    {
        var repository = new InMemoryAppealRepository();
        var publisher = new RecordingAppealEventPublisher();
        var encryptor = new ReversibleAppealFieldEncryptor();
        var sink = new RecordingAttachment275DeadLetterSink();
        var mapper = new Attachment275EnvelopeMapper();
        var consumer = new Attachment275ConsumerHostedService(
            repository, publisher, encryptor, sink, mapper,
            NullLogger<Attachment275ConsumerHostedService>.Instance);
        return (consumer, repository, publisher, sink);
    }

    private static async Task<Appeal> SeedOpenAppeal(
        InMemoryAppealRepository repository, string tenantId, string claimId)
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
            Status = AppealStatus.Submitted,
            SubmittedDate = DateTime.UtcNow.AddDays(-2),
            CreatedAt = DateTime.UtcNow.AddDays(-2)
        };
        var genesis = new AppealEvent
        {
            TenantId = appeal.TenantId,
            AppealId = appeal.Id,
            EventId = Guid.NewGuid().ToString(),
            EventType = AppealEventType.AppealCreated,
            FromStatus = null,
            ToStatus = AppealStatus.Submitted,
            ActorId = "seed"
        };
        return await repository.CreateAsync(appeal, genesis);
    }

    [Fact]
    public async Task AppealContextFixture_RoutesToSeededAppeal_WithFullAuditLineage()
    {
        var (consumer, repository, publisher, sink) = BuildHarness();
        var seeded = await SeedOpenAppeal(repository, tenantId: "tenant-txmco01", claimId: "claim-txmco01-9001");
        var bht03 = "BHT03-2026-04-23-XYZ";
        var json = AppealContextFixtureTemplate
            .Replace("{CLAIM_ID}", seeded.ClaimId)
            .Replace("{CONTROL_NUMBER}", bht03);

        var outcome = await consumer.HandleMessageAsync(json, CancellationToken.None);

        outcome.Should().Be(Attachment275HandleOutcome.Routed);

        var stored = repository.PeekStored(seeded.TenantId, seeded.Id);
        stored.Should().NotBeNull();
        stored!.Attachments.Should().ContainSingle();
        stored.Attachments[0].ControlNumber.Should().Be(bht03);
        stored.Attachments[0].AttachmentTypeCode.Should().Be("OZ"); // Medical Records
        stored.Attachments[0].TransmissionCode.Should().Be("EL");   // default
        stored.AttachmentControlNumbers.Should().Contain(bht03);

        var auditEvents = await repository.ListByAppealAsync(seeded.TenantId, seeded.Id);
        var attachmentEvent = auditEvents.Single(e => e.EventType == AppealEventType.AppealAttachmentAdded);
        attachmentEvent.CorrelationId.Should().Be(bht03,
            "the BHT03 control number is the correlation key payer responses echo back");
        attachmentEvent.ActorId.Should().Be(Attachment275ConsumerHostedService.IngressActor);
        attachmentEvent.Payload!["ingressSource"]!.GetValue<string>().Should().Be("Availity275");

        publisher.AttachmentsAdded.Should().ContainSingle();
        publisher.AttachmentsAdded.Single().CorrelationId.Should().Be(bht03);

        sink.Envelopes.Should().BeEmpty();
        sink.Malformed.Should().BeEmpty();
    }

    [Fact]
    public async Task CanonicalSolicitedFixture_SkipsCleanly_PreArgoBaseline()
    {
        // Today's canonical envelope shape: no "context" field. Until
        // the Argo workflow follow-up ships, every production message
        // takes this branch. Must not dead-letter, must not write to
        // the repository, must not emit an audit event.
        var (consumer, repository, publisher, sink) = BuildHarness();
        // Seed an appeal so a "happy" lookup would succeed if we accidentally
        // tried to route — that way the assertion has teeth.
        var seeded = await SeedOpenAppeal(repository, tenantId: "blueshield-ca", claimId: "claim-1");

        var outcome = await consumer.HandleMessageAsync(CanonicalSolicitedFixture, CancellationToken.None);

        outcome.Should().Be(Attachment275HandleOutcome.SkippedNonAppealContext);

        var stored = repository.PeekStored(seeded.TenantId, seeded.Id);
        stored!.Attachments.Should().BeEmpty("no attachment must be written for a non-appeal-context envelope");

        var auditEvents = await repository.ListByAppealAsync(seeded.TenantId, seeded.Id);
        auditEvents.Should().NotContain(e => e.EventType == AppealEventType.AppealAttachmentAdded);

        publisher.AttachmentsAdded.Should().BeEmpty();
        sink.Envelopes.Should().BeEmpty();
        sink.Malformed.Should().BeEmpty();
    }

    [Fact]
    public async Task AppealContextFixture_DeadLetters_WhenSeededAppealIsClosed()
    {
        var (consumer, repository, publisher, sink) = BuildHarness();
        var seeded = await SeedOpenAppeal(repository, tenantId: "tenant-txmco01", claimId: "claim-closed-x");
        // Move the appeal to Closed via the repository's transition path
        // so the next 275 has nothing to land on.
        var transition = new AppealEvent
        {
            TenantId = seeded.TenantId,
            AppealId = seeded.Id,
            EventId = Guid.NewGuid().ToString(),
            EventType = AppealEventType.AppealClosed,
            FromStatus = AppealStatus.Submitted,
            ToStatus = AppealStatus.Closed,
            ActorId = "seed"
        };
        seeded.Status = AppealStatus.Closed;
        await repository.TransitionStatusAsync(seeded, transition);

        var json = AppealContextFixtureTemplate
            .Replace("{CLAIM_ID}", seeded.ClaimId)
            .Replace("{CONTROL_NUMBER}", "BHT-doesnt-matter");

        var outcome = await consumer.HandleMessageAsync(json, CancellationToken.None);

        outcome.Should().Be(Attachment275HandleOutcome.DeadLetteredNoOpenAppeal);
        sink.Envelopes.Should().ContainSingle(e => e.Reason == "no-open-appeal-for-claim");
    }
}
