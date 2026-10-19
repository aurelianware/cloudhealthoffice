using System.Net;
using ClaimsExaminerService.Models;
using ClaimsExaminerService.Services;
using ClaimsExaminerService.Services.Anthropic;
using ClaimsExaminerService.Services.Events;
using ClaimsExaminerService.Services.Examiner;
using CloudHealthOffice.Events;
using CloudHealthOffice.Infrastructure.Tests.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Xunit;

namespace CloudHealthOffice.ClaimsExaminerService.Tests.Services;

/// <summary>
/// An Anthropic outage the client could not ride out is transient: rate
/// limited (429), overloaded (529), an open circuit, or a timed-out attempt.
/// The orchestrator throws <see cref="ExaminerTransientException"/>, which
/// the consumer retries and then dead-letters, and writes nothing. Writing
/// EscalateToHuman would permanently route the claim to a person because of
/// a burst. A genuine failure still escalates, and host shutdown is neither.
/// </summary>
public class AnthropicTransientFailureTests
{
    private static ClaimPendedEvent NcciEvent() => new()
    {
        ClaimId = "claim-1",
        TenantId = "tenant-a",
        PendDetails = new PendDetails
        {
            PendCode = "NCCI",
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

    private static IClaimsServiceClient ClaimsService()
    {
        var claims = Substitute.For<IClaimsServiceClient>();
        claims.GetClaimAsync(default!, default!, default)
            .ReturnsForAnyArgs(new ClaimSnapshot { Id = "claim-1", TenantId = "tenant-a", BillingProviderNPI = "1234567890" });
        claims.SetAiExaminationAsync(default!, default!, default!, default).ReturnsForAnyArgs(true);
        return claims;
    }

    private static ExaminerOrchestrator Orchestrator(IClaimsServiceClient claims, IAnthropicClient anthropic) =>
        new(claims, anthropic, new ExaminerPromptBuilder(), Substitute.For<IProviderRfaiHistoryClient>(),
            Substitute.For<IAiExaminationEventPublisher>(), NullLogger<ExaminerOrchestrator>.Instance);

    // ── The exceptions themselves ─────────────────────────────────────

    public static TheoryData<string, Exception> TransientFailures => new()
    {
        { "429 after the client's retries", new HttpRequestException("429", null, HttpStatusCode.TooManyRequests) },
        { "529 after the client's retries", new HttpRequestException("529", null, (HttpStatusCode)529) },
        { "open circuit (as the shared handler surfaces it)",
            new HttpRequestException("circuit open", new BrokenCircuitException(), HttpStatusCode.ServiceUnavailable) },
        { "open circuit (Polly's own exception)", new BrokenCircuitException() },
        { "timeout (as the shared handler surfaces it)",
            new TaskCanceledException("timed out", new TimeoutException()) },
        { "timeout (Polly's own exception)", new TimeoutRejectedException() },
    };

    [Theory]
    [MemberData(nameof(TransientFailures))]
    public async Task ATransientAnthropicFailure_IsRetriedByTheConsumer_AndWritesNoEscalation(string _, Exception failure)
    {
        var claims = ClaimsService();
        var anthropic = Substitute.For<IAnthropicClient>();
        anthropic.CallWithToolAsync(default!, default!, default!, default).ThrowsAsyncForAnyArgs(failure);

        var act = () => Orchestrator(claims, anthropic).ProcessAsync(NcciEvent(), CancellationToken.None);

        var thrown = await Assert.ThrowsAsync<ExaminerTransientException>(act);
        Assert.Same(failure, thrown.InnerException);
        await claims.DidNotReceiveWithAnyArgs().SetAiExaminationAsync(default!, default!, default!, default);
    }

    public static TheoryData<string, Exception> GenuineFailures => new()
    {
        { "400 (the request is wrong)", new HttpRequestException("400", null, HttpStatusCode.BadRequest) },
        { "401 (the key is wrong)", new HttpRequestException("401", null, HttpStatusCode.Unauthorized) },
        { "an unreadable response", new System.Text.Json.JsonException("bad body") },
    };

    [Theory]
    [MemberData(nameof(GenuineFailures))]
    public async Task AGenuineAnthropicFailure_StillEscalatesToAHuman(string _, Exception failure)
    {
        var claims = ClaimsService();
        AiExaminationDto? written = null;
        claims.SetAiExaminationAsync(default!, default!, default!, default)
            .ReturnsForAnyArgs(call => { written = call.ArgAt<AiExaminationDto>(2); return true; });
        var anthropic = Substitute.For<IAnthropicClient>();
        anthropic.CallWithToolAsync(default!, default!, default!, default).ThrowsAsyncForAnyArgs(failure);

        await Orchestrator(claims, anthropic).ProcessAsync(NcciEvent(), CancellationToken.None);

        Assert.Equal(AiDispositions.EscalateToHuman, written?.RecommendedDisposition);
    }

    [Fact]
    public async Task HostShutdown_DuringTheAnthropicCall_IsNeitherTransientNorAnEscalation()
    {
        var claims = ClaimsService();
        using var shutdown = new CancellationTokenSource();
        shutdown.Cancel();
        var anthropic = Substitute.For<IAnthropicClient>();
        anthropic.CallWithToolAsync(default!, default!, default!, default)
            .ThrowsAsyncForAnyArgs(new TaskCanceledException("canceled", null, shutdown.Token));

        var act = () => Orchestrator(claims, anthropic).ProcessAsync(NcciEvent(), shutdown.Token);

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
        Assert.IsNotType<ExaminerTransientException>(thrown);
        await claims.DidNotReceiveWithAnyArgs().SetAiExaminationAsync(default!, default!, default!, default);
    }

    // ── Through the real Anthropic client and shared resilience ───────

    private static (ExaminerHostFactory Factory, IClaimsServiceClient Claims, ManualClock Clock) Host(
        Func<int, CancellationToken, Task<HttpResponseMessage>> anthropic, ManualClock? clock = null)
    {
        clock ??= new ManualClock();
        var claims = ClaimsService();
        var factory = new ExaminerHostFactory(anthropic,
            new Dictionary<string, string> { ["Anthropic:ApiKey"] = "test-key" },
            services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton(claims);
            });
        return (factory, claims, clock);
    }

    private static async Task<Exception?> Examine(ExaminerHostFactory factory, ManualClock clock)
    {
        using var scope = factory.Services.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IExaminerOrchestrator>();
        try
        {
            // Retry delays (2 s, 4 s, 8 s) run on the manual clock.
            await clock.Drive(orchestrator.ProcessAsync(NcciEvent(), CancellationToken.None), step: TimeSpan.FromMilliseconds(500));
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Theory]
    [InlineData(429)]
    [InlineData(529)]
    public async Task ABurstThatOutlastsTheClientsRetries_IsTransient(int status)
    {
        var (factory, claims, clock) = Host((_, _) => Task.FromResult(ExaminerHostFactory.Status(status)));
        await using var _ = factory;

        var failure = await Examine(factory, clock);

        Assert.IsType<ExaminerTransientException>(failure);
        Assert.Equal(4, factory.AnthropicCalls); // the client's 3 retries ran first
        await claims.DidNotReceiveWithAnyArgs().SetAiExaminationAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task AnOpenCircuit_IsTransient()
    {
        var (factory, claims, clock) = Host((_, _) => Task.FromResult(ExaminerHostFactory.Status(429)));
        await using var _ = factory;

        // Two rate-limited examinations open the circuit (5 failures in its window)...
        await Examine(factory, clock);
        await Examine(factory, clock);
        var callsWhenOpen = factory.AnthropicCalls;

        // ...and the next one is refused without calling Anthropic.
        var failure = await Examine(factory, clock);

        Assert.Equal(callsWhenOpen, factory.AnthropicCalls);
        var transient = Assert.IsType<ExaminerTransientException>(failure);
        Assert.IsType<BrokenCircuitException>(transient.InnerException?.InnerException);
        await claims.DidNotReceiveWithAnyArgs().SetAiExaminationAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task ATimedOutModelCall_IsTransient()
    {
        var clock = new ManualClock();
        var (factory, claims, _) = Host((_, ct) =>
        {
            clock.Advance(TimeSpan.FromSeconds(61)); // past the 60 s model timeout
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ExaminerHostFactory.ToolUse());
        }, clock);
        await using var _ = factory;

        var failure = await Examine(factory, clock);

        var transient = Assert.IsType<ExaminerTransientException>(failure);
        Assert.IsType<TimeoutException>(transient.InnerException?.InnerException);
        await claims.DidNotReceiveWithAnyArgs().SetAiExaminationAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task ARejectedRequest_StillEscalatesToAHuman()
    {
        var (factory, claims, clock) = Host((_, _) => Task.FromResult(ExaminerHostFactory.Status(400)));
        await using var _ = factory;

        var failure = await Examine(factory, clock);

        Assert.Null(failure);
        Assert.Equal(1, factory.AnthropicCalls); // a 400 is not retried
        await claims.Received(1).SetAiExaminationAsync("claim-1", "tenant-a",
            Arg.Is<AiExaminationDto>(d => d.RecommendedDisposition == AiDispositions.EscalateToHuman), Arg.Any<CancellationToken>());
    }
}
