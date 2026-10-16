using System.Net;
using System.Text.Json.Nodes;
using ClaimsExaminerService.Models;
using ClaimsExaminerService.Services;
using ClaimsExaminerService.Services.Anthropic;
using ClaimsExaminerService.Services.Events;
using ClaimsExaminerService.Services.Examiner;
using ClaimsExaminerService.Services.Kafka;
using CloudHealthOffice.Events;
using CloudHealthOffice.Events.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace CloudHealthOffice.ClaimsExaminerService.Tests.Services;

/// <summary>
/// claims.pended.v1 messages are no longer dropped on a transient failure.
/// Before, a claims-service transport error skipped the event, a failed
/// write-back advanced the offset, and a crashed consume loop exited silently.
/// Now a transient failure is retried, the last failure is dead-lettered, and
/// the offset only moves past a message that is processed or dead-lettered.
/// </summary>
public class ExaminerReliabilityTests
{
    private static ClaimPendedEvent NcciEvent() => new()
    {
        ClaimId = "claim-1",
        TenantId = "tenant-a",
        PendDetails = new PendDetails
        {
            PendCode = ExaminerPendCodes.Ncci,
            EditFailures =
            [
                new NcciEditFailureSnapshot
                {
                    EditType = "NcciPair", RuleId = "NE001", Column1Code = "27447", Column2Code = "27486",
                    AffectedLineNumbers = [2], ModifierOverridePresent = false, SuggestedCarc = "97",
                },
            ],
        },
    };

    private static ExaminerOrchestrator Orchestrator(IClaimsServiceClient claims, IAnthropicClient anthropic) =>
        new(claims, anthropic, new ExaminerPromptBuilder(), Substitute.For<IProviderRfaiHistoryClient>(),
            Substitute.For<IAiExaminationEventPublisher>(), NullLogger<ExaminerOrchestrator>.Instance);

    // ── Orchestrator: transient failures are thrown, not swallowed ──────

    [Fact]
    public async Task ClaimsServiceUnreachable_FetchingTheClaim_IsTransient()
    {
        var claims = Substitute.For<IClaimsServiceClient>();
        claims.GetClaimAsync(default!, default!, default).ThrowsAsyncForAnyArgs(new HttpRequestException("connection refused"));

        var act = () => Orchestrator(claims, Substitute.For<IAnthropicClient>()).ProcessAsync(NcciEvent(), CancellationToken.None);

        await Assert.ThrowsAsync<ExaminerTransientException>(act);
    }

    [Fact]
    public async Task ClaimsServiceFailing_TheWriteBack_IsTransient()
    {
        var claims = Substitute.For<IClaimsServiceClient>();
        claims.GetClaimAsync(default!, default!, default).ReturnsForAnyArgs(new ClaimSnapshot { Id = "claim-1", BillingProviderNPI = "1234567890" });
        claims.SetAiExaminationAsync(default!, default!, default!, default)
            .ThrowsAsyncForAnyArgs(new HttpRequestException("503", null, HttpStatusCode.ServiceUnavailable));
        var anthropic = Substitute.For<IAnthropicClient>();
        anthropic.CallWithToolAsync(default!, default!, default!, default).ReturnsForAnyArgs(new AnthropicToolResult
        {
            Arguments = new JsonObject { ["recommended_disposition"] = "Approve", ["confidence_score"] = 0.9 },
            ModelId = "model",
        });

        var act = () => Orchestrator(claims, anthropic).ProcessAsync(NcciEvent(), CancellationToken.None);

        await Assert.ThrowsAsync<ExaminerTransientException>(act);
    }

    [Fact]
    public async Task AnUnknownDispositionFromTheModel_EscalatesToAHuman()
    {
        var claims = Substitute.For<IClaimsServiceClient>();
        claims.GetClaimAsync(default!, default!, default).ReturnsForAnyArgs(new ClaimSnapshot { Id = "claim-1", BillingProviderNPI = "1234567890" });
        AiExaminationDto? written = null;
        claims.SetAiExaminationAsync(default!, default!, default!, default)
            .ReturnsForAnyArgs(call => { written = call.ArgAt<AiExaminationDto>(2); return true; });
        var anthropic = Substitute.For<IAnthropicClient>();
        anthropic.CallWithToolAsync(default!, default!, default!, default).ReturnsForAnyArgs(new AnthropicToolResult
        {
            Arguments = new JsonObject { ["recommended_disposition"] = "PayDouble", ["confidence_score"] = 0.99 },
            ModelId = "model",
        });

        await Orchestrator(claims, anthropic).ProcessAsync(NcciEvent(), CancellationToken.None);

        Assert.Equal(AiDispositions.EscalateToHuman, written!.RecommendedDisposition);
    }

    [Fact]
    public void TheToolSchema_OffersExactlyTheKnownDispositions()
    {
        var tool = new ExaminerPromptBuilder().BuildRecommendationTool();
        var schema = System.Text.Json.JsonSerializer.SerializeToNode(tool)!.ToJsonString();

        foreach (var d in AiDispositions.All) Assert.Contains($"\"{d}\"", schema);
        Assert.Equal("Approve", AiDispositions.Normalize("approve"));
        Assert.Equal(AiDispositions.EscalateToHuman, AiDispositions.Normalize(null));
    }

    // ── Claims-service client: a 5xx write-back is not a silent "false" ─

    private sealed class Answer(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
    }

    private static ClaimsServiceClient ClaimsClient(HttpStatusCode status) =>
        new(new HttpClient(new Answer(status)) { BaseAddress = new Uri("http://claims-service/") },
            NullLogger<ClaimsServiceClient>.Instance);

