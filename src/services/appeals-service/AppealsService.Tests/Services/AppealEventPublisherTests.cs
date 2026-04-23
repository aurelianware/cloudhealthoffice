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

    // ── Degraded mode ───────────────────────────────────────────────────

    [Fact]
    public async Task DegradedMode_WhenBootstrapServersEmpty_PublishIsNoOp()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kafka:BootstrapServers"] = ""
            })
            .Build();

        var publisher = new AppealEventPublisher(NullLogger<AppealEventPublisher>.Instance, config);
        await publisher.StartAsync(CancellationToken.None);

        // Should not throw — degraded-mode publish is a silent no-op.
        await publisher.PublishCreatedAsync(NewAppeal(), "user1", "corr1");
        await publisher.PublishStatusChangedAsync(
            NewAppeal(), AppealStatus.Draft, AppealStatus.Submitted, "user1", "corr1");
        await publisher.PublishClosedAsync(NewAppeal(), AppealStatus.InReview, "user1", "corr1");
        await publisher.PublishStatusMigratedAsync(
            NewAppeal(), "Approved", AppealClosureReasonCode.Approved, "system", "corr1");

        await publisher.StopAsync(CancellationToken.None);
    }
}
