using System.Net;
using System.Text.Json;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// claims-service refused an examiner's resolution (400 / 403 / 409) — e.g.
/// a COB pend approved without a payer order, a new pend found on the
/// approval re-run, a missing permission. Its <see cref="Exception.Message"/>
/// and <see cref="Reasons"/> are the service's own explanation, shown to the
/// examiner — not "service unavailable" (PR #1278 round 3, M5).
/// </summary>
public sealed class ClaimResolutionRefusedException : Exception
{
    public HttpStatusCode StatusCode { get; }

    /// <summary>Every unresolved reason the service listed (may be empty).</summary>
    public IReadOnlyList<string> Reasons { get; }

    public ClaimResolutionRefusedException(HttpStatusCode statusCode, string message, IReadOnlyList<string> reasons)
        : base(message)
    {
        StatusCode = statusCode;
        Reasons = reasons;
    }

    /// <summary>
    /// Reads the refusal body: <c>{ error, reasons }</c>, a ProblemDetails
    /// (<c>title</c> / <c>detail</c>), or plain text.
    /// </summary>
    public static ClaimResolutionRefusedException From(HttpStatusCode statusCode, string? body)
    {
        var message = $"The claim was not resolved ({(int)statusCode}).";
        var reasons = new List<string>();
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                        message = error.GetString()!;
                    else if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                        message = detail.GetString()!;
                    else if (root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                        message = title.GetString()!;
                    if (root.TryGetProperty("reasons", out var list) && list.ValueKind == JsonValueKind.Array)
                        reasons.AddRange(list.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.String).Select(r => r.GetString()!));
                }
                else if (root.ValueKind == JsonValueKind.String)
                {
                    message = root.GetString()!;
                }
            }
            catch (JsonException)
            {
                message = body.Trim();
            }
        }
        return new ClaimResolutionRefusedException(statusCode, message, reasons);
    }

    /// <summary>The message and every reason, for a snackbar.</summary>
    public string ToDisplayText() =>
        Reasons.Count == 0 ? Message : $"{Message} — {string.Join("; ", Reasons)}";
}

/// <summary>Outcome of an examiner resolution the service accepted.</summary>
public sealed record ClaimResolutionResult(bool AwaitingSecondApproval, string? Message)
{
    public static readonly ClaimResolutionResult Resolved = new(false, null);
}
