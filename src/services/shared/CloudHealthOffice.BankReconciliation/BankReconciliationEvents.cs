using Microsoft.Extensions.Logging;
using CloudHealthOffice.Infrastructure.Logging;

namespace CloudHealthOffice.BankReconciliation;

/// <summary>
/// What reconciliation decided for a bank line, or how a person resolved it.
/// This is the seam for GL posting: a sink can journal cash confirmed by the
/// bank, NACHA settlements and ACH returns. It carries no account number and
/// no 16 text.
/// </summary>
public sealed record BankReconciliationEvent
{
    public const string Decided = "BankTransactionReconciled";
    public const string ResolvedType = "BankReconciliationItemResolved";

    /// <summary>Stable per item and event type, so a sink can deduplicate.</summary>
    public required string EventId { get; init; }

    public required string Type { get; init; }
    public required string TenantId { get; init; }
    public required string Service { get; init; }
    public required string ItemId { get; init; }
    public required string TransactionId { get; init; }
    public required string StatementId { get; init; }

    /// <summary>PremiumCash, LockboxDeposit, NachaSettlement, AchReturn, Other.</summary>
    public required string Kind { get; init; }

    public required ReconciliationStatus Status { get; init; }
    public required string TypeCode { get; init; }
    public required Bai2.Bai2Direction Direction { get; init; }
    public required long AmountMinor { get; init; }
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }
    public required DateTime AsOfDate { get; init; }
    public string? MatchedEntityType { get; init; }
    public IReadOnlyList<string> MatchedEntityIds { get; init; } = [];
    public DateTime OccurredAt { get; init; }

    public static BankReconciliationEvent From(ReconciliationItem item, string type, DateTime now) => new()
    {
        EventId = $"{item.Id}:{type}",
        Type = type,
        TenantId = item.TenantId,
        Service = item.Service,
        ItemId = item.Id,
        TransactionId = item.TransactionId,
        StatementId = item.StatementId,
        Kind = item.Kind,
        Status = item.Status,
        TypeCode = item.TypeCode,
        Direction = item.Direction,
        AmountMinor = item.AmountMinor,
        Amount = item.Amount,
        Currency = item.Currency,
        AsOfDate = item.AsOfDate,
        MatchedEntityType = item.MatchedEntityType,
        MatchedEntityIds = [.. item.MatchedEntityIds],
        OccurredAt = now,
    };
}

/// <summary>
/// Receives reconciliation events. The default logs them; GL posting
/// replaces it by registering its own <see cref="IBankReconciliationEventSink"/>
/// (registration uses TryAdd, so a sink registered first wins). Delivery is at
/// least once: an item's event is retried on the next ingest of its file until
/// <see cref="ReconciliationItem.EventPublishedAt"/> is set, so a sink must
/// deduplicate on <see cref="BankReconciliationEvent.EventId"/>.
/// </summary>
public interface IBankReconciliationEventSink
{
    Task PublishAsync(BankReconciliationEvent evt, CancellationToken ct = default);
}

public sealed class LoggingBankReconciliationEventSink : IBankReconciliationEventSink
{
    private readonly ILogger<LoggingBankReconciliationEventSink> _logger;

    public LoggingBankReconciliationEventSink(ILogger<LoggingBankReconciliationEventSink> logger) => _logger = logger;

    public Task PublishAsync(BankReconciliationEvent evt, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Bank reconciliation {Type}: {Service} item {ItemId} {Kind} {Status} type {TypeCode} {Direction} {Amount} {Currency}",
            LogSanitizer.Sanitize(evt.Type), LogSanitizer.Sanitize(evt.Service), LogSanitizer.Sanitize(evt.ItemId), LogSanitizer.Sanitize(evt.Kind), evt.Status,
            LogSanitizer.Sanitize(evt.TypeCode), evt.Direction, evt.Amount, LogSanitizer.Sanitize(evt.Currency));
        return Task.CompletedTask;
    }
}
