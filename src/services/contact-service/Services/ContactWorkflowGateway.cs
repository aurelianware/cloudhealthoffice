using System.Net.Http.Json;
using CloudHealthOffice.Infrastructure.Contacts;

namespace ContactService.Services;

public interface IContactWorkflowGateway
{
    Task<ContactCaseResult> OpenCaseAsync(ContactService.Models.Contact contact, CancellationToken ct);
    Task<string> CreateFollowUpAsync(string contactId, string key, CancellationToken ct);
}

public sealed class ContactWorkflowUnavailableException(int status) : Exception("The owning service did not accept the request. The saved request can be retried.")
{
    public int Status { get; } = status;
}

public sealed class HttpContactWorkflowGateway(IHttpClientFactory clients) : IContactWorkflowGateway
{
    public async Task<ContactCaseResult> OpenCaseAsync(ContactService.Models.Contact contact, CancellationToken ct)
    {
        var coverage = contact.Classification == Models.ContactClassification.CoverageRequest;
        using var response = await clients.CreateClient(coverage ? "ContactAuthorization" : "ContactAppeals")
            .PostAsync((coverage ? "api/authorizations/from-contact/" : "api/service-cases/from-contact/")
                + Uri.EscapeDataString(contact.Id), null, ct);
        if (!response.IsSuccessStatusCode) throw new ContactWorkflowUnavailableException((int)response.StatusCode);
        return await response.Content.ReadFromJsonAsync<ContactCaseResult>(cancellationToken: ct)
            ?? throw new ContactWorkflowUnavailableException(502);
    }

    public async Task<string> CreateFollowUpAsync(string contactId, string key, CancellationToken ct)
    {
        using var response = await clients.CreateClient("ContactWork")
            .PostAsync($"api/v1/work/from-contact/{Uri.EscapeDataString(contactId)}/{Uri.EscapeDataString(key)}", null, ct);
        if (!response.IsSuccessStatusCode) throw new ContactWorkflowUnavailableException((int)response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<WorkResult>(cancellationToken: ct);
        return result?.Id ?? throw new ContactWorkflowUnavailableException(502);
    }
    private sealed record WorkResult(string Id);
}
