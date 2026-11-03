using System.Text.Json;

namespace CloudHealthOffice.Portal.Services;

/// <summary>Outcome of submitting a corrected claim as an adjustment.</summary>
public sealed class ClaimCorrectionResult
{
    public bool Success { get; private init; }

    /// <summary>The adjustment record's id.</summary>
    public string AdjustmentId { get; private init; } = string.Empty;

    /// <summary>The new claim version the correction created; adjudication runs on it.</summary>
    public string NewClaimId { get; private init; } = string.Empty;

    /// <summary>Why the service refused the correction, in its own words.</summary>
    public IReadOnlyList<string> Errors { get; private init; } = Array.Empty<string>();

    public static ClaimCorrectionResult Ok(string adjustmentId, string newClaimId) =>
        new() { Success = true, AdjustmentId = adjustmentId, NewClaimId = newClaimId };

    public static ClaimCorrectionResult Fail(IEnumerable<string> errors) =>
        new() { Success = false, Errors = errors.ToList() };

    /// <summary>
    /// Reads the service's refusal: <c>{ error, errors: [{ field, message }] }</c>
    /// for business refusals, or ProblemDetails <c>errors: { field: [..] }</c>
    /// for model validation. Never echoes the request body.
    /// </summary>
    internal static IReadOnlyList<string> ReadErrors(string body, int status)
    {
        var messages = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                    messages.Add(error.GetString()!);
                else if (root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                    messages.Add(title.GetString()!);

                if (root.TryGetProperty("errors", out var errors))
                {
                    if (errors.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var e in errors.EnumerateArray())
                        {
                            var field = e.TryGetProperty("field", out var f) ? f.GetString() : null;
                            var message = e.TryGetProperty("message", out var m) ? m.GetString() : null;
                            if (!string.IsNullOrWhiteSpace(message))
                                messages.Add(string.IsNullOrWhiteSpace(field) ? message! : $"{field}: {message}");
                        }
                    }
                    else if (errors.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var p in errors.EnumerateObject())
                        {
                            if (p.Value.ValueKind != JsonValueKind.Array) continue;
                            foreach (var m in p.Value.EnumerateArray())
                            {
                                if (m.ValueKind == JsonValueKind.String) messages.Add($"{p.Name}: {m.GetString()}");
                            }
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON: fall through to the generic message.
        }

        if (messages.Count == 0)
            messages.Add($"The correction wasn't accepted ({status}).");
        return messages;
    }
}
