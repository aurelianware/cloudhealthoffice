using System.Text.Json;
using System.Text.Json.Nodes;
using FhirService.Services.Identity;
using FhirService.Services.Subscriptions;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// FHIR R4 Subscription, for the Subscriptions R5 Backport and the one topic
/// CHO publishes: Da Vinci PAS's pended-authorization topic. See
/// <see cref="PasSubscriptionService"/>.
///
/// <para>
/// The body is read as JSON rather than bound to the FHIR model, because the
/// backport carries its settings in primitive extensions (<c>_criteria</c>,
/// <c>_payload</c>) and the stored resource is returned as the client sent it.
/// Subscriptions are tenant-scoped from the authenticated context. Not served
/// at all unless <c>Subscriptions:Enabled</c>.
/// </para>
/// </summary>
[Route("fhir/r4/Subscription")]
[FhirAccess(smart: true, cho: "authorizations:write")]
public sealed class SubscriptionController : FhirControllerBase
{
    private const int MaxBodyBytes = 64 * 1024;

    private readonly PasSubscriptionService _subscriptions;

    public SubscriptionController(PasSubscriptionService subscriptions) => _subscriptions = subscriptions;

    [HttpPost]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        if (!_subscriptions.Enabled)
            return FhirNotFound("Subscription", "");

        var (body, error) = await ReadBodyAsync(ct);
        if (error is not null)
            return error;

        var (created, refusal) = await _subscriptions.CreateAsync(body!, TenantId, FhirBaseUrl, ct);
        if (refusal is not null)
            return FhirBadRequest(refusal.Message);

        Response.Headers.Location = $"{FhirBaseUrl}/Subscription/{created!.Id}";
        return Resource(created, StatusCodes.Status201Created);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Read(string id, CancellationToken ct)
    {
        if (!_subscriptions.Enabled)
            return FhirNotFound("Subscription", id);

        var stored = await _subscriptions.Store.GetAsync(TenantId, id, ct);
        return stored is null ? FhirNotFound("Subscription", id) : Resource(stored, StatusCodes.Status200OK);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, CancellationToken ct)
    {
        if (!_subscriptions.Enabled)
            return FhirNotFound("Subscription", id);

        var (body, error) = await ReadBodyAsync(ct);
        if (error is not null)
            return error;
        if (body!["id"]?.GetValue<string>() is { } bodyId && bodyId != id)
            return FhirBadRequest("The Subscription's id does not match the URL.");

        var (updated, refusal) = await _subscriptions.UpdateAsync(body, TenantId, id, FhirBaseUrl, ct);
        if (refusal is not null)
            return FhirBadRequest(refusal.Message);
        return updated is null ? FhirNotFound("Subscription", id) : Resource(updated, StatusCodes.Status200OK);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        if (!_subscriptions.Enabled)
            return FhirNotFound("Subscription", id);

        return await _subscriptions.Store.DeleteAsync(TenantId, id, ct)
            ? NoContent()
            : FhirNotFound("Subscription", id);
    }

    private async Task<(JsonObject? Body, IActionResult? Error)> ReadBodyAsync(CancellationToken ct)
    {
        if (Request.ContentLength is > MaxBodyBytes)
            return (null, FhirBadRequest("The Subscription is too large."));

        using var reader = new StreamReader(Request.Body);
        var buffer = new char[MaxBodyBytes + 1];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
        if (read > MaxBodyBytes)
            return (null, FhirBadRequest("The Subscription is too large."));

        try
        {
            return JsonNode.Parse(new string(buffer, 0, read)) is JsonObject body
                ? (body, null)
                : (null, FhirBadRequest("The body is not a Subscription."));
        }
        catch (JsonException)
        {
            return (null, FhirBadRequest("The body is not valid JSON."));
        }
    }

    private static ContentResult Resource(PasSubscriptionRecord subscription, int status) => new()
    {
        Content = subscription.Resource,
        ContentType = "application/fhir+json",
        StatusCode = status,
    };
}
