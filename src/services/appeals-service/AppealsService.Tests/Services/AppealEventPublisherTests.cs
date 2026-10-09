using System.Text.Json;
using AppealsService.Models;
using AppealsService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppealsService.Tests.Services;

/// <summary>
/// Field-whitelist tests for every event payload. Serializes the payload
/// to JSON, enumerates the emitted keys, and asserts the exact set —
/// substring scans would miss "decisionReasonText" sneaking in under an
/// innocent-looking name. Also negative-asserts that no encrypted-at-rest
/// field name appears anywhere in the payload.
///
/// These tests are the primary guard that PHI-adjacent fields
/// (PatientName, AppealReason, DenialReason, NoteText, DecisionReason,
/// ReviewerNotes, Summary, Description) cannot silently leak onto the
/// event stream when the event payload builder is refactored.
/// </summary>
public class AppealEventPublisherTests
{
    private static readonly HashSet<string> EncryptedAtRestFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "patientName", "appealReason", "denialReason",
        "noteText", "decisionReason", "reviewerNotes",
        "summary", "description"
    };

    private static Appeal NewAppeal() => new()
    {
        TenantId = "t1",
        Id = "a1",
        AppealNumber = "APL-001",
        ClaimId = "c1",
        ClaimNumber = "CLM-001",
        MemberId = "m1",
        ProviderNPI = "1234567890",
        AppealType = AppealType.Reconsideration,
        AppealLevel = AppealLevel.FirstLevel,
        LineOfBusiness = LineOfBusiness.Commercial,
        Status = AppealStatus.Submitted,
        Source = AppealSource.ProviderPortal,
        IsUrgent = true,
        TargetResponseDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        // Encrypted-at-rest on the entity — these must NEVER leak into a
        // payload, so set them to distinctive strings that would be easy
        // to spot if they did.
        PatientName = "PATIENT::MUST::NOT::LEAK",
        AppealReason = "APPEAL_REASON::MUST::NOT::LEAK",
        DenialReason = "DENIAL_REASON::MUST::NOT::LEAK"
    };

    private static HashSet<string> JsonFields<T>(T payload)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        var json = JsonSerializer.Serialize(payload, options);
        using var doc = JsonDocument.Parse(json);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            keys.Add(prop.Name);
        }
        return keys;
    }

    private static void AssertNoEncryptedFieldValues<T>(T payload)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = JsonSerializer.Serialize(payload, options);
        json.Should().NotContain("MUST::NOT::LEAK", "encrypted-at-rest field values must not appear in event payloads");
    }

    [Fact]
    public void AppealCreated_FieldWhitelist()
    {
        var payload = AppealEventPublisher.BuildCreatedPayload(NewAppeal(), "user1", "corr1");
        JsonFields(payload).Should().BeEquivalentTo(new[]
        {
            "eventId", "eventType", "eventVersion", "occurredAt",
            "tenantId", "appealId",
            "appealNumber", "claimId", "claimNumber", "memberId", "providerNPI",
            "appealType", "appealLevel", "lineOfBusiness", "source",
            "targetResponseDate", "isUrgent",
            "actor", "correlationId"
        });
        AssertNoEncryptedFieldValues(payload);
    }

    [Fact]
    public void AppealStatusChanged_FieldWhitelist()
    {
        var payload = AppealEventPublisher.BuildStatusChangedPayload(
            NewAppeal(), AppealStatus.Submitted, AppealStatus.InReview, "user1", "corr1");
        JsonFields(payload).Should().BeEquivalentTo(new[]
        {
            "eventId", "eventType", "eventVersion", "occurredAt",
            "tenantId", "appealId",
            "fromStatus", "toStatus",
            "actor", "correlationId"
        });
        AssertNoEncryptedFieldValues(payload);
    }

    [Fact]
    public void AppealClosed_FieldWhitelist()
    {
        var a = NewAppeal();
        a.Status = AppealStatus.Closed;
        a.ClosureReasonCode = AppealClosureReasonCode.PartialApproval;
        a.Decision = new AppealDecision
        {
            DecisionType = AppealDecisionType.PartialApproval,
            ApprovedAmount = 1500.00m,
            DecisionReason = "MUST::NOT::LEAK", // encrypted on entity; must not be on payload
            ReviewerNotes = "MUST::NOT::LEAK"
        };
        a.DecisionDate = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc);

        var payload = AppealEventPublisher.BuildClosedPayload(a, AppealStatus.InReview, "user1", "corr1");
        JsonFields(payload).Should().BeEquivalentTo(new[]
        {
            "eventId", "eventType", "eventVersion", "occurredAt",
            "tenantId", "appealId",
            "fromStatus", "closureReasonCode",
            "decisionType", "approvedAmount", "decisionDate",
            "actor", "correlationId"
        });
        AssertNoEncryptedFieldValues(payload);
    }

    [Fact]
    public void AppealNoteAdded_FieldWhitelist()
    {
        var note = new AppealNote
        {
            CreatedBy = "reviewer1",
            NoteText = "MUST::NOT::LEAK",
            IsInternal = true
        };
        var payload = AppealEventPublisher.BuildNoteAddedPayload(NewAppeal(), note, "user1", "corr1");
        JsonFields(payload).Should().BeEquivalentTo(new[]
        {
            "eventId", "eventType", "eventVersion", "occurredAt",
            "tenantId", "appealId",
            "noteId", "author", "createdAt", "isInternal",
            "actor", "correlationId"
        });
        AssertNoEncryptedFieldValues(payload);
    }

    [Fact]
    public void AppealAttachmentAdded_FieldWhitelist()
    {
        var att = new AppealAttachment
        {
            AttachmentTypeCode = "OZ",
            TransmissionCode = "EL",
            ControlNumber = "275-000001",
            Description = "MUST::NOT::LEAK"
        };
        var payload = AppealEventPublisher.BuildAttachmentAddedPayload(NewAppeal(), att, "user1", "corr1");
        JsonFields(payload).Should().BeEquivalentTo(new[]
        {
            "eventId", "eventType", "eventVersion", "occurredAt",
            "tenantId", "appealId",
            "attachmentId", "attachmentTypeCode", "transmissionCode", "controlNumber", "uploadedAt",
            "actor", "correlationId"
        });
        AssertNoEncryptedFieldValues(payload);
    }

    [Fact]
    public void AppealAttachmentAcknowledged_FieldWhitelist()
    {
        var att = new AppealAttachment
        {
            AttachmentTypeCode = "OZ",
            TransmissionCode = "EL",
            AcknowledgmentReceived = true,
            SentDate = new DateTime(2026, 5, 3, 0, 0, 0, DateTimeKind.Utc),
            Description = "MUST::NOT::LEAK"
        };
        var payload = AppealEventPublisher.BuildAttachmentAcknowledgedPayload(NewAppeal(), att, "user1", "corr1");
        JsonFields(payload).Should().BeEquivalentTo(new[]
        {
            "eventId", "eventType", "eventVersion", "occurredAt",
            "tenantId", "appealId",
            "attachmentId", "acknowledgmentReceived", "sentDate",
            "actor", "correlationId"
        });
        AssertNoEncryptedFieldValues(payload);
    }

    [Fact]
    public void AppealOverdueObserved_FieldWhitelist()
    {
        var payload = AppealEventPublisher.BuildOverdueObservedPayload(NewAppeal(), "system", "corr1");
        JsonFields(payload).Should().BeEquivalentTo(new[]
        {
            "eventId", "eventType", "eventVersion", "occurredAt",
            "tenantId", "appealId",
            "currentStatus", "targetResponseDate",
            "actor", "correlationId"
        });
        AssertNoEncryptedFieldValues(payload);
    }

    [Fact]
    public void AppealAssigned_FieldWhitelist()
    {
        var a = NewAppeal();
        a.AssignedReviewerId = "reviewer-99";
        var payload = AppealEventPublisher.BuildAssignedPayload(a, "reviewer-01", "user1", "corr1");
        JsonFields(payload).Should().BeEquivalentTo(new[]
        {
            "eventId", "eventType", "eventVersion", "occurredAt",
            "tenantId", "appealId",
            "assignedReviewerId", "previousReviewerId",
            "actor", "correlationId"
        });
        AssertNoEncryptedFieldValues(payload);
    }

    [Fact]
    public void AppealStatusMigrated_FieldWhitelist()
    {
        var payload = AppealEventPublisher.BuildStatusMigratedPayload(
            NewAppeal(), "Approved", AppealClosureReasonCode.Approved, "system:migration", "corr1");
        JsonFields(payload).Should().BeEquivalentTo(new[]
        {
            "eventId", "eventType", "eventVersion", "occurredAt",
            "tenantId", "appealId",
            "legacyStatus", "mappedReasonCode",
            "actor", "correlationId"
        });
        AssertNoEncryptedFieldValues(payload);
    }

    [Fact]
    public void AppealDeadlineExtended_FieldWhitelist()
    {
        var a = NewAppeal();
        a.LineOfBusiness = LineOfBusiness.Medicare;
        a.DeadlineExtension = new AppealDeadlineExtension
        {
            Reason = AppealExtensionReason.PlanNeedsInfo,
            ExtensionDays = 14,
            PreviousTargetResponseDate = a.TargetResponseDate!.Value,
            NewTargetResponseDate = a.TargetResponseDate!.Value.AddDays(14),
            WrittenNoticeSentAt = new DateTime(2026, 4, 20, 0, 0, 0, DateTimeKind.Utc),
            ExtendedBy = "user1",
            RegulatoryBasis = "42 CFR 422.590(f)"
        };
        a.TargetResponseDate = a.DeadlineExtension.NewTargetResponseDate;

        var payload = AppealEventPublisher.BuildDeadlineExtendedPayload(a, "user1", "corr1");
        JsonFields(payload).Should().BeEquivalentTo(new[]
        {
            "eventId", "eventType", "eventVersion", "occurredAt",
            "tenantId", "appealId",
            "currentStatus", "lineOfBusiness", "reason", "extensionDays",
            "previousTargetResponseDate", "targetResponseDate", "writtenNoticeSentAt",
            "regulatoryBasis",
            "actor", "correlationId"
        });
        payload.Reason.Should().Be("PlanNeedsInfo");
        payload.ExtensionDays.Should().Be(14);
        AssertNoEncryptedFieldValues(payload);
    }

    // ── Disabled mode: no silent drop ───────────────────────────────────

    [Fact]
    public async Task DisabledMode_ProduceThrows_InsteadOfSilentlyDropping()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kafka:BootstrapServers"] = ""
            })
            .Build();

        var publisher = new AppealEventPublisher(NullLogger<AppealEventPublisher>.Instance, config);
        await publisher.StartAsync(CancellationToken.None);
        (await publisher.Started).Should().Be(AppealEventPublisherState.Disabled);

        // The outbox dispatcher must learn the event was NOT delivered, so
        // it stays in the outbox; the old path returned as if it had been.
        var message = new AppealOutboxMessage
        {
            EventId = "e1", EventType = AppealEventPublisher.AppealCreatedType,
            TenantId = "t1", AppealId = "a1", PayloadJson = "{}"
        };
        var act = () => publisher.ProduceAsync(message, CancellationToken.None);
        await act.Should().ThrowAsync<AppealEventTransportUnavailableException>();
        publisher.IsTransient(new AppealEventTransportUnavailableException("x")).Should().BeTrue();

        await publisher.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(Confluent.Kafka.ErrorCode.Local_MsgTimedOut, true)]
    [InlineData(Confluent.Kafka.ErrorCode.Local_AllBrokersDown, true)]
    [InlineData(Confluent.Kafka.ErrorCode.Local_Transport, true)]
    // Broker-wide conditions hit every event alike: pause, never dead-letter.
    [InlineData(Confluent.Kafka.ErrorCode.UnknownTopicOrPart, true)]
    [InlineData(Confluent.Kafka.ErrorCode.Local_UnknownTopic, true)]
    [InlineData(Confluent.Kafka.ErrorCode.TopicAuthorizationFailed, true)]
    [InlineData(Confluent.Kafka.ErrorCode.ClusterAuthorizationFailed, true)]
    [InlineData(Confluent.Kafka.ErrorCode.SaslAuthenticationFailed, true)]
    [InlineData(Confluent.Kafka.ErrorCode.Local_Authentication, true)]
    // About one message only: retried, then dead-lettered.
    [InlineData(Confluent.Kafka.ErrorCode.MsgSizeTooLarge, false)]
    [InlineData(Confluent.Kafka.ErrorCode.InvalidMsg, false)]
    public void IsTransient_Separates_Outages_From_Rejected_Messages(Confluent.Kafka.ErrorCode code, bool transient)
    {
        AppealEventPublisher.IsTransientError(new Confluent.Kafka.KafkaException(code)).Should().Be(transient);
        AppealEventPublisher.IsTransientError(new InvalidOperationException("boom")).Should().BeFalse();
    }

    [Fact]
    public void Fatal_Producer_Errors_Are_Transient()
    {
        var fatal = new Confluent.Kafka.KafkaException(
            new Confluent.Kafka.Error(Confluent.Kafka.ErrorCode.InvalidMsg, "fenced", isFatal: true));
        AppealEventPublisher.IsTransientError(fatal).Should().BeTrue("the producer is rebuilt and the event retried, not dead-lettered");
    }
}
