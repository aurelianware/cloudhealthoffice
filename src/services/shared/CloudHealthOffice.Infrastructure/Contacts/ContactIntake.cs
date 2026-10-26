using System.ComponentModel.DataAnnotations;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.Infrastructure.Contacts;

// Wire contracts shared by contact-service and the owning intake services.
// These deliberately contain no persistence or downstream business-policy code.
public sealed class ContactCaseIntent
{
    [Required, StringLength(30)] public string LineOfBusiness { get; set; } = "Medicaid";
    [Required, RegularExpression("^[A-Z]{2}$")] public string State { get; set; } = string.Empty;
    public ContactCoverageDetails? Coverage { get; set; }
    [StringLength(100)] public string? AuthorizationId { get; set; }
}

public sealed class ContactCoverageDetails
{
    [Required, StringLength(100)] public string FirstName { get; set; } = string.Empty;
    [Required, StringLength(100)] public string LastName { get; set; } = string.Empty;
    public DateTime DateOfBirth { get; set; }
    [Required, RegularExpression("^[0-9]{10}$")] public string ProviderNpi { get; set; } = string.Empty;
    [Required, StringLength(2)] public string ServiceTypeCode { get; set; } = string.Empty;
    [Required, StringLength(10)] public string ProcedureCode { get; set; } = string.Empty;
    [Range(0.01, 1000000)] public decimal Units { get; set; } = 1;
    public DateTime ServiceDate { get; set; }
    public bool Expedited { get; set; }
}

public sealed class ContactIntakeReceipt
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string? MemberId { get; set; }
    public DateTime StartedAt { get; set; }
    public string? Classification { get; set; }
    public string? Summary { get; set; }
    public string Channel { get; set; } = string.Empty;
    public ContactIntakeCaller Caller { get; set; } = new();
    public ContactCaseIntent? CaseIntent { get; set; }
    public List<ContactFollowUpIntent> FollowUps { get; set; } = new();
}

public sealed class ContactIntakeCaller
{
    public string Type { get; set; } = string.Empty;
}

public sealed class ContactCaseResult
{
    public string? IntakeHash { get; set; }
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
    public DateTime? DueAt { get; set; }
    public string? RulePackVersion { get; set; }
}

public static class ContactIntakeIdentity
{
    public static string Hash(ContactCaseIntent intent) => Convert.ToHexString(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(intent))).ToLowerInvariant();

    public static string For(string tenant, string contact) =>
        "contact-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new[] { tenant, contact })))).ToLowerInvariant()[..32];
}

public interface IContactIntakeReader
{
    Task<ContactIntakeReceipt?> GetAsync(string id, CancellationToken ct);
}

public sealed class HttpContactIntakeReader(IHttpClientFactory clients) : IContactIntakeReader
{
    public const string ClientName = "ContactIntake";
    public async Task<ContactIntakeReceipt?> GetAsync(string id, CancellationToken ct)
    {
        using var response = await clients.CreateClient(ClientName)
            .GetAsync("api/contacts/" + Uri.EscapeDataString(id), ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ContactIntakeReceipt>(cancellationToken: ct);
    }
}

public static class ContactIntakeRegistration
{
    public static IServiceCollection AddContactIntakeReader(this IServiceCollection services, IConfiguration config)
    {
        services.AddHttpClient(HttpContactIntakeReader.ClientName, client =>
        {
            client.BaseAddress = new Uri((config["Services:ContactServiceUrl"]
                ?? "http://contact-service.cloudhealthoffice/").TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddScoped<IContactIntakeReader, HttpContactIntakeReader>();
        return services;
    }
}

public sealed class ContactFollowUpIntent
{
    public string Key { get; set; } = string.Empty;
    [Required, StringLength(500)] public string Title { get; set; } = string.Empty;
    public DateTime? DueAt { get; set; }
    public DateTime RequestedAt { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public string? MemberId { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string CallerType { get; set; } = string.Empty;
    public string Classification { get; set; } = string.Empty;
    public string? WorkItemId { get; set; }
}
