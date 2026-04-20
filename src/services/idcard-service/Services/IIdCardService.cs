using IdCardService.Adapters;
using IdCardService.Models;
using IdCardService.Repositories;

namespace IdCardService.Services;

public interface IIdCardOrchestrator
{
    Task<IdCardOrder> CreateOrderAsync(string tenantId, CreateIdCardOrderRequest request, CancellationToken ct = default);
    Task<IdCardOrder?> GetOrderAsync(string tenantId, string orderId, CancellationToken ct = default);
    Task<List<IdCardRecord>> ListForMemberAsync(string tenantId, string memberId, CancellationToken ct = default);
    Task<IdCardRecord?> GetByCardIdAsync(string tenantId, string cardId, CancellationToken ct = default);
    Task<IdCardRecord?> RevokeAsync(string tenantId, string cardId, RevokeIdCardRequest request, CancellationToken ct = default);
    Task<IdCardRecord?> RecordScanAsync(string tenantId, string cardId, CancellationToken ct = default);
}

public class IdCardOrchestrator : IIdCardOrchestrator
{
    private readonly IdCardAdapterFactory _adapters;
    private readonly IIdCardOrderRepository _orders;
    private readonly IIdCardRecordRepository _records;
    private readonly ILogger<IdCardOrchestrator> _logger;

    public IdCardOrchestrator(
        IdCardAdapterFactory adapters,
        IIdCardOrderRepository orders,
        IIdCardRecordRepository records,
        ILogger<IdCardOrchestrator> logger)
    {
        _adapters = adapters;
        _orders = orders;
        _records = records;
        _logger = logger;
    }

    public async Task<IdCardOrder> CreateOrderAsync(string tenantId, CreateIdCardOrderRequest request, CancellationToken ct = default)
    {
        if (request.Channel != IdCardDeliveryChannel.Digital)
        {
            var rejected = new IdCardOrder
            {
                TenantId = tenantId,
                MemberId = request.MemberId,
                Channel = request.Channel,
                LanguageCode = request.LanguageCode,
                RequestedBy = request.RequestedBy ?? "system",
                Status = IdCardOrderStatus.Failed,
                FailureCode = "CHANNEL_NOT_SUPPORTED",
                FailureReason = $"Delivery channel {request.Channel} is Phase 2 (digital-only in Phase 1)",
                UpdatedAt = DateTime.UtcNow
            };
            await _orders.UpsertAsync(rejected, ct);
            return rejected;
        }

        var (adapter, settings) = await _adapters.GetAdapterWithSettingsAsync(tenantId, ct);

        var order = new IdCardOrder
        {
            TenantId = tenantId,
            MemberId = request.MemberId,
            Channel = request.Channel,
            LanguageCode = request.LanguageCode,
            RequestedBy = request.RequestedBy ?? "system",
            Platform = adapter.Platform,
            Status = IdCardOrderStatus.Rendering
        };
        await _orders.UpsertAsync(order, ct);

        try
        {
            var result = await adapter.IssueAsync(new IdCardIssueRequest
            {
                TenantId = tenantId,
                OrderId = order.Id,
                MemberId = request.MemberId,
                Channel = request.Channel,
                LanguageCode = request.LanguageCode,
                RequestedBy = request.RequestedBy,
                PlatformSettings = settings
            }, ct);

            if (result.Success && result.Record != null)
            {
                await _records.UpsertAsync(result.Record, ct);

                order.CardId = result.Record.CardId;
                order.DocumentId = result.Record.DocumentId;
                order.PreviewDocumentId = result.Record.PreviewDocumentId;
                order.Status = IdCardOrderStatus.Issued;
                order.IssuedAt = result.Record.IssuedAt;
            }
            else
            {
                order.Status = IdCardOrderStatus.Failed;
                order.FailureCode = result.FailureCode;
                order.FailureReason = result.FailureReason;
            }
        }
        catch (NotSupportedException nse)
        {
            order.Status = IdCardOrderStatus.Failed;
            order.FailureCode = "CHANNEL_NOT_SUPPORTED";
            order.FailureReason = nse.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ID card issuance failed for order {OrderId}", order.Id);
            order.Status = IdCardOrderStatus.Failed;
            order.FailureCode = "ISSUANCE_ERROR";
            order.FailureReason = ex.Message;
        }
        finally
        {
            order.UpdatedAt = DateTime.UtcNow;
            await _orders.UpsertAsync(order, ct);
        }

        return order;
    }

    public Task<IdCardOrder?> GetOrderAsync(string tenantId, string orderId, CancellationToken ct = default) =>
        _orders.GetAsync(tenantId, orderId, ct);

    public Task<List<IdCardRecord>> ListForMemberAsync(string tenantId, string memberId, CancellationToken ct = default) =>
        _records.ListForMemberAsync(tenantId, memberId, ct);

    public Task<IdCardRecord?> GetByCardIdAsync(string tenantId, string cardId, CancellationToken ct = default) =>
        _records.FindByCardIdAsync(tenantId, cardId, ct);

    public async Task<IdCardRecord?> RevokeAsync(string tenantId, string cardId, RevokeIdCardRequest request, CancellationToken ct = default)
    {
        var record = await _records.FindByCardIdAsync(tenantId, cardId, ct);
        if (record == null) return null;

        record.RevokedAt = DateTime.UtcNow;
        record.RevocationReason = request.Reason;
        record.RevokedBy = request.Notes;
        await _records.UpsertAsync(record, ct);
        return record;
    }

    public async Task<IdCardRecord?> RecordScanAsync(string tenantId, string cardId, CancellationToken ct = default)
    {
        var record = await _records.FindByCardIdAsync(tenantId, cardId, ct);
        if (record == null) return null;
        record.ScanCount += 1;
        record.LastScannedAt = DateTime.UtcNow;
        await _records.UpsertAsync(record, ct);
        return record;
    }
}
