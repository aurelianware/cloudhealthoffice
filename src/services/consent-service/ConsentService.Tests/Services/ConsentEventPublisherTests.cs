using System.Text.Json;
using ConsentService.Models;
using ConsentService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConsentService.Tests.Services;

public class ConsentEventPublisherTests
{
    private static IConfiguration Config(Dictionary<string, string?> kv) =>
        new ConfigurationBuilder().AddInMemoryCollection(kv).Build();

    [Fact]
    public async Task DegradedMode_NoBootstrap_PublishIsNoOp()
    {
        // No Kafka:BootstrapServers configured. StartAsync must still succeed,
        // PublishStatusChangedAsync must not throw.
        var pub = new ConsentEventPublisher(NullLogger<ConsentEventPublisher>.Instance,
            Config(new Dictionary<string, string?>()));
        await pub.StartAsync(CancellationToken.None);

        var consent = NewConsent();
        await pub.PublishStatusChangedAsync(consent, null, ConsentStatus.Draft, "alice", "corr", CancellationToken.None);

        await pub.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DegradedMode_StopBeforeStart_DoesNotThrow()
    {
        // Defensive: the xUnit fixture teardown order or a partial Start
        // might end up disposing a publisher that never started. Must be
        // safe.
        var pub = new ConsentEventPublisher(NullLogger<ConsentEventPublisher>.Instance,
            Config(new Dictionary<string, string?>()));
        await pub.StopAsync(CancellationToken.None);
        await pub.DisposeAsync();
    }

    /// <summary>
    /// Field-whitelist guard. Serialize the event, parse it, and compare
    /// the key set to an explicit allow-list. Catches BOTH (a) raw
    /// plaintext leakage and (b) accidental inclusion of encrypted
    /// ciphertext — either would fail the whitelist comparison.
    /// </summary>
    [Fact]
    public void EventPayload_HasExactlyWhitelistedFields()
    {
        var consent = NewConsent();
        consent.Reason = "enc::should-never-appear-in-event";
        consent.GrantedToName = "enc::also-not-in-event";
        consent.GrantedToContact = "enc::nope";
        consent.Purpose = "enc::never";
        consent.SensitiveCategory = "HIV";
        consent.RevocationReasonCode = ConsentRevocationReasonCode.MemberRequest;

        var evt = ConsentEventPublisher.BuildEvent(
            consent, ConsentStatus.Active, ConsentStatus.Revoked, "alice", "corr-1");

        var json = JsonSerializer.Serialize(evt, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        using var doc = JsonDocument.Parse(json);
        var actualFields = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();

        var expected = new HashSet<string>
        {
            "eventId", "eventType", "eventVersion", "occurredAt",
            "tenantId", "consentId", "memberId",
            "consentType", "sensitiveCategory",
            "fromStatus", "toStatus",
            "effectiveAt", "expiresAt",
            "actor", "correlationId",
            "revocationReasonCode"
        };

        actualFields.Should().BeEquivalentTo(expected,
            "the event payload field list is a compliance contract — adding a field here requires a review of whether it leaks PHI-adjacent data");

        // Defense in depth: the specific sensitive fields are nowhere in the JSON.
        // Use quoted property-name matches so substring collisions with other
        // fields (e.g. "revocationReasonCode" containing "reason") don't give
        // a false miss.
        json.Should().NotContain("\"reason\"");
        json.Should().NotContain("\"grantedToName\"");
        json.Should().NotContain("\"grantedToContact\"");
        json.Should().NotContain("\"purpose\"");
        json.Should().NotContain("enc::");
    }

    [Fact]
    public void EventPayload_GenesisEvent_FromStatusIsNull()
    {
        var consent = NewConsent();
        var evt = ConsentEventPublisher.BuildEvent(consent, null, ConsentStatus.Draft, "alice", "corr");
        evt.FromStatus.Should().BeNull();
        evt.ToStatus.Should().Be("Draft");
    }

    [Fact]
    public void EventTopicAndHeaders_MatchContract()
    {
        // Topic name and event type/version are effectively public API for
        // downstream consumers. Lock them down.
        ConsentEventPublisher.StatusChangedTopic.Should().Be("consent.status-changed.v1");
        ConsentEventPublisher.EventTypeName.Should().Be("ConsentStatusChanged");
        ConsentEventPublisher.EventVersion.Should().Be("1.0");
    }

    private static Consent NewConsent() => new()
    {
        TenantId = "tenant-a",
        Id = "consent-1",
        MemberId = "M1",
        ConsentType = ConsentType.GeneralAuthorization,
        Status = ConsentStatus.Draft,
        EffectiveAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddYears(1),
        GrantedBy = "alice"
    };
}
