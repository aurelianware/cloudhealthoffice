using CloudHealthOffice.NachaTransmission;

namespace PremiumBillingService.Tests.Services;

/// <summary>
/// The dispatcher as unit tests see it: records what would have been sent and
/// answers with a chosen outcome (delivered by default).
/// </summary>
public sealed class RecordingNachaDispatcher : INachaDispatcher
{
    public List<NachaTransmissionRequest> Sent { get; } = new();
    public NachaTransmissionStatus Status { get; set; } = NachaTransmissionStatus.Transmitted;
    public string? Reason { get; set; }

    public Task<NachaDispatchOutcome> DispatchAsync(NachaTransmissionRequest request, CancellationToken cancellationToken = default)
    {
        Sent.Add(request);
        return Task.FromResult(Status == NachaTransmissionStatus.Transmitted
            ? new NachaDispatchOutcome
            {
                Status = Status,
                Receipt = new NachaTransmissionReceipt
                {
                    TenantId = request.TenantId, FileReference = request.FileReference, RemoteFileName = request.FileName,
                    Sha256 = NachaFileFacts.From(request.Content).Sha256, TransmittedBy = request.TransmittedBy,
                },
            }
            : new NachaDispatchOutcome { Status = Status, Reason = Reason ?? "bank unreachable", HeldUntil = DateTime.UtcNow.AddDays(7) });
    }

    public Task<IReadOnlyList<NachaHeldFile>> ListHeldAsync(string tenantId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<NachaHeldFile>>(Array.Empty<NachaHeldFile>());

    public Task<NachaHeldFile?> GetHeldAsync(string tenantId, string fileReference, CancellationToken cancellationToken = default)
        => Task.FromResult<NachaHeldFile?>(null);

    public Task<NachaDispatchOutcome> RetryAsync(string tenantId, string fileReference, NachaActor actor, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<NachaRetrievedFile> RetrieveAsync(string tenantId, string fileReference, NachaActor actor, string reason, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
