using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContactService.Models;

namespace ContactService.Services.Claims;

/// <summary>
/// Writes contact notes on claims through claims-service's own notes API.
///
/// The rep's token is forwarded by <c>ChoOutboundTokenHandler</c>, which
/// every CHO HttpClient carries, so the note is authored by the rep who took
/// the call rather than by this service. That is the point: a claim note is
/// a record of who said what, and it is worth nothing attributed to a
/// machine. A rep whose role does not hold <c>claims:note</c> gets a
/// rejected write and a contact that still records the link.
/// </summary>
public sealed class HttpContactNoteGateway(
    IHttpClientFactory factory, ILogger<HttpContactNoteGateway> logger) : IContactNoteGateway
{
    public const string HttpClientName = "ClaimsService";

    /// <summary>The header claims-service's TenantMiddleware reads.</summary>
    private const string TenantHeader = "X-Tenant-ID";

    /// <summary>claims-service's own category values (ClaimNoteCategories).</summary>
    private const string MemberContactCategory = "MemberContact";
    private const string ProviderContactCategory = "ProviderContact";

    /// <summary>claims-service rejects a note longer than this.</summary>
    private const int MaxNoteLength = 4000;

    private static readonly JsonSerializerOptions WireFormat = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<string?> WriteContactNoteAsync(ContactNoteCommand command, CancellationToken ct = default)
    {
        if (command.SubjectType != ContactSubjectType.Claim) return null;

        var payload = new
        {
            category = CategoryFor(command.CallerType),
            text = TextFor(command),
        };

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"api/claims/{Uri.EscapeDataString(command.SubjectId)}/notes")
            {
                Content = JsonContent.Create(payload, options: WireFormat),
            };
            request.Headers.TryAddWithoutValidation(TenantHeader, command.TenantId);

            using var response = await factory.CreateClient(HttpClientName).SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                // Status category only — never the body, which echoes the request.
                logger.LogWarning(
                    "claims-service returned {Status} when writing a contact note on claim {ClaimId}; " +
                    "the claim is linked to the contact but its own history does not show the call.",
                    (int)response.StatusCode, command.SubjectId);
                return null;
            }

            var created = await response.Content.ReadFromJsonAsync<ClaimNoteCreated>(WireFormat, ct);
            return created?.Id;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not write a contact note on claim {ClaimId}; the claim is linked to the contact " +
                "but its own history does not show the call.",
                command.SubjectId);
            return null;
        }
    }

    private static string CategoryFor(CallerType caller) =>
        caller == CallerType.Provider ? ProviderContactCategory : MemberContactCategory;

    /// <summary>
    /// The note names the contact, so someone reading the claim can find the
    /// whole conversation, and carries what the caller actually asked.
    /// </summary>
    private static string TextFor(ContactNoteCommand command)
    {
        var who = command.CallerType switch
        {
            CallerType.Provider       => "Provider",
            CallerType.Representative => "Member's representative",
            CallerType.Other          => "Caller",
            _                         => "Member",
        };

        var text = string.IsNullOrWhiteSpace(command.Note)
            ? $"{who} asked about this claim on contact {command.ContactNumber}."
            : $"{who} asked about this claim on contact {command.ContactNumber}: {command.Note.Trim()}";

        return text.Length <= MaxNoteLength ? text : text[..MaxNoteLength];
    }

    private sealed record ClaimNoteCreated(string Id);
}
