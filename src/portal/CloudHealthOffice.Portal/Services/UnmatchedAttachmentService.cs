using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// One 275 the payer is holding because no claim has announced its
/// attachment control number. It is linked automatically the moment a claim
/// does, and rejected with an 824 once the hold runs out.
/// </summary>
public sealed record HeldAttachment(
    string Id,
    string? AttachmentControlNumber,
    string ProviderId,
    string? ProviderName,
    string? PatientFirstName,
    string? PatientLastName,
    string DocumentType,
    DateTime ReceivedUtc,
    string? Notes)
{
    /// <summary>Whole days this has been held, as of <paramref name="asOfUtc"/>.</summary>
    public int DaysHeld(DateTime asOfUtc) => Math.Max(0, (int)(asOfUtc.Date - ReceivedUtc.Date).TotalDays);

    /// <summary>
    /// Days left before the hold expires and the 275 is rejected. Negative
    /// means the sweeper has not got to it yet.
    /// </summary>
    public int DaysLeft(DateTime asOfUtc, int holdDays) => holdDays - DaysHeld(asOfUtc);

    public string PatientName =>
        string.Join(" ", new[] { PatientFirstName, PatientLastName }
            .Where(p => !string.IsNullOrWhiteSpace(p))) is { Length: > 0 } name
            ? name
            : "—";
}

/// <summary>What happened when somebody tried to file a held 275 on a claim.</summary>
public sealed record LinkOutcome(bool Linked, string? Problem)
{
    public static readonly LinkOutcome Success = new(true, null);
    public static LinkOutcome Failed(string problem) => new(false, problem);
}

public interface IUnmatchedAttachmentService
{
    /// <summary>
    /// The held 275s for the signed-in user's tenant, oldest first. Null when
    /// attachment-service could not be reached — which is not the same as
    /// none being held, and the screen must not show it as such.
    /// </summary>
    Task<IReadOnlyList<HeldAttachment>?> GetHeldAsync(CancellationToken ct = default);

    Task<LinkOutcome> LinkToClaimAsync(string attachmentId, string claimId, string? reason, CancellationToken ct = default);
}

/// <summary>
/// Reads and resolves the held-275 queue through attachment-service, with the
/// signed-in user's CHO token (attached by ChoBearerTokenHandler), so the
/// tenant and the permissions are theirs and not the portal's.
/// </summary>
public sealed class UnmatchedAttachmentService : IUnmatchedAttachmentService
{
    public const string BaseUrlSetting = "Services:AttachmentService";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<UnmatchedAttachmentService> _logger;

    public UnmatchedAttachmentService(
        HttpClient httpClient, IConfiguration configuration, ILogger<UnmatchedAttachmentService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    private string BaseUrl => _configuration[BaseUrlSetting]
        ?? throw new InvalidOperationException($"{BaseUrlSetting} is not configured.");

    public async Task<IReadOnlyList<HeldAttachment>?> GetHeldAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync($"{BaseUrl}/attachments/unmatched", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("attachment-service answered {Status} listing held attachments", (int)response.StatusCode);
                return null;
            }

            var held = await response.Content.ReadFromJsonAsync<List<WireAttachment>>(Json, ct);
            return (held ?? [])
                .Select(a => new HeldAttachment(
                    a.Id, a.AttachmentControlNumber, a.ProviderId, a.ProviderName,
                    a.PatientFirstName, a.PatientLastName, a.DocumentType, a.CreatedDate, a.Notes))
                .OrderBy(a => a.ReceivedUtc)
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "attachment-service unreachable listing held attachments");
            return null;
        }
    }

    public async Task<LinkOutcome> LinkToClaimAsync(
        string attachmentId, string claimId, string? reason, CancellationToken ct = default)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                $"{BaseUrl}/attachments/{Uri.EscapeDataString(attachmentId)}/link",
                new { ClaimId = claimId, Reason = reason }, ct);

            if (response.IsSuccessStatusCode) return LinkOutcome.Success;

            // The service's own wording is the useful part — especially the
            // billing-NPI refusal, which explains itself better than a status
            // code does.
            var detail = await Detail(response, ct);
            return LinkOutcome.Failed(detail ?? response.StatusCode switch
            {
                HttpStatusCode.NotFound => "That attachment is no longer here.",
                HttpStatusCode.Forbidden => "You do not have permission to file attachments on claims.",
                _ => "The attachment could not be linked.",
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "attachment-service unreachable linking a held attachment");
            return LinkOutcome.Failed("Could not reach the attachment service. Nothing was linked.");
        }
    }

    private static async Task<string?> Detail(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body)) return null;

            // ProblemDetails for the 502s, a plain string for the rest.
            if (body.TrimStart().StartsWith('{'))
            {
                using var document = JsonDocument.Parse(body);
                return document.RootElement.TryGetProperty("detail", out var detail)
                    ? detail.GetString()
                    : null;
            }

            return body.Trim('"', ' ', '\n', '\r');
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The attachment fields this screen shows; the record carries more.</summary>
    private sealed record WireAttachment(
        string Id,
        string? AttachmentControlNumber,
        string ProviderId,
        string? ProviderName,
        string? PatientFirstName,
        string? PatientLastName,
        string DocumentType,
        DateTime CreatedDate,
        string? Notes);
}