    [Fact]
    public async Task AWriteBackAnswered503_Throws()
    {
        var act = () => ClaimsClient(HttpStatusCode.ServiceUnavailable)
            .SetAiExaminationAsync("claim-1", "tenant-a", new AiExaminationDto(), CancellationToken.None);

        await Assert.ThrowsAsync<HttpRequestException>(act);
    }

    [Fact]
    public async Task AWriteBackAnswered409_IsAnAnswer_NotAFailure()
    {
        Assert.False(await ClaimsClient(HttpStatusCode.Conflict)
            .SetAiExaminationAsync("claim-1", "tenant-a", new AiExaminationDto(), CancellationToken.None));
    }

    // ── Message processor: retry, dead-letter, never drop ───────────────

    private sealed class FakeOrchestrator : IExaminerOrchestrator
    {
        public Queue<Exception?> Script { get; } = new();
        public Exception? Otherwise { get; set; }
        public int Calls;

        public Task ProcessAsync(ClaimPendedEvent evt, CancellationToken ct)
        {
            Calls++;
            var ex = Script.Count > 0 ? Script.Dequeue() : Otherwise;
            return ex is null ? Task.CompletedTask : Task.FromException(ex);
        }
    }

    private sealed class FakeProducer : IChoEventProducer
    {
        public int FailNext;
        public List<(string Topic, string Key, string Value, IReadOnlyDictionary<string, string> Headers)> Published { get; } = new();
        public int Attempts;
        public bool IsConfigured => true;

        public Task ProduceAsync(string topic, string key, string value, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
        {
            Attempts++;
            if (FailNext > 0)
            {
                FailNext--;
                throw new InvalidOperationException("broker down");
            }
            Published.Add((topic, key, value, headers));
            return Task.CompletedTask;
        }
    }

    private static ClaimPendedMessageProcessor Processor(FakeOrchestrator orchestrator, FakeProducer producer) =>
        new(new ServiceCollection().AddScoped<IExaminerOrchestrator>(_ => orchestrator).BuildServiceProvider()
                .GetRequiredService<IServiceScopeFactory>(),
            producer,
            Options.Create(new ClaimPendedConsumerOptions { MaxAttempts = 3, BaseDelay = TimeSpan.FromMilliseconds(1) }),
            NullLogger<ClaimPendedMessageProcessor>.Instance);

    private const string Topic = "claims.pended.v1";
    private const string Message = """{"claimId":"claim-1","tenantId":"tenant-a"}""";

    [Fact]
    public async Task ATransientFailure_IsRetried_AndThenProcessed()
    {
        var orchestrator = new FakeOrchestrator();
        orchestrator.Script.Enqueue(new ExaminerTransientException("claims-service down"));
        orchestrator.Script.Enqueue(new ExaminerTransientException("claims-service down"));
        var producer = new FakeProducer();

        var outcome = await Processor(orchestrator, producer).ProcessAsync(Topic, "claim-1", Message, CancellationToken.None);

        Assert.Equal(ClaimPendedMessageOutcome.Processed, outcome);
        Assert.Equal(3, orchestrator.Calls);
        Assert.Empty(producer.Published);
    }

    [Fact]
    public async Task ATransientFailureOnEveryAttempt_IsDeadLettered_WithTheOriginalMessage()
    {
        var orchestrator = new FakeOrchestrator { Otherwise = new ExaminerTransientException("claims-service down") };
        var producer = new FakeProducer();

        var outcome = await Processor(orchestrator, producer).ProcessAsync(Topic, "claim-1", Message, CancellationToken.None);

        Assert.Equal(ClaimPendedMessageOutcome.DeadLettered, outcome);
        Assert.Equal(3, orchestrator.Calls);
        var dead = Assert.Single(producer.Published);
        Assert.Equal("claims.pended.v1.dlq", dead.Topic);
        Assert.Equal(Message, dead.Value);
        Assert.Equal(Topic, dead.Headers["original-topic"]);
        Assert.Equal(nameof(ExaminerTransientException), dead.Headers["error-type"]);
    }

    [Fact]
    public async Task AnUnexpectedFailure_IsDeadLetteredAtOnce()
    {
        var orchestrator = new FakeOrchestrator { Otherwise = new NullReferenceException() };
        var producer = new FakeProducer();

        var outcome = await Processor(orchestrator, producer).ProcessAsync(Topic, "claim-1", Message, CancellationToken.None);

        Assert.Equal(ClaimPendedMessageOutcome.DeadLettered, outcome);
        Assert.Equal(1, orchestrator.Calls);
        Assert.Single(producer.Published);
    }

    [Fact]
    public async Task ADeadLetterTopicThatIsDown_IsRetried_TheMessageIsNotLost()
    {
        var orchestrator = new FakeOrchestrator { Otherwise = new NullReferenceException() };
        var producer = new FakeProducer { FailNext = 2 };

        var outcome = await Processor(orchestrator, producer).ProcessAsync(Topic, "claim-1", Message, CancellationToken.None);

        Assert.Equal(ClaimPendedMessageOutcome.DeadLettered, outcome);
        Assert.Equal(3, producer.Attempts);
        Assert.Single(producer.Published);
    }

    [Fact]
    public async Task AnUnreadableMessage_IsSkipped_NotRetried()
    {
        var orchestrator = new FakeOrchestrator();
        var producer = new FakeProducer();

        var outcome = await Processor(orchestrator, producer).ProcessAsync(Topic, "k", "{not json", CancellationToken.None);

        Assert.Equal(ClaimPendedMessageOutcome.Processed, outcome);
        Assert.Equal(0, orchestrator.Calls);
        Assert.Empty(producer.Published);
    }
}
