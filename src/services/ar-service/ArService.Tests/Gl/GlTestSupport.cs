using System.Text.Json;
using ArService.Gl;
using ArService.Models;
using CloudHealthOffice.Finance.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ArService.Tests.Gl;

/// <summary>The GL stores in memory, with the Mongo stores' semantics (copies, insert-if-absent, versions, balance guard).</summary>
internal sealed class InMemoryGl : IGlJournalRepository, IGlSourceEventRepository, IGlPeriodRepository, IGlChartLookup
{
    private readonly Dictionary<string, string> _entries = new();
    private readonly Dictionary<string, string> _events = new();
    private readonly Dictionary<string, GlClosedPeriod> _periods = new();
    public List<GlAccount> Chart { get; } = new();

    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    public IReadOnlyList<GlJournalEntry> Entries
    {
        get { lock (_entries) return _entries.Values.Select(j => JsonSerializer.Deserialize<GlJournalEntry>(j)!).ToList(); }
    }

    public Task<bool> TryInsertAsync(GlJournalEntry entry, CancellationToken cancellationToken = default)
    {
        GlJournal.EnsureBalanced(entry);
        lock (_entries) return Task.FromResult(_entries.TryAdd(entry.Id, JsonSerializer.Serialize(entry)));
    }

    public Task<GlJournalEntry?> GetAsync(string tenantId, string entryId, CancellationToken cancellationToken = default)
    {
        lock (_entries)
            return Task.FromResult(_entries.TryGetValue(entryId, out var j) && JsonSerializer.Deserialize<GlJournalEntry>(j) is { } e && e.TenantId == tenantId ? e : null);
    }

