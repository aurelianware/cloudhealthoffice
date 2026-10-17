using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClaimsExaminerService.Services.Anthropic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Xunit;

namespace CloudHealthOffice.ClaimsExaminerService.Tests.Services;

/// <summary>
/// claims-examiner-service as Program.cs builds it, with only the primary
/// handler of the Anthropic client replaced by a scripted fake.
/// </summary>
public sealed class ExaminerHostFactory : WebApplicationFactory<Program>
{
    private readonly Func<int, CancellationToken, Task<HttpResponseMessage>> _anthropic;
    private readonly IReadOnlyDictionary<string, string> _settings;
    private int _calls;

    /// <param name="anthropic">Answers the n-th (1-based) call to the Messages API.</param>
    /// <param name="settings">Configuration Program.cs reads while it builds its services.</param>
    public ExaminerHostFactory(
        Func<int, CancellationToken, Task<HttpResponseMessage>> anthropic, IReadOnlyDictionary<string, string>? settings = null)
    {
        _anthropic = anthropic;
        _settings = settings ?? new Dictionary<string, string>();
    }

    public int AnthropicCalls => Volatile.Read(ref _calls);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        // Program.cs reads the Anthropic section before the host is built.
        foreach (var (key, value) in _settings) builder.UseSetting(key, value);
        builder.ConfigureServices(services =>
        {
            foreach (var hosted in services
                         .Where(d => d.ServiceType == typeof(IHostedService)
                                     && d.ImplementationType?.Namespace?.StartsWith("ClaimsExaminerService") == true)
                         .ToList())
            {
                services.Remove(hosted);
            }
            services.Configure<HttpClientFactoryOptions>(nameof(IAnthropicClient),
                o => o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new Scripted(this)));
        });
    }

    private sealed class Scripted(ExaminerHostFactory owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            owner._anthropic(Interlocked.Increment(ref owner._calls), ct);
    }

    public static HttpResponseMessage ToolUse() => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new
        {
            model = "claude-test",
            usage = new { input_tokens = 10, output_tokens = 5 },
            content = new object[]
            {
                new { type = "tool_use", name = "recommend_disposition", input = new { recommended_disposition = "Approve" } },
            },
        }),
    };

    public static HttpResponseMessage Status(int status, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("{}") };
        if (retryAfter is { } wait) response.Headers.RetryAfter = new RetryConditionHeaderValue(wait);
        return response;
    }

    public static readonly AnthropicTool Tool = new("recommend_disposition", "test", new JsonObject { ["type"] = "object" });
}

/// <summary>
/// The Anthropic client's timeouts. A rate-limited call that waits out its
/// Retry-After and then gets a slow answer succeeds: the model timeout bounds
/// one attempt, not every attempt together.
/// <para>
/// REAL TIME: HttpClient.Timeout runs on real time, so this test waits about
/// 2.5 s. It uses a 2 s model timeout to stay that short.
/// </para>
/// </summary>
public class AnthropicClientResilienceTests
{
    [Fact]
    public async Task A429WithRetryAfter_ThenASlowSuccess_Succeeds()
    {
        // Call 1: 429, Retry-After 1 s. Call 2: answers after 1.5 s, inside the
        // 2 s model timeout, but 2.5 s after the call began.
        await using var factory = new ExaminerHostFactory(
            async (n, ct) =>
            {
                if (n == 1) return ExaminerHostFactory.Status(429, TimeSpan.FromSeconds(1));
                await Task.Delay(TimeSpan.FromMilliseconds(1500), ct);
                return ExaminerHostFactory.ToolUse();
            },
            new Dictionary<string, string> { ["Anthropic:TimeoutSeconds"] = "2", ["Anthropic:ApiKey"] = "test-key" });
        var client = factory.Services.GetRequiredService<IAnthropicClient>();

        var watch = Stopwatch.StartNew();
        var result = await client.CallWithToolAsync("system", "user", ExaminerHostFactory.Tool, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("recommend_disposition", result!.ToolName);
        Assert.Equal(2, factory.AnthropicCalls);
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(2), "the Retry-After wait and the slow answer both happened");
    }
}
