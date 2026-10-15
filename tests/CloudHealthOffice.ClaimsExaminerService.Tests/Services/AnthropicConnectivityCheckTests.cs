using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ClaimsExaminerService.Services.Anthropic;
using Xunit;

namespace CloudHealthOffice.ClaimsExaminerService.Tests.Services;

/// <summary>
/// The deploy-time check behind <c>--check-anthropic</c>: one real call, and a
/// failure message that says why (missing key, bad key, retired model, no network).
/// </summary>
public class AnthropicConnectivityCheckTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return _respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static AnthropicConnectivityCheck Check(StubHandler handler, string model = "claude-test") =>
        new(new HttpClient(handler), new AnthropicOptions { Model = model, BaseUrl = "https://anthropic.test" });

    [Fact]
    public async Task Success_MakesOneMinimalCallWithTheConfiguredModel()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"model":"claude-test","content":[]}"""));

        var result = await Check(handler).RunAsync("sk-test", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("claude-test", result.Message);
        Assert.Equal("https://anthropic.test/v1/messages", handler.Request!.RequestUri!.ToString());
        Assert.True(handler.Request.Headers.Contains("anthropic-version"));
        var body = JsonNode.Parse(handler.Body!)!;
        Assert.Equal("claude-test", body["model"]!.GetValue<string>());
        Assert.Equal(1, body["max_tokens"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-configured")]
    public async Task MissingKey_FailsWithoutCalling(string? key)
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("must not call"));

        var result = await Check(handler).RunAsync(key, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("ApiKey is not set", result.Message);
        Assert.Null(handler.Request);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "authentication_error", "invalid x-api-key")]
    [InlineData(HttpStatusCode.NotFound, "not_found_error", "model: claude-test")]
    [InlineData(HttpStatusCode.BadRequest, "invalid_request_error", "Your credit balance is too low")]
    public async Task ApiError_FailsAndSaysWhy(HttpStatusCode status, string type, string message)
    {
        var handler = new StubHandler(_ => Json(status,
            new JsonObject
            {
                ["type"] = "error",
                ["error"] = new JsonObject { ["type"] = type, ["message"] = message },
            }.ToJsonString()));

        var result = await Check(handler).RunAsync("sk-test", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains(((int)status).ToString(), result.Message);
        Assert.Contains(type, result.Message);
        Assert.Contains(message, result.Message);
        Assert.DoesNotContain("sk-test", result.Message);
    }

    [Fact]
    public async Task NetworkFailure_FailsWithReason()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("Name or service not known"));

        var result = await Check(handler).RunAsync("sk-test", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("unreachable", result.Message);
        Assert.Contains("Name or service not known", result.Message);
    }
}
