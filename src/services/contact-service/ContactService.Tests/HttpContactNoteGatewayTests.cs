using System.Net;
using System.Text.Json.Nodes;
using ContactService.Models;
using ContactService.Services.Claims;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContactService.Tests;

/// <summary>
/// The claim-note gateway's contract: it writes a readable note that names
/// the contact, it carries the contact's own tenant, and it never throws.
/// The contact is the record of the conversation, so a claims-service that
/// is down costs the claim its cross-reference and nothing else.
/// </summary>
public class HttpContactNoteGatewayTests
{
    private static ContactNoteCommand Command(
        ContactSubjectType type = ContactSubjectType.Claim,
        CallerType caller = CallerType.Member,
        string? note = "why did line 2 deny") =>
        new("t1", type, "claim-7", caller, "C-20261022-AAAA1111", note);

    private static (HttpContactNoteGateway Gateway, StubHandler Handler) Build(
        HttpStatusCode status = HttpStatusCode.Created, string? body = null, Exception? throws = null)
    {
        var handler = new StubHandler(status, body, throws);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(HttpContactNoteGateway.HttpClientName))
               .Returns(() => new HttpClient(handler) { BaseAddress = new Uri("http://claims-service/") });

        return (new HttpContactNoteGateway(factory.Object, NullLogger<HttpContactNoteGateway>.Instance), handler);
    }

    [Fact]
    public async Task A_written_note_returns_its_id()
    {
        var (gateway, _) = Build(body: """{"id":"note-9"}""");

        (await gateway.WriteContactNoteAsync(Command())).Should().Be("note-9");
    }

    [Fact]
    public async Task The_note_goes_on_the_claim_that_was_discussed()
    {
        var (gateway, handler) = Build(body: """{"id":"note-9"}""");

        await gateway.WriteContactNoteAsync(Command());

        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/api/claims/claim-7/notes");
        handler.LastRequest.Headers.GetValues("X-Tenant-ID").Should().Equal("t1");
    }

    [Fact]
    public async Task The_note_names_the_contact_and_what_was_asked()
    {
        var (gateway, handler) = Build(body: """{"id":"note-9"}""");

        await gateway.WriteContactNoteAsync(Command());

        var body = JsonNode.Parse(handler.LastBody!)!;
        body["category"]!.GetValue<string>().Should().Be("MemberContact");
        body["text"]!.GetValue<string>().Should()
            .Be("Member asked about this claim on contact C-20261022-AAAA1111: why did line 2 deny");
    }

    [Fact]
    public async Task A_provider_call_is_a_provider_contact_note()
    {
        var (gateway, handler) = Build(body: """{"id":"note-9"}""");

        await gateway.WriteContactNoteAsync(Command(caller: CallerType.Provider, note: null));

        var body = JsonNode.Parse(handler.LastBody!)!;
        body["category"]!.GetValue<string>().Should().Be("ProviderContact");
        body["text"]!.GetValue<string>().Should()
            .Be("Provider asked about this claim on contact C-20261022-AAAA1111.");
    }

    [Theory]
    [InlineData(ContactSubjectType.Authorization)]
    [InlineData(ContactSubjectType.Appeal)]
    [InlineData(ContactSubjectType.PaymentRun)]
    [InlineData(ContactSubjectType.Document)]
    public async Task Only_claims_take_a_contact_note_today(ContactSubjectType type)
    {
        var (gateway, handler) = Build(body: """{"id":"note-9"}""");

        (await gateway.WriteContactNoteAsync(Command(type))).Should().BeNull();
        handler.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task A_rejected_write_reports_no_note_rather_than_throwing()
    {
        // A rep whose role does not hold claims:note, for instance.
        var (gateway, _) = Build(HttpStatusCode.Forbidden);

        (await gateway.WriteContactNoteAsync(Command())).Should().BeNull();
    }

    [Fact]
    public async Task A_claims_service_that_is_down_reports_no_note_rather_than_throwing()
    {
        var (gateway, _) = Build(throws: new HttpRequestException("connection refused"));

        (await gateway.WriteContactNoteAsync(Command())).Should().BeNull();
    }

    [Fact]
    public async Task A_cancelled_call_still_cancels()
    {
        var (gateway, _) = Build();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await gateway.Invoking(g => g.WriteContactNoteAsync(Command(), cancelled.Token))
                     .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_subject_id_carrying_newlines_cannot_forge_a_log_line()
    {
        // The subject id comes from the request body. CodeQL flagged it
        // reaching the logger unsanitised; LogSanitizer replaces the control
        // characters that would otherwise let a caller write their own lines.
        var logger = new CapturingLogger();
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(HttpContactNoteGateway.HttpClientName))
               .Returns(() => new HttpClient(new StubHandler(HttpStatusCode.Forbidden, null, null))
               {
                   BaseAddress = new Uri("http://claims-service/"),
               });

        var gateway = new HttpContactNoteGateway(factory.Object, logger);

        await gateway.WriteContactNoteAsync(new ContactNoteCommand(
            "t1", ContactSubjectType.Claim, "claim-7\nFATAL everything is fine",
            CallerType.Member, "C-1", null));

        logger.Messages.Should().ContainSingle()
              .Which.Should().NotContain("\n").And.Contain("claim-7_FATAL");
    }

    /// <summary>Keeps the formatted log messages so a test can read them.</summary>
    private sealed class CapturingLogger : ILogger<HttpContactNoteGateway>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task A_very_long_note_is_cut_to_what_claims_service_accepts()
    {
        var (gateway, handler) = Build(body: """{"id":"note-9"}""");

        await gateway.WriteContactNoteAsync(Command(note: new string('x', 5000)));

        JsonNode.Parse(handler.LastBody!)!["text"]!.GetValue<string>().Length.Should().Be(4000);
    }

    private sealed class StubHandler(HttpStatusCode status, string? body, Exception? throws) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            if (throws is not null) throw throws;

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body ?? "{}", System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
