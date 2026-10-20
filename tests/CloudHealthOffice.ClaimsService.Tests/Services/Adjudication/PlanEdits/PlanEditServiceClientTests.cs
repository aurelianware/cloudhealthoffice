using System.Net;
using System.Text;
using ClaimsService.Services.Adjudication.PlanEdits;
using CloudHealthOffice.Infrastructure.Http;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.PlanEdits.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.PlanEdits;

/// <summary>
/// The HTTP side of the plan edit service: the signed request, what counts as
/// an answer, and every way an answer can be unusable. Runs through the real
/// registration (named client, CHO resilience) with a stub transport.
/// </summary>
public class PlanEditServiceClientTests
{
    private const string Tenant = "tenant-1";
    private const string Secret = "plan-edit-secret-with-at-least-32-characters";
    private const string Endpoint = "https://edits.plan.example/v1/claims";
    private static readonly int[] Lines = [1, 2];

    private static readonly PlanEditRequest Request = new()
    {
        RequestId = "req-1",
        TenantId = Tenant,
        Claim = new PlanEditClaim { ClaimId = "c1", ClaimVersionId = "v1", MemberId = "M1" },
    };

    [Fact]
    public async Task The_request_is_a_signed_json_post_with_the_request_id_and_no_authorization_header_of_its_own()
    {
        var stub = new Stub(_ => Json("""{"contractVersion":"1","edits":[]}"""));
        var client = Build(stub);

        var result = await client.EvaluateAsync(Tenant, Request, Lines, CancellationToken.None);

        Assert.IsType<PlanEditCallResult.Answered>(result);
        var sent = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal(Endpoint, sent.Uri);
        Assert.Equal("application/json", sent.ContentType);
        Assert.Equal("req-1", sent.Headers[PlanEditContract.RequestIdHeader]);
        Assert.Equal("req-1", sent.Headers[PlanEditContract.IdempotencyKeyHeader]);
        Assert.False(sent.Headers.ContainsKey("Authorization"));
        Assert.True(PlanEditSignature.Verify(Secret, sent.Headers[PlanEditContract.TimestampHeader],
            sent.Headers[PlanEditContract.SignatureHeader], sent.Body, DateTimeOffset.UtcNow));
        Assert.Contains("\"requestId\":\"req-1\"", Encoding.UTF8.GetString(sent.Body));
    }

    [Fact]
    public async Task A_valid_response_returns_its_edits()
    {
        var client = Build(new Stub(_ => Json("""
            {"contractVersion":"1","edits":[
              {"ruleId":"PLAN-NC-7","message":"Not covered","lineNumbers":[1],"suggestedCarc":"96"},
              {"ruleId":"PLAN-HIDOLLAR","message":"Review"}]}
            """)));

        var result = await client.EvaluateAsync(Tenant, Request, Lines, CancellationToken.None);

        var answered = Assert.IsType<PlanEditCallResult.Answered>(result);
        Assert.Equal(["PLAN-NC-7", "PLAN-HIDOLLAR"], answered.Edits.Select(e => e.RuleId));
    }

