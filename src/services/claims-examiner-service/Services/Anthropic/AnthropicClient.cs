using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ClaimsExaminerService.Services.Anthropic;

public interface IAnthropicClient
{
    /// <summary>
    /// Send a single message to Claude with a forced tool-use response.
    /// The model is required to call <paramref name="tool"/>; the parsed
    /// tool-call arguments are returned as a JsonNode for the orchestrator
    /// to project into its domain shape.
    ///
    /// Throws on transport / HTTP errors. Returns null when the model
    /// declined to call the tool (rare, but possible — caller should
    /// fall back to EscalateToHuman).
    /// </summary>
    Task<AnthropicToolResult?> CallWithToolAsync(
        string systemPrompt,
        string userMessage,
        AnthropicTool tool,
        CancellationToken ct);
}

/// <summary>
/// Tool definition handed to Claude. The model is forced to call exactly this
/// tool, which is how we get strict structured output without parsing free text.
/// </summary>
public class AnthropicTool
{
    public string Name { get; }
    public string Description { get; }
    public JsonObject InputSchema { get; }

    public AnthropicTool(string name, string description, JsonObject inputSchema)
    {
        Name = name;
        Description = description;
        InputSchema = inputSchema;
    }
}

public class AnthropicToolResult
{
    public string ToolName { get; init; } = string.Empty;
    public JsonNode Arguments { get; init; } = new JsonObject();
    public string ModelId { get; init; } = string.Empty;
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
}

public class AnthropicOptions
{
    public string BaseUrl { get; set; } = "https://api.anthropic.com";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "claude-opus-4-6";
    public int MaxTokens { get; set; } = 2048;
    public int TimeoutSeconds { get; set; } = 60;
    public string AnthropicVersion { get; set; } = "2023-06-01";
}

public class AnthropicClient : IAnthropicClient
{
    private readonly HttpClient _http;
    private readonly AnthropicOptions _options;
    private readonly ILogger<AnthropicClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AnthropicClient(HttpClient http, AnthropicOptions options, ILogger<AnthropicClient> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;

        _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        _http.DefaultRequestHeaders.Add("anthropic-version", _options.AnthropicVersion);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<AnthropicToolResult?> CallWithToolAsync(
        string systemPrompt,
        string userMessage,
        AnthropicTool tool,
        CancellationToken ct)
    {
        // The Messages API request body. Forcing tool_choice to the named tool
        // guarantees the model returns a structured tool_use block; we never
        // have to parse free-text JSON out of an assistant turn.
        var requestBody = new
        {
            model = _options.Model,
            max_tokens = _options.MaxTokens,
            system = systemPrompt,
            messages = new[]
            {
                new { role = "user", content = userMessage }
            },
            tools = new[]
            {
                new
                {
                    name = tool.Name,
                    description = tool.Description,
                    input_schema = tool.InputSchema
                }
            },
            tool_choice = new { type = "tool", name = tool.Name }
        };

        using var response = await _http.PostAsJsonAsync("v1/messages", requestBody, JsonOptions, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError(
                "Anthropic API error: {Status} {Body}",
                response.StatusCode, errorBody);
            response.EnsureSuccessStatusCode();
        }

        var payload = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct);
        if (payload is null)
        {
            _logger.LogWarning("Anthropic API returned empty body");
            return null;
        }

        var modelId = payload["model"]?.GetValue<string>() ?? _options.Model;
        var usage = payload["usage"];
        var inputTokens = usage?["input_tokens"]?.GetValue<int>() ?? 0;
        var outputTokens = usage?["output_tokens"]?.GetValue<int>() ?? 0;

        // Walk the content blocks for the tool_use the model produced. Forced
        // tool_choice means there should always be exactly one — we still
        // tolerate the model declining (returns null), since the alternative
        // is throwing on a path the orchestrator already handles cleanly.
        if (payload["content"] is not JsonArray content)
        {
            _logger.LogWarning("Anthropic response missing content array");
            return null;
        }

        foreach (var block in content)
        {
            if (block is null) continue;
            var type = block["type"]?.GetValue<string>();
            if (type != "tool_use") continue;

            var toolName = block["name"]?.GetValue<string>() ?? string.Empty;
            var input = block["input"];
            if (input is null) continue;

            return new AnthropicToolResult
            {
                ToolName = toolName,
                Arguments = input.DeepClone(),
                ModelId = modelId,
                InputTokens = inputTokens,
                OutputTokens = outputTokens
            };
        }

        _logger.LogWarning(
            "Anthropic response did not contain a tool_use block (stop_reason={StopReason})",
            payload["stop_reason"]?.GetValue<string>());
        return null;
    }
}
