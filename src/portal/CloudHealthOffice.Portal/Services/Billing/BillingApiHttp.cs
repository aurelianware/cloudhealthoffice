using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Driver;
using CloudHealthOffice.Infrastructure.Logging;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// Request/response handling shared by the premium billing and sponsor
/// bank-account clients: camelCase JSON with string enums, a refusal turned
/// into <see cref="BillingApiException"/> carrying the service's reason, and
/// an unreachable service into <see cref="ServiceUnavailableException"/>.
/// </summary>
internal static class BillingApiHttp
{
    internal static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static async Task<T> SendAsync<T>(HttpClient client, ILogger logger, string serviceName,
        HttpMethod method, string url, object? body)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(method, url);
            if (body != null)
                request.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions);
            response = await client.SendAsync(request);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Service unavailable: {ServiceName}", serviceName);
            throw new ServiceUnavailableException(serviceName, ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                var (title, detail) = ParseError(text);
                logger.LogWarning("{ServiceName} refused {Method} {Path}: {Status} {Title}",
                    serviceName, method, LogSanitizer.Sanitize(new Uri(url).AbsolutePath), (int)response.StatusCode, LogSanitizer.Sanitize(title));
                throw new BillingApiException((int)response.StatusCode, title, detail) { Body = text };
            }

            if (string.IsNullOrWhiteSpace(text)) return default!;
            return JsonSerializer.Deserialize<T>(text, JsonOptions)!;
        }
    }

    public static T? TryDeserialize<T>(string text) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(text, JsonOptions); }
        catch (JsonException) { return null; }
    }


    /// <summary>
    /// The services refuse with ProblemDetails (<c>title</c>, <c>detail</c>),
    /// <c>{ error }</c>, <c>{ error, errors: [...] }</c> (bank-account
    /// validation) or ValidationProblemDetails (<c>errors: { field: [...] }</c>).
    /// </summary>
    private static (string? Title, string? Detail) ParseError(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, Truncate(text));

            string? Str(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            var title = Str("title");
            var detail = Str("detail") ?? Str("error") ?? Str("message");
            if (root.TryGetProperty("errors", out var errors))
            {
                var items = errors.ValueKind switch
                {
                    JsonValueKind.Array => errors.EnumerateArray().Select(e => e.ToString()).ToList(),
                    JsonValueKind.Object => errors.EnumerateObject().SelectMany(p =>
                        p.Value.ValueKind == JsonValueKind.Array
                            ? p.Value.EnumerateArray().Select(e => e.ToString())
                            : new[] { p.Value.ToString() }).ToList(),
                    _ => new List<string>()
                };
                var joined = string.Join(" ", items.Where(s => !string.IsNullOrWhiteSpace(s)));
                if (joined.Length > 0) detail = string.IsNullOrEmpty(detail) ? joined : $"{detail}: {joined}";
            }
            return (title, detail);
        }
        catch (JsonException)
        {
            return (null, Truncate(text));
        }
    }

    private static string Truncate(string text) => text.Length > 300 ? text[..300] : text;
}
