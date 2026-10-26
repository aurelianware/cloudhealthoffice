using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// Calls contact-service (<c>Services:ContactService</c>) with the signed-in
/// rep's own CHO token, so the contact and any claim note it writes are
/// attributed to the person who took the call.
/// </summary>
public class ContactTrackingService(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger<ContactTrackingService> logger) : IContactTrackingService
{
    public const string ServiceName = "Contact Service";

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private string BaseUrl =>
        (configuration["Services:ContactService"] ?? "http://contact-service.cloudhealthoffice/api").TrimEnd('/');

    // ── Reading ───────────────────────────────────────────────────

    public Task<ContactRecord?> GetAsync(string id, CancellationToken ct = default) =>
        GetAsync<ContactRecord>($"contacts/{Uri.EscapeDataString(id)}", ct);

    public async Task<List<ContactListItem>> GetMineAsync(
        bool openOnly = false, int limit = 50, CancellationToken ct = default) =>
        await GetAsync<List<ContactListItem>>($"contacts/mine?openOnly={openOnly}&limit={limit}", ct) ?? [];

    public async Task<List<ContactListItem>> GetByMemberAsync(
        string memberId, int limit = 50, CancellationToken ct = default) =>
        await GetAsync<List<ContactListItem>>(
            $"contacts/by-member/{Uri.EscapeDataString(memberId)}?limit={limit}", ct) ?? [];

    public async Task<List<ContactListItem>> GetByProviderAsync(
        string npi, int limit = 50, CancellationToken ct = default) =>
        await GetAsync<List<ContactListItem>>(
            $"contacts/by-provider/{Uri.EscapeDataString(npi)}?limit={limit}", ct) ?? [];

    public async Task<List<ContactListItem>> GetBySubjectAsync(
        ContactSubjectType type, string subjectId, int limit = 50, CancellationToken ct = default) =>
        await GetAsync<List<ContactListItem>>(
            $"contacts/by-subject/{type}/{Uri.EscapeDataString(subjectId)}?limit={limit}", ct) ?? [];

    public async Task<List<ContactReasonOption>> GetReasonsAsync(
        bool activeOnly = true, CancellationToken ct = default) =>
        await GetAsync<List<ContactReasonOption>>($"contact-reasons?activeOnly={activeOnly}", ct) ?? [];

    // ── Writing ───────────────────────────────────────────────────

    public Task<ContactRecord?> StartAsync(StartContactRequest request, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "contacts", request, ct);

    public Task<ContactRecord?> UpdateAsync(string id, UpdateContactRequest request, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Patch, $"contacts/{Uri.EscapeDataString(id)}", request, ct);

    public Task<ContactRecord?> LinkSubjectAsync(
        string id, LinkContactSubjectRequest request, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"contacts/{Uri.EscapeDataString(id)}/subjects", request, ct);

    public Task<ContactRecord?> CloseAsync(string id, CloseContactRequest request, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"contacts/{Uri.EscapeDataString(id)}/close", request, ct);

    public Task<ContactRecord?> ReopenAsync(string id, ReopenContactRequest request, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"contacts/{Uri.EscapeDataString(id)}/reopen", request, ct);

    public Task<ContactRecord?> AmendCaseAsync(string id, AmendContactCaseRequest request, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"contacts/{Uri.EscapeDataString(id)}/case/amend", request, ct);

    public Task<ContactRecord?> OpenCaseAsync(string id, OpenContactCaseRequest request, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"contacts/{Uri.EscapeDataString(id)}/case", request, ct);
    public Task<ContactRecord?> CreateFollowUpAsync(string id, CreateContactFollowUpRequest request, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"contacts/{Uri.EscapeDataString(id)}/follow-ups", request, ct);

    // ── Plumbing ──────────────────────────────────────────────────

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
    {
        try
        {
            using var response = await httpClient.GetAsync($"{BaseUrl}/{path}", ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return default;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(Wire, ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Service unavailable: {ServiceName}", ServiceName);
            throw new ServiceUnavailableException(ServiceName, ex);
        }
    }

    private async Task<ContactRecord?> SendAsync<TRequest>(
        HttpMethod method, string path, TRequest body, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, $"{BaseUrl}/{path}")
            {
                Content = JsonContent.Create(body, options: Wire),
            };

            using var response = await httpClient.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.NotFound) return null;

            // A refusal carries a sentence written for the person reading it
            // ("This contact is closed", "it has changed since you read it"),
            // so it is shown rather than swallowed into a generic failure.
            if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.UnprocessableEntity)
            {
                var reason = (await response.Content.ReadAsStringAsync(ct)).Trim().Trim('"');
                throw new ContactWriteRefusedException(
                    string.IsNullOrWhiteSpace(reason) ? "That change was refused." : reason);
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ContactRecord>(Wire, ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Service unavailable: {ServiceName}", ServiceName);
            throw new ServiceUnavailableException(ServiceName, ex);
        }
    }
}
