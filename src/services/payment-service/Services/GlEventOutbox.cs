using System.Text.Json;
using CloudHealthOffice.Finance.Contracts;
using PaymentService.Models;

namespace PaymentService.Services;

/// <summary>
/// Builds the GL source events of payment and reversal runs and attaches them to the
/// run's <c>GlOutbox</c>, so the repository writes them in the same document write that
/// records the run's outcome (the appeals-service outbox pattern). Event ids are
/// deterministic per run, so re-attaching never creates a second event.
/// Amounts are the payments' own (money, 2 decimals); nothing else is copied:
/// no bank number, no member or claim detail.
/// </summary>
public static class GlEventOutbox
{
    /// <summary>
    /// Attaches PaymentRunExecuted for <paramref name="issued"/> (the payments the run
    /// actually created, also when it then failed). Nothing when none were issued.
    /// </summary>
    public static void AttachPaymentRunExecuted(PaymentRun run, IReadOnlyCollection<Payment> issued, DateTime at)
    {
        if (issued.Count == 0 || run.GlOutbox.Any(m => m.Type == GlEventTypes.PaymentRunExecuted))
            return;

        var eventId = GlEventTypes.EventIdFor(run.TenantId, $"payment-run:{run.Id}", GlEventTypes.PaymentRunExecuted);
        var lines = issued
            .OrderBy(p => p.CheckNumber, StringComparer.Ordinal).ThenBy(p => p.Id, StringComparer.Ordinal)
            .Select(p => new GlPaymentLine
            {
                PaymentId = p.Id,
                CheckNumber = p.CheckNumber,
                PayeeNpi = p.PayeeNPI,
                PaymentMethod = p.PaymentMethod,
                NetAmount = p.TotalPaymentAmount,
                ReceivableOffsetAmount = p.ReceivableOffsets.Sum(o => o.Amount),
            })
            .ToList();
        var payload = new PaymentRunExecutedEvent
        {
            EventId = eventId,
            TenantId = run.TenantId,
            PaymentRunId = run.Id,
            PaymentRunNumber = run.PaymentRunNumber,
            RunStatus = run.Status.ToString(),
            LineOfBusiness = run.Criteria.LineOfBusiness?.ToString(),
            PaymentDate = run.PaymentDate.Date,
            ExecutedAt = run.ExecutionStartedAt ?? at,
            ExecutedBy = run.ExecutedBy ?? string.Empty,
            Payments = lines,
            TotalNetAmount = lines.Sum(l => l.NetAmount),
            TotalReceivableOffsetAmount = lines.Sum(l => l.ReceivableOffsetAmount),
        };
        run.GlOutbox.Add(Message(eventId, GlEventTypes.PaymentRunExecuted, JsonSerializer.Serialize(payload, GlEventTypes.Json), at));
    }

    /// <summary>Attaches ReversalRunExecuted for the reversal payments <paramref name="issued"/> created.</summary>
    public static void AttachReversalRunExecuted(ReversalRun run, IReadOnlyCollection<Payment> issued, DateTime at)
    {
        if (issued.Count == 0 || run.GlOutbox.Any(m => m.Type == GlEventTypes.ReversalRunExecuted))
            return;

        var eventId = GlEventTypes.EventIdFor(run.TenantId, $"reversal-run:{run.Id}", GlEventTypes.ReversalRunExecuted);
        var lines = issued
            .OrderBy(p => p.CheckNumber, StringComparer.Ordinal).ThenBy(p => p.Id, StringComparer.Ordinal)
            .Select(p => new GlReversalLine
            {
                ReversalPaymentId = p.Id,
                CheckNumber = p.CheckNumber,
                PayeeNpi = p.PayeeNPI,
                // A reversal payment is the negative of what was paid.
                Amount = Math.Abs(p.TotalPaymentAmount),
            })
            .ToList();
        var payload = new ReversalRunExecutedEvent
        {
            EventId = eventId,
            TenantId = run.TenantId,
            ReversalRunId = run.Id,
            ReversalRunNumber = run.ReversalRunNumber,
            RunStatus = run.Status.ToString(),
            ExecutedAt = run.ExecutionStartedAt ?? at,
            ExecutedBy = run.ExecutedBy ?? string.Empty,
            Reversals = lines,
            TotalAmount = lines.Sum(l => l.Amount),
        };
        run.GlOutbox.Add(Message(eventId, GlEventTypes.ReversalRunExecuted, JsonSerializer.Serialize(payload, GlEventTypes.Json), at));
    }

    private static PaymentFileOutboxMessage Message(string eventId, string type, string payload, DateTime at) => new()
    {
        EventId = eventId,
        Type = type,
        PayloadJson = payload,
        CreatedAt = at,
    };
}
