using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CareManagementService.Models;

namespace CareManagementService.Services.Work;

/// <summary>
/// Queues care activities against work-management-service over its internal
/// API.
///
/// care-management-service does not own the work item and keeps no copy of
/// it: it sends the activity and keeps the handle that comes back. Every call
/// carries the case's OWN tenant explicitly, so the item lands in the same
/// partition as the case that caused it even on a background thread with no
/// ambient HTTP context. Modelled on authorization-service's RFAI gateway.
/// </summary>
public sealed class HttpCareWorkItemGateway(
    IHttpClientFactory factory, ILogger<HttpCareWorkItemGateway> logger) : ICareWorkItemGateway
{
    public const string HttpClientName = "WorkManagementService";

    /// <summary>The header work-management-service's TenantMiddleware reads.</summary>
    private const string TenantHeader = "X-Tenant-ID";

    private static readonly JsonSerializerOptions WireFormat = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<string?> EnqueueAsync(CareWorkItemCommand command, CancellationToken ct = default)
    {
        var payload = new
        {
            workType = "care-activity",
            externalId = $"{command.CaseId}:{command.Activity.Id}",
            subjectId = command.MemberId,
            title = command.Activity.Description,
            description = command.Activity.ActivityType,
            priority = MapPriority(command.Acuity),
            dueDate = command.Activity.DueDate,
            assignedTo = command.Activity.AssignedTo,
            queue = $"care-management/{command.Program}",
            reference = command.CaseNumber,
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/work")
            {
                Content = JsonContent.Create(payload, options: WireFormat),
            };
            request.Headers.TryAddWithoutValidation(TenantHeader, command.TenantId);

            using var response = await factory.CreateClient(HttpClientName).SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                // Status category only — never the body, which echoes the request.
                logger.LogWarning(
                    "work-management-service returned {Status} when queueing a care activity; " +
                    "the activity is recorded on the case but is in nobody's queue.",
                    (int)response.StatusCode);
                return null;
            }

            var created = await response.Content.ReadFromJsonAsync<WorkItemCreated>(WireFormat, ct);
            return created?.Id;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The care record stands whether or not the queue took the item.
            logger.LogWarning(ex,
                "Could not queue a care activity with work-management-service; " +
                "the activity is recorded on the case but is in nobody's queue.");
            return null;
        }
    }

    public async Task CompleteAsync(
        string tenantId, string workItemId, string? outcome, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workItemId)) return;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"api/work/{workItemId}/complete")
            {
                Content = JsonContent.Create(new { outcome }, options: WireFormat),
            };
            request.Headers.TryAddWithoutValidation(TenantHeader, tenantId);

            using var response = await factory.CreateClient(HttpClientName).SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
                logger.LogWarning(
                    "work-management-service returned {Status} when completing a care activity's work item.",
                    (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not complete a care activity's work item.");
        }
    }

    /// <summary>Acuity is the plan's judgement of urgency, so it drives the queue's priority.</summary>
    private static string MapPriority(CareAcuity acuity) => acuity switch
    {
        CareAcuity.Critical => "urgent",
        CareAcuity.High     => "high",
        CareAcuity.Moderate => "normal",
        _                   => "low",
    };

    private sealed record WorkItemCreated([property: JsonPropertyName("id")] string? Id);
}
