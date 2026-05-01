using ClaimsExaminerService.Models;
using ClaimsExaminerService.Services.Events;
using CloudHealthOffice.Events;
using CloudHealthOffice.Infrastructure.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace CloudHealthOffice.ClaimsExaminerService.Tests.Services.Events;

/// <summary>
/// Capability 5.9 — verifies <see cref="ServiceBusAiExaminationEventPublisher"/>
/// constructs the right payload, sends to the right topic with the
/// correct <see cref="SendOptions.MessageId"/> dedup key, applies the
/// <c>MessageType</c> application property, and degrades gracefully on
/// transport failure.
/// </summary>
public class ServiceBusAiExaminationEventPublisherTests
{
    private const string ClaimId = "claim-pub-1";
    private const string TenantId = "tenant-pub-1";

    private readonly IMessageBus _bus = Substitute.For<IMessageBus>();
    private readonly ServiceBusAiExaminationEventPublisher _sut;

    public ServiceBusAiExaminationEventPublisherTests()
    {
        _sut = new ServiceBusAiExaminationEventPublisher(
            _bus, NullLogger<ServiceBusAiExaminationEventPublisher>.Instance);
    }

    private static AiExaminationDto Examination(string disposition = "Approve", double confidence = 0.9) => new()
    {
        RecommendedDisposition = disposition,
        ConfidenceScore = confidence,
        Rationale = "test rationale",
        PolicyCitations = new List<string> { "CMS NCCI Manual ch.1" },
        ModelId = "claude-opus-4-6",
        PromptVersion = "ncci-pend-v1",
        GeneratedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task Sends_to_ai_examination_events_topic()
    {
        await _sut.PublishCompletedAsync(
            ClaimId, TenantId, Examination(), correlationId: null, CancellationToken.None);

        await _bus.Received(1).SendAsync(
            AiExaminationEventTopics.TopicName,
            Arg.Any<ClaimAiExaminationCompletedEvent>(),
            Arg.Any<SendOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MessageId_uses_claimId_only_for_terminal_dedup()
    {
        // Plan-First Decision 15 — disposition NOT in the dedup key so
        // re-emissions with a different recommendation are dropped by the
        // broker as duplicates of the same logical completion.
        SendOptions? captured = null;
        _bus.When(b => b.SendAsync(
                Arg.Any<string>(),
                Arg.Any<ClaimAiExaminationCompletedEvent>(),
                Arg.Any<SendOptions>(),
                Arg.Any<CancellationToken>()))
            .Do(call => captured = call.Arg<SendOptions>());

        await _sut.PublishCompletedAsync(
            ClaimId, TenantId, Examination("Approve"), correlationId: null, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal($"ai-completed:{ClaimId}", captured!.MessageId);
    }

    [Fact]
    public async Task MessageType_application_property_is_set()
    {
        SendOptions? captured = null;
        _bus.When(b => b.SendAsync(
                Arg.Any<string>(),
                Arg.Any<ClaimAiExaminationCompletedEvent>(),
                Arg.Any<SendOptions>(),
                Arg.Any<CancellationToken>()))
            .Do(call => captured = call.Arg<SendOptions>());

        await _sut.PublishCompletedAsync(
            ClaimId, TenantId, Examination(), correlationId: null, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.NotNull(captured!.Properties);
        Assert.True(captured.Properties!.ContainsKey(AiExaminationEventTopics.MessageTypeProperty));
        Assert.Equal(
            AiExaminationEventTopics.CompletedMessageType,
            captured.Properties![AiExaminationEventTopics.MessageTypeProperty]);
    }

    [Fact]
    public async Task CorrelationId_is_propagated_to_SendOptions()
    {
        SendOptions? captured = null;
        _bus.When(b => b.SendAsync(
                Arg.Any<string>(),
                Arg.Any<ClaimAiExaminationCompletedEvent>(),
                Arg.Any<SendOptions>(),
                Arg.Any<CancellationToken>()))
            .Do(call => captured = call.Arg<SendOptions>());

        await _sut.PublishCompletedAsync(
            ClaimId, TenantId, Examination(),
            correlationId: "corr-xyz",
            CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("corr-xyz", captured!.CorrelationId);
    }

    [Fact]
    public async Task Payload_carries_disposition_confidence_and_correlation()
    {
        ClaimAiExaminationCompletedEvent? captured = null;
        _bus.When(b => b.SendAsync(
                Arg.Any<string>(),
                Arg.Any<ClaimAiExaminationCompletedEvent>(),
                Arg.Any<SendOptions>(),
                Arg.Any<CancellationToken>()))
            .Do(call => captured = call.Arg<ClaimAiExaminationCompletedEvent>());

        await _sut.PublishCompletedAsync(
            ClaimId, TenantId, Examination("Deny", 0.78),
            correlationId: "corr-abc",
            CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(ClaimId, captured!.ClaimId);
        Assert.Equal(TenantId, captured.TenantId);
        Assert.Equal("Deny", captured.RecommendedDisposition);
        Assert.Equal(0.78, captured.ConfidenceScore);
        Assert.Equal("corr-abc", captured.CorrelationId);
    }

    [Fact]
    public async Task Transport_exception_is_swallowed()
    {
        // Mirrors ClaimEventPublisher's degraded-mode posture: claim DB
        // already has the AI examination written via HTTP, so the event
        // failing to publish is degraded notification, not data loss.
        _bus
            .When(b => b.SendAsync(
                Arg.Any<string>(),
                Arg.Any<ClaimAiExaminationCompletedEvent>(),
                Arg.Any<SendOptions>(),
                Arg.Any<CancellationToken>()))
            .Throw(new InvalidOperationException("simulated broker outage"));

        // Should NOT throw.
        await _sut.PublishCompletedAsync(
            ClaimId, TenantId, Examination(), correlationId: null, CancellationToken.None);
    }

    [Fact]
    public async Task Cancellation_token_propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _bus
            .When(b => b.SendAsync(
                Arg.Any<string>(),
                Arg.Any<ClaimAiExaminationCompletedEvent>(),
                Arg.Any<SendOptions>(),
                Arg.Any<CancellationToken>()))
            .Do(_ => throw new OperationCanceledException(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _sut.PublishCompletedAsync(
                ClaimId, TenantId, Examination(), correlationId: null, cts.Token));
    }

    [Fact]
    public async Task InMemoryMessageBus_dedup_drops_duplicate_completions()
    {
        // End-to-end: real InMemoryMessageBus + real publisher. The second
        // emission for the same claim should be silently dropped within
        // the dedup window, even if the disposition differs.
        var realBus = new InMemoryMessageBus();
        var pub = new ServiceBusAiExaminationEventPublisher(
            realBus, NullLogger<ServiceBusAiExaminationEventPublisher>.Instance);

        var received = new List<ClaimAiExaminationCompletedEvent>();
        var sub = realBus.Subscribe<ClaimAiExaminationCompletedEvent>(
            AiExaminationEventTopics.TopicName,
            (msg, ctx, ct) => { lock (received) received.Add(msg); return Task.CompletedTask; });
        await sub.StartAsync(CancellationToken.None);

        try
        {
            await pub.PublishCompletedAsync(
                ClaimId, TenantId, Examination("Approve", 0.9),
                correlationId: null, CancellationToken.None);
            await pub.PublishCompletedAsync(
                ClaimId, TenantId, Examination("Deny", 0.4),  // different disposition
                correlationId: null, CancellationToken.None);

            // Drain — give the in-memory channel a chance to dispatch.
            for (var i = 0; i < 50; i++)
            {
                lock (received)
                {
                    if (received.Count >= 1) break;
                }
                await Task.Delay(20);
            }

            lock (received)
            {
                Assert.Single(received);  // duplicate dropped
                Assert.Equal("Approve", received[0].RecommendedDisposition);
            }
        }
        finally
        {
            await sub.DisposeAsync();
            await realBus.DisposeAsync();
        }
    }
}
