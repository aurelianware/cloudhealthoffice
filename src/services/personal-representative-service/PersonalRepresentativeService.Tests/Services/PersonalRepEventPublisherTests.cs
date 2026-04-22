using System.Text.Json;
using PersonalRepresentativeService.Models;
using PersonalRepresentativeService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace PersonalRepresentativeService.Tests.Services;

public class PersonalRepEventPublisherTests
{
    private static IConfiguration Config(Dictionary<string, string?> kv) =>
        new ConfigurationBuilder().AddInMemoryCollection(kv).Build();

    [Fact]
    public async Task DegradedMode_NoBootstrap_PublishIsNoOp()
    {
        var pub = new PersonalRepEventPublisher(
            NullLogger<PersonalRepEventPublisher>.Instance,
            Config(new Dictionary<string, string?>()));
        await pub.StartAsync(CancellationToken.None);

        var rep = NewRep();
        await pub.PublishStatusChangedAsync(
            rep, null, PersonalRepStatus.Draft,
            associatedMemberIds: new[] { "M1" },
            "alice", "corr", CancellationToken.None);

        await pub.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DegradedMode_StopBeforeStart_DoesNotThrow()
    {
        var pub = new PersonalRepEventPublisher(
            NullLogger<PersonalRepEventPublisher>.Instance,
            Config(new Dictionary<string, string?>()));
        await pub.StopAsync(CancellationToken.None);
        await pub.DisposeAsync();
    }

    /// <summary>
    /// Field-whitelist guard for status-changed payload. Serialize the
    /// event, parse it, compare the key set to an explicit allow-list.
    /// </summary>
    [Fact]
    public void StatusChangedPayload_HasExactlyWhitelistedFields()
    {
        var rep = NewRep();
        rep.FirstName = "enc::should-never-appear";
        rep.LastName = "enc::also-not-in-event";
        rep.Email = "enc::nope";
        rep.PhoneNumber = "enc::never";
        rep.MailingAddressLine1 = "enc::hidden";
        rep.RelationshipNotes = "enc::secret";
        rep.InactivationReasonCode = PersonalRepInactivationReasonCode.PoaRevoked;

        var evt = PersonalRepEventPublisher.BuildStatusChangedEvent(
            rep, PersonalRepStatus.Active, PersonalRepStatus.Inactive,
            new[] { "M1", "M2" }, "alice", "corr-1");

        var json = JsonSerializer.Serialize(evt, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        using var doc = JsonDocument.Parse(json);
        var actualFields = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();

        var expected = new HashSet<string>
        {
            "eventId", "eventType", "eventVersion", "occurredAt",
            "tenantId", "personalRepId",
            "credentialType",
            "fromStatus", "toStatus",
            "effectiveFrom", "effectiveTo", "expiresAt",
            "associatedMemberIds",
            "actor", "correlationId",
            "inactivationReasonCode"
        };

        actualFields.Should().BeEquivalentTo(expected,
            "the event payload field list is a compliance contract — adding a field here requires a review of whether it leaks PHI-adjacent data");

        json.Should().NotContain("\"firstName\"");
        json.Should().NotContain("\"lastName\"");
        json.Should().NotContain("\"middleName\"");
        json.Should().NotContain("\"email\"");
        json.Should().NotContain("\"phoneNumber\"");
        json.Should().NotContain("\"mailingAddressLine1\"");
        json.Should().NotContain("\"relationshipNotes\"");
        json.Should().NotContain("\"proofOfAuthorityDocumentId\"");
        json.Should().NotContain("enc::");
    }

    [Fact]
    public void AssociationChangedPayload_HasExactlyWhitelistedFields()
    {
        var rep = NewRep();
        rep.FirstName = "enc::should-never-appear";
        rep.RelationshipNotes = "enc::secret";

        var association = new PersonalRepAssociation
        {
            Id = "assoc-1",
            TenantId = rep.TenantId,
            PairId = "pair-1",
            RepId = rep.Id,
            MemberId = "M1",
            Direction = AssociationDirection.RepToMember,
            CredentialType = rep.CredentialType,
            EffectiveFrom = DateTime.UtcNow
        };

        var evt = PersonalRepEventPublisher.BuildAssociationChangedEvent(
            rep, association, PersonalRepEventPublisher.AssociationAddedEventType,
            "alice", "corr-2");

        var json = JsonSerializer.Serialize(evt, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        using var doc = JsonDocument.Parse(json);
        var actualFields = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();

        var expected = new HashSet<string>
        {
            "eventId", "eventType", "eventVersion", "occurredAt",
            "tenantId", "personalRepId", "memberId",
            "credentialType",
            "effectiveFrom", "effectiveTo",
            "actor", "correlationId"
        };

        actualFields.Should().BeEquivalentTo(expected);

        json.Should().NotContain("\"firstName\"");
        json.Should().NotContain("\"lastName\"");
        json.Should().NotContain("\"relationshipNotes\"");
        json.Should().NotContain("enc::");
    }

    [Fact]
    public void StatusChangedPayload_GenesisEvent_FromStatusIsNull()
    {
        var rep = NewRep();
        var evt = PersonalRepEventPublisher.BuildStatusChangedEvent(
            rep, null, PersonalRepStatus.Draft, Array.Empty<string>(), "alice", "corr");
        evt.FromStatus.Should().BeNull();
        evt.ToStatus.Should().Be("Draft");
    }

    [Fact]
    public void EventTopicAndHeaders_MatchContract()
    {
        PersonalRepEventPublisher.StatusChangedTopic.Should().Be("personal-rep.status-changed.v1");
        PersonalRepEventPublisher.StatusChangedEventType.Should().Be("PersonalRepStatusChanged");
        PersonalRepEventPublisher.AssociationAddedEventType.Should().Be("PersonalRepAssociationAdded");
        PersonalRepEventPublisher.AssociationRemovedEventType.Should().Be("PersonalRepAssociationRemoved");
        PersonalRepEventPublisher.EventVersion.Should().Be("1.0");
    }

    private static PersonalRepresentative NewRep() => new()
    {
        TenantId = "tenant-a",
        Id = "rep-1",
        Status = PersonalRepStatus.Draft,
        CredentialType = PersonalRepCredentialType.LegalGuardian,
        EffectiveFrom = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddYears(1)
    };
}