    public Task<IReadOnlyList<GlJournalEntry>> ListAsync(string tenantId, string? period, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<GlJournalEntry>>(Entries.Where(e => e.TenantId == tenantId && (period == null || e.Period == period))
            .OrderBy(e => e.EntryDate).ThenBy(e => e.Id, StringComparer.Ordinal).ToList());

    public Task<bool> TryInsertAsync(GlSourceEvent record, CancellationToken cancellationToken = default)
    {
        record.Id = GlSourceEvent.KeyFor(record.TenantId, record.EventId);
        record.Version = Guid.NewGuid().ToString("N");
        lock (_events) return Task.FromResult(_events.TryAdd(record.Id, JsonSerializer.Serialize(record)));
    }

    Task<GlSourceEvent?> IGlSourceEventRepository.GetAsync(string tenantId, string eventId, CancellationToken cancellationToken)
    {
        lock (_events)
            return Task.FromResult(_events.TryGetValue(GlSourceEvent.KeyFor(tenantId, eventId), out var j) ? JsonSerializer.Deserialize<GlSourceEvent>(j) : null);
    }

    public Task<bool> TryReplaceAsync(GlSourceEvent record, string expectedVersion, CancellationToken cancellationToken = default)
    {
        lock (_events)
        {
            if (!_events.TryGetValue(record.Id, out var j) || JsonSerializer.Deserialize<GlSourceEvent>(j)!.Version != expectedVersion)
                return Task.FromResult(false);
            record.Version = Guid.NewGuid().ToString("N");
            _events[record.Id] = JsonSerializer.Serialize(record);
            return Task.FromResult(true);
        }
    }

    public IReadOnlyList<GlSourceEvent> Events
    {
        get { lock (_events) return _events.Values.Select(j => JsonSerializer.Deserialize<GlSourceEvent>(j)!).ToList(); }
    }

    public Task<IReadOnlyList<GlSourceEvent>> ListAsync(string tenantId, GlSourceEventStatus? status, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<GlSourceEvent>>(Events.Where(e => e.TenantId == tenantId && (status == null || e.Status == status))
            .OrderBy(e => e.ReceivedAt).ThenBy(e => e.EventId, StringComparer.Ordinal).ToList());

    public Task<bool> IsClosedAsync(string tenantId, string period, CancellationToken cancellationToken = default)
    {
        lock (_periods) return Task.FromResult(_periods.ContainsKey(GlClosedPeriod.KeyFor(tenantId, period)));
    }

    public Task<bool> TryCloseAsync(GlClosedPeriod period, CancellationToken cancellationToken = default)
    {
        period.Id = GlClosedPeriod.KeyFor(period.TenantId, period.Period);
        lock (_periods) return Task.FromResult(_periods.TryAdd(period.Id, Copy(period)));
    }

    Task<IReadOnlyList<GlClosedPeriod>> IGlPeriodRepository.ListAsync(string tenantId, CancellationToken cancellationToken)
    {
        lock (_periods) return Task.FromResult<IReadOnlyList<GlClosedPeriod>>(_periods.Values.Where(p => p.TenantId == tenantId).ToList());
    }

    public Task<GlAccount?> FindAsync(string tenantId, string accountNumber, CancellationToken cancellationToken = default)
    {
        var matches = Chart.Where(a => a.TenantId == tenantId && a.AccountNumber == accountNumber).ToList();
        return Task.FromResult(matches.Count == 1 ? matches[0] : null);
    }
}

internal sealed class GlClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 5, 20, 15, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

internal static class GlFixtures
{
    public const string Tenant = "tenant-1";

    public static readonly Dictionary<GlPostingRole, string> Numbers = new()
    {
        [GlPostingRole.ClaimsExpense] = "5100",
        [GlPostingRole.ClaimsPayable] = "2100",
        [GlPostingRole.AchInTransit] = "1015",
        [GlPostingRole.ProviderReceivable] = "1250",
        [GlPostingRole.Cash] = "1010",
    };

    public static GlPostingOptions Options(bool enabled = true, params GlPostingRole[] unmapped)
        => new()
        {
            Enabled = enabled,
            Tenants =
            {
                [Tenant] = new GlTenantPostingOptions
                {
                    Accounts = Numbers.Where(n => !unmapped.Contains(n.Key)).ToDictionary(n => n.Key.ToString(), n => n.Value),
                },
            },
        };

    public static void SeedChart(InMemoryGl gl, string tenant = Tenant)
    {
        foreach (var (role, number) in Numbers)
        {
            gl.Chart.Add(new GlAccount
            {
                Id = $"acct-{tenant}-{number}",
                TenantId = tenant,
                AccountNumber = number,
                AccountName = role.ToString(),
                Status = GlAccountStatus.Active,
                EffectiveDate = new DateTime(2020, 1, 1),
            });
        }
    }

    public static GlPostingService Service(InMemoryGl gl, GlPostingOptions options, GlClock clock, ILogger<GlPostingService>? logger = null)
        => new(gl, gl, gl, gl, Microsoft.Extensions.Options.Options.Create(options), logger ?? NullLogger<GlPostingService>.Instance, clock);

    public static GlEventEnvelope Envelope<T>(string type, string sourceKey, T payload, string tenant = Tenant, string? eventId = null)
        => new()
        {
            EventId = eventId ?? GlEventTypes.EventIdFor(tenant, sourceKey, type),
            Type = type,
            TenantId = tenant,
            Source = "payment-service",
            PayloadJson = JsonSerializer.Serialize(payload, GlEventTypes.Json),
            CreatedAt = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc),
        };

    /// <summary>A run that paid 100.00 ACH and 50.00 by check, the check after a 20.00 receivable offset.</summary>
    public static GlEventEnvelope RunExecuted(string runId = "run-1", DateTime? executedAt = null, decimal ach = 100m, decimal check = 50m, decimal offset = 20m)
    {
        var payments = new List<GlPaymentLine>
        {
            new() { PaymentId = $"{runId}-p1", CheckNumber = "0001000001", PayeeNpi = "1111111111", PaymentMethod = "ACH", NetAmount = ach },
            new() { PaymentId = $"{runId}-p2", CheckNumber = "0001000002", PayeeNpi = "2222222222", PaymentMethod = "CHK", NetAmount = check, ReceivableOffsetAmount = offset },
        };
        return Envelope(GlEventTypes.PaymentRunExecuted, $"payment-run:{runId}", new PaymentRunExecutedEvent
        {
            TenantId = Tenant,
            PaymentRunId = runId,
            PaymentRunNumber = $"PR-{runId}",
            RunStatus = "Completed",
            PaymentDate = new DateTime(2026, 5, 21),
            ExecutedAt = executedAt ?? new DateTime(2026, 5, 20, 14, 0, 0, DateTimeKind.Utc),
            ExecutedBy = "approver-1",
            Payments = payments,
            TotalNetAmount = payments.Sum(p => p.NetAmount),
            TotalReceivableOffsetAmount = payments.Sum(p => p.ReceivableOffsetAmount),
        });
    }

    public static GlEventEnvelope FileTransmitted(string runId = "run-1", string fileReference = "FFS-PR-run-1", decimal amount = 100m, DateTime? at = null)
        => Envelope(GlEventTypes.PaymentFileTransmitted, $"file:{fileReference}", new PaymentFileTransmittedEvent
        {
            TenantId = Tenant,
            PaymentRunId = runId,
            PaymentRunNumber = $"PR-{runId}",
            FileReference = fileReference,
            Sha256 = new string('a', 64),
            EntryCount = 1,
            TotalCreditAmount = amount,
            EffectiveEntryDate = new DateTime(2026, 5, 21),
            TransmittedAt = at ?? new DateTime(2026, 5, 20, 16, 0, 0, DateTimeKind.Utc),
            ApprovedBy = "treasury-1",
            ConfirmedBy = "Upload",
        });

    public static GlEventEnvelope ReversalExecuted(string reversalRunId = "rr-1", decimal amount = 100m)
        => Envelope(GlEventTypes.ReversalRunExecuted, $"reversal-run:{reversalRunId}", new ReversalRunExecutedEvent
        {
            TenantId = Tenant,
            ReversalRunId = reversalRunId,
            ReversalRunNumber = $"RR-{reversalRunId}",
            RunStatus = "Completed",
            ExecutedAt = new DateTime(2026, 5, 22, 10, 0, 0, DateTimeKind.Utc),
            ExecutedBy = "approver-3",
            Reversals = [new GlReversalLine { ReversalPaymentId = $"{reversalRunId}-p1", CheckNumber = "0001000009", PayeeNpi = "1111111111", Amount = amount }],
            TotalAmount = amount,
        });
}
