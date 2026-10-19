using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace ClaimsExaminerService.Services.Anthropic;

/// <summary>
/// One tiny, real Messages API call with the service's own configuration (key,
/// model, base URL, network path). Deploys run it after rollout
/// (<c>dotnet claims-examiner-service.dll --check-anthropic</c> inside the pod)
/// so an examiner that is "enabled" but cannot reach Claude, because the key is
/// revoked, out of credit or mistyped, or the model id is retired, fails the
/// deploy instead of failing every examination quietly.
/// </summary>
public sealed class AnthropicConnectivityCheck
{
    public const string CommandLineFlag = "--check-anthropic";

    private readonly HttpClient _http;
    private readonly AnthropicOptions _options;

    public AnthropicConnectivityCheck(HttpClient http, AnthropicOptions options)
    {
        _http = http;
        _options = options;
        // HttpClient.Timeout is set at registration (Program.cs), where the shared resilience reads it.
        _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Remove("anthropic-version");
        _http.DefaultRequestHeaders.Add("anthropic-version", _options.AnthropicVersion);
    }

    public sealed record Result(bool Ok, string Message);

    public async Task<Result> RunAsync(string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "not-configured")
            return new Result(false, "Anthropic:ApiKey is not set");

        var body = new
        {
            model = _options.Model,
            max_tokens = 1,
            messages = new[] { new { role = "user", content = "ping" } },
        };

        try
        {
            using var response = await _http.PostAsJsonAsync("v1/messages", body, ct);
            if (response.IsSuccessStatusCode)
            {
                var payload = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct);
                var model = payload?["model"]?.GetValue<string>() ?? _options.Model;
                return new Result(true, $"Anthropic reachable; model {model} answered");
            }

            var error = await response.Content.ReadAsStringAsync(ct);
            return new Result(false,
                $"Anthropic returned {(int)response.StatusCode} {response.StatusCode} for model {_options.Model}: {Describe(error)}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new Result(false, $"Anthropic unreachable at {_options.BaseUrl}: {ex.Message}");
        }
    }

    // The API's error JSON names the problem (authentication_error, not_found_error
    // for a retired model, credit balance); keep the message, never echo request data.
    private static string Describe(string errorBody)
    {
        try
        {
            var node = JsonNode.Parse(errorBody);
            var type = node?["error"]?["type"]?.GetValue<string>();
            var message = node?["error"]?["message"]?.GetValue<string>();
            if (type is not null || message is not null) return $"{type}: {message}";
        }
        catch (System.Text.Json.JsonException)
        {
            // Not the API's JSON error shape (e.g. a proxy page): report the truncated body instead.
        }
        return errorBody.Length > 300 ? errorBody[..300] : errorBody;
    }
}
