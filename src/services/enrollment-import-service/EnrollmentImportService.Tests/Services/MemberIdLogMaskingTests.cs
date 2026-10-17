using System.Collections.Concurrent;
using System.Net;
using EnrollmentImportService.Clients;
using Microsoft.Extensions.Logging;

namespace EnrollmentImportService.Tests.Services;

/// <summary>
/// Member ids reach the log only masked to their last four characters.
/// </summary>
public class MemberIdLogMaskingTests
{
    private const string MemberId = "SUB123456789";

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("rejected") });
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
    }

    private static HttpMemberServiceClient Client(ListLogger<HttpMemberServiceClient> logger)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(HttpMemberServiceClient.HttpClientName))
            .Returns(() => new HttpClient(new StatusHandler(HttpStatusCode.BadRequest))
            {
                BaseAddress = new Uri("http://member-service")
            });
        return new HttpMemberServiceClient(factory.Object, logger);
    }

    [Fact]
    public async Task RejectedUpdate_LogsOnlyTheLastFourCharactersOfTheMemberId()
    {
        var logger = new ListLogger<HttpMemberServiceClient>();

        var act = () => Client(logger).UpdateAsync("t1", MemberId, new UpdateMemberRequestDto());

        await act.Should().ThrowAsync<MemberServiceException>();
        var line = logger.Messages.Should().ContainSingle().Subject;
        line.Should().Contain("for ***6789:");
        line.Should().NotContain(MemberId);
    }

    [Fact]
    public async Task RejectedTermination_LogsOnlyTheLastFourCharactersOfTheMemberId()
    {
        var logger = new ListLogger<HttpMemberServiceClient>();

        var act = () => Client(logger).TerminateAsync("t1", MemberId, new TerminateMemberRequestDto
        {
            MemberId = MemberId,
            TerminationDate = new DateTime(2026, 1, 31),
        });

        await act.Should().ThrowAsync<MemberServiceException>();
        var line = logger.Messages.Should().ContainSingle().Subject;
        line.Should().Contain("for ***6789:");
        line.Should().NotContain(MemberId);
    }
}