    [Fact]
    public async Task A_server_error_is_retried_once_then_reported_as_http_status()
    {
        var stub = new Stub(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var result = await Build(stub).EvaluateAsync(Tenant, Request, Lines, CancellationToken.None);

        Assert.Equal(PlanEditUnavailableKinds.HttpStatus, Assert.IsType<PlanEditCallResult.Unavailable>(result).Kind);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Anything_but_200_is_no_answer(HttpStatusCode status)
    {
        var stub = new Stub(_ =>
        {
            var response = new HttpResponseMessage(status);
            if (status == HttpStatusCode.Found) response.Headers.Location = new Uri("https://elsewhere.example/");
            return response;
        });

        var result = await Build(stub).EvaluateAsync(Tenant, Request, Lines, CancellationToken.None);

        Assert.Equal(PlanEditUnavailableKinds.HttpStatus, Assert.IsType<PlanEditCallResult.Unavailable>(result).Kind);
        Assert.All(stub.Requests, r => Assert.Equal(Endpoint, r.Uri));
    }

    [Fact]
    public async Task A_connection_failure_is_unreachable()
    {
        var stub = new Stub(_ => throw new HttpRequestException("refused", null, null));

        var result = await Build(stub).EvaluateAsync(Tenant, Request, Lines, CancellationToken.None);

        Assert.Equal(PlanEditUnavailableKinds.Unreachable, Assert.IsType<PlanEditCallResult.Unavailable>(result).Kind);
    }

    [Fact]
    public async Task A_service_slower_than_the_timeout_is_a_timeout()
    {
        var stub = new Stub(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return Json("""{"contractVersion":"1","edits":[]}""");
        });

        var result = await Build(stub, timeoutMs: 200).EvaluateAsync(Tenant, Request, Lines, CancellationToken.None);

        Assert.Equal(PlanEditUnavailableKinds.Timeout, Assert.IsType<PlanEditCallResult.Unavailable>(result).Kind);
    }

    [Fact]
    public async Task A_service_that_sends_headers_then_stalls_the_body_is_a_timeout_within_the_budget()
    {
        var stub = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream()) { Headers = { ContentType = new("application/json") } },
        });
        var started = System.Diagnostics.Stopwatch.StartNew();

        var result = await Build(stub, timeoutMs: 200).EvaluateAsync(Tenant, Request, Lines, CancellationToken.None);

        Assert.Equal(PlanEditUnavailableKinds.Timeout, Assert.IsType<PlanEditCallResult.Unavailable>(result).Kind);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(3), $"took {started.Elapsed}");
    }

    [Fact]
    public async Task The_callers_cancellation_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        var stub = new Stub(async (_, ct) =>
        {
            cts.Cancel();
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return Json("{}");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Build(stub).EvaluateAsync(Tenant, Request, Lines, cts.Token));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"contractVersion":"2","edits":[]}""")]
    [InlineData("""{"contractVersion":"1"}""")]
    [InlineData("""{"contractVersion":"1","edits":[{"ruleId":"R1","message":"m","lineNumbers":[9]}]}""")]
    [InlineData("""{"contractVersion":"1","edits":[{"ruleId":"R1","message":"m","lineNumbers":["1"]}]}""")]
    public async Task A_response_that_breaks_the_contract_is_invalid_never_no_edits(string body)
    {
        var result = await Build(new Stub(_ => Json(body))).EvaluateAsync(Tenant, Request, Lines, CancellationToken.None);

        Assert.Equal(PlanEditUnavailableKinds.InvalidResponse, Assert.IsType<PlanEditCallResult.Unavailable>(result).Kind);
    }

    [Fact]
    public async Task The_invalid_response_detail_never_carries_the_plans_text()
    {
        var body = """{"contractVersion":"1","edits":[{"ruleId":"bad id","message":"Jane Doe 1980-01-01","lineNumbers":[9]}]}""";

        var result = await Build(new Stub(_ => Json(body))).EvaluateAsync(Tenant, Request, Lines, CancellationToken.None);

        var detail = Assert.IsType<PlanEditCallResult.Unavailable>(result).Detail;
        Assert.DoesNotContain("Jane", detail);
        Assert.DoesNotContain("1980", detail);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/problem+json")]
    public async Task A_content_type_other_than_application_json_is_invalid(string mediaType)
    {
        var stub = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"contractVersion":"1","edits":[]}""", Encoding.UTF8, mediaType),
        });

        var result = await Build(stub).EvaluateAsync(Tenant, Request, Lines, CancellationToken.None);

        Assert.Equal(PlanEditUnavailableKinds.InvalidResponse, Assert.IsType<PlanEditCallResult.Unavailable>(result).Kind);
    }

    [Fact]
    public async Task A_body_over_the_limit_is_refused_with_or_without_a_content_length()
    {
        var huge = "{\"contractVersion\":\"1\",\"edits\":[],\"pad\":\"" + new string('x', PlanEditContract.MaxResponseBytes) + "\"}";
        var declared = new Stub(_ => Json(huge));
        var chunked = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(huge)))
            {
                Headers = { ContentType = new("application/json") },
            },
        });

        var a = await Build(declared).EvaluateAsync(Tenant, Request, Lines, CancellationToken.None);
        var b = await Build(chunked).EvaluateAsync(Tenant, Request, Lines, CancellationToken.None);

        Assert.Equal(PlanEditUnavailableKinds.TooLarge, Assert.IsType<PlanEditCallResult.Unavailable>(a).Kind);
        Assert.Equal(PlanEditUnavailableKinds.TooLarge, Assert.IsType<PlanEditCallResult.Unavailable>(b).Kind);
    }

    [Fact]
    public void Only_configured_tenants_have_a_plan_edit_service()
    {
        var client = Build(new Stub(_ => Json("{}")));

        Assert.NotNull(client.For(Tenant));
        Assert.Null(client.For("tenant-2"));
        Assert.Null(client.For(""));
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    internal static IConfiguration Configuration(string endpoint = Endpoint, string secret = Secret, int timeoutMs = 2000) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"Adjudication:PlanEdits:Tenants:{Tenant}:Endpoint"] = endpoint,
            [$"Adjudication:PlanEdits:Tenants:{Tenant}:SigningSecret"] = secret,
            [$"Adjudication:PlanEdits:Tenants:{Tenant}:TimeoutMilliseconds"] = timeoutMs.ToString(),
        }).Build();

    private static IPlanEditServiceClient Build(Stub stub, int timeoutMs = 2000)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChoServiceClientResilience();
        services.AddPlanEditService(Configuration(timeoutMs: timeoutMs), new Env("Production"));
        services.AddHttpClient(PlanEditServiceClient.ClientName(Tenant)).ConfigurePrimaryHttpMessageHandler(() => stub);
        return services.BuildServiceProvider().GetRequiredService<IPlanEditServiceClient>();
    }

    internal sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "claims-service";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    /// <summary>A body that never yields a byte until cancelled.</summary>
    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    }

    private sealed record Sent(HttpMethod Method, string Uri, string? ContentType, Dictionary<string, string> Headers, byte[] Body);

    private sealed class Stub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = (r, _) => Task.FromResult(respond(r));
        public Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        public List<Sent> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
            Requests.Add(new Sent(
                request.Method,
                request.RequestUri!.ToString(),
                request.Content?.Headers.ContentType?.MediaType,
                request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)),
                body));
            return await _respond(request, ct);
        }
    }
}
