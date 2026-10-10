using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ArService.Models;
using CloudHealthOffice.Finance.Contracts;
using Microsoft.Extensions.Options;

namespace ArService.Gl;

/// <summary>GL posting is switched off (<c>GlPosting:Enabled</c>). Nothing was stored.</summary>
public sealed class GlPostingDisabledException : Exception
{
    public const string Code = "GlPostingDisabled";
    public GlPostingDisabledException() : base("GL posting is disabled (GlPosting:Enabled is false). Nothing was stored; the event stays with its producer.") { }
}

/// <summary>The same event id arrived with a different payload (409): never overwritten.</summary>
public sealed class GlEventConflictException : Exception
{
    public GlEventConflictException(string message) : base(message) { }
}

/// <summary>The request is not allowed in the record's or entry's state (409).</summary>
public sealed class GlStateException : Exception
{
    public GlStateException(string message) : base(message) { }
}

/// <summary>The caller may not do this (403).</summary>
public sealed class GlForbiddenException : Exception
{
    public GlForbiddenException(string message) : base(message) { }
}

/// <summary>What became of one delivered event.</summary>
public sealed class GlIngestResult
{
    public string EventId { get; init; } = string.Empty;
    public GlSourceEventStatus Status { get; init; }
    public GlParkReason? ParkReason { get; init; }
    public string? ParkDetail { get; init; }
    public string? EntryId { get; init; }

    /// <summary>True when the event had been received before (nothing new happened).</summary>
    public bool Duplicate { get; init; }

    public static GlIngestResult From(GlSourceEvent e, bool duplicate) => new()
    {
        EventId = e.EventId, Status = e.Status, ParkReason = e.ParkReason, ParkDetail = e.ParkDetail, EntryId = e.EntryId, Duplicate = duplicate,
    };
}

/// <summary>
/// Posts GL source events to the double-entry journal.
/// <list type="bullet">
/// <item>Idempotent twice over: on the event id (the register), and on the entry's
/// business key (one accrual per payment run, one recoupment per reversal run, one ACH
/// entry per payment run), so neither a redelivery nor a second event about the same run
/// (e.g. a re-dated file) posts twice.</item>
/// <item>Never drops an event: one that cannot post (no mapped or no active account, closed
/// period, duplicate business key, invalid payload) is parked with the reason, and posts
/// when an operator retries it after the cause is fixed.</item>
/// <item>Append-only: corrections are reversing entries.</item>
/// </list>
/// </summary>
public sealed class GlPostingService
{
    public static readonly EventId PostedEvent = new(4961, "GlEntryPosted");
    public static readonly EventId ParkedEvent = new(4962, "GlEventParked");
    public static readonly EventId ReversedEvent = new(4963, "GlEntryReversed");
    public static readonly EventId PeriodClosedEvent = new(4964, "GlPeriodClosed");
    public static readonly EventId DismissedEvent = new(4965, "GlEventDismissed");

    private static readonly Regex SafeId = new("^[A-Za-z0-9._:\\-]{1,128}$", RegexOptions.Compiled);
    private static readonly Regex PeriodFormat = new("^[0-9]{4}-(0[1-9]|1[0-2])$", RegexOptions.Compiled);

    private readonly IGlJournalRepository _journal;
    private readonly IGlSourceEventRepository _events;
    private readonly IGlPeriodRepository _periods;
    private readonly IGlChartLookup _chart;
    private readonly GlPostingOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<GlPostingService> _logger;

    public GlPostingService(
        IGlJournalRepository journal, IGlSourceEventRepository events, IGlPeriodRepository periods, IGlChartLookup chart,
        IOptions<GlPostingOptions> options, ILogger<GlPostingService> logger, TimeProvider? clock = null)
    {
        _journal = journal;
        _events = events;
        _periods = periods;
        _chart = chart;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public bool Enabled => _options.Enabled;

    public static bool IsPeriod(string? period) => period != null && PeriodFormat.IsMatch(period);

    public async Task<GlIngestResult> IngestAsync(GlEventEnvelope envelope, string tokenTenantId, CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        if (string.IsNullOrEmpty(envelope.EventId) || !SafeId.IsMatch(envelope.EventId))
            throw new ArgumentException("eventId is missing or not a plain identifier.");
        if (!GlEventTypes.All.Contains(envelope.Type))
            throw new ArgumentException($"Unknown GL event type '{envelope.Type}'.");
        if (!string.Equals(envelope.TenantId, tokenTenantId, StringComparison.Ordinal))
            throw new GlForbiddenException("The event's tenant is not the token's tenant.");

        var sha = Sha256(envelope.PayloadJson);
        var existing = await _events.GetAsync(tokenTenantId, envelope.EventId, cancellationToken);
        if (existing != null)
            return Duplicate(existing, sha);

        var record = new GlSourceEvent
        {
            TenantId = tokenTenantId,
            EventId = envelope.EventId,
            Type = envelope.Type,
            Source = envelope.Source,
            PayloadJson = envelope.PayloadJson,
            PayloadSha256 = sha,
            ReceivedAt = Now,
            Attempts = 1,
            LastAttemptAt = Now,
            LastAttemptBy = string.IsNullOrEmpty(envelope.Source) ? "service" : envelope.Source,
        };
        // The journal first: a crash after it leaves an entry carrying this event id, which
        // the redelivery recognises as its own.
        await PostAsync(envelope, record, entryDateOverride: null, reason: null, record.LastAttemptBy, cancellationToken);
        if (!await _events.TryInsertAsync(record, cancellationToken))
        {
            var raced = await _events.GetAsync(tokenTenantId, envelope.EventId, cancellationToken)
                        ?? throw new GlStateException($"GL event {envelope.EventId} could not be recorded; deliver it again.");
            return Duplicate(raced, sha);
        }
        return GlIngestResult.From(record, duplicate: false);
    }

    private static GlIngestResult Duplicate(GlSourceEvent existing, string sha)
    {
        if (!string.Equals(existing.PayloadSha256, sha, StringComparison.Ordinal))
            throw new GlEventConflictException(
                $"GL event {existing.EventId} was received before with a different payload; the first one stands.");
        return GlIngestResult.From(existing, duplicate: true);
    }

    /// <summary>
    /// Retries a parked event (after its mapping, chart or period was fixed). For a closed
    /// period, <paramref name="entryDate"/> moves it to a later date in an open period,
    /// with a reason (both recorded on the entry).
    /// </summary>
    public async Task<GlIngestResult> RetryAsync(string tenantId, string eventId, DateTime? entryDate, string? reason, string actor, CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        var record = await _events.GetAsync(tenantId, eventId, cancellationToken)
                     ?? throw new KeyNotFoundException($"No GL event {eventId}.");
        if (record.Status != GlSourceEventStatus.Parked)
            throw new GlStateException($"GL event {eventId} is {record.Status}, not parked.");
        if (entryDate != null && string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A reason is required to post an event on another date.");

        var envelope = new GlEventEnvelope
        {
            EventId = record.EventId, Type = record.Type, TenantId = record.TenantId, Source = record.Source, PayloadJson = record.PayloadJson,
        };
        if (entryDate != null)
        {
            var original = SafeBuild(envelope)?.EntryDate;
            if (original != null && entryDate.Value.Date < original.Value.Date)
                throw new ArgumentException($"An entry is moved to a later date, never an earlier one (its date is {original:yyyy-MM-dd}).");
        }

        var readVersion = record.Version;
        record.Attempts++;
        record.LastAttemptAt = Now;
        record.LastAttemptBy = actor;
        await PostAsync(envelope, record, entryDate?.Date, reason == null ? null : Clean(reason), actor, cancellationToken);
        if (!await _events.TryReplaceAsync(record, readVersion, cancellationToken))
            throw new GlStateException($"GL event {eventId} changed meanwhile; check it and retry.");
        return GlIngestResult.From(record, duplicate: false);
    }

    /// <summary>A parked event finance decided not to post (e.g. a duplicate transmission): kept, with the reason.</summary>
    public async Task<GlIngestResult> DismissAsync(string tenantId, string eventId, string reason, string actor, CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A reason is required to dismiss a parked GL event.");
        var record = await _events.GetAsync(tenantId, eventId, cancellationToken)
                     ?? throw new KeyNotFoundException($"No GL event {eventId}.");
        if (record.Status != GlSourceEventStatus.Parked)
            throw new GlStateException($"GL event {eventId} is {record.Status}, not parked.");
        var readVersion = record.Version;
        record.Status = GlSourceEventStatus.Dismissed;
        record.ParkDetail = $"dismissed by {actor}: {Clean(reason)} (was parked: {record.ParkReason} {record.ParkDetail})";
        record.LastAttemptAt = Now;
        record.LastAttemptBy = actor;
        if (!await _events.TryReplaceAsync(record, readVersion, cancellationToken))
            throw new GlStateException($"GL event {eventId} changed meanwhile; check it and retry.");
        _logger.LogWarning(DismissedEvent, "AUDIT GL event {EventId} ({Type}) of tenant {TenantId} dismissed by {User}: {Reason}",
            record.EventId, record.Type, Clean(tenantId), Clean(actor), Clean(reason));
        return GlIngestResult.From(record, duplicate: false);
    }

    /// <summary>
    /// The mirror image of <paramref name="entryId"/>, dated <paramref name="entryDate"/> (default
    /// today), in an open period. An entry is reversed at most once; a reversal is not reversed.
    /// </summary>
    public async Task<GlJournalEntry> ReverseAsync(string tenantId, string entryId, string reason, DateTime? entryDate, string actor, CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A reason is required to reverse a GL entry.");
        var original = await _journal.GetAsync(tenantId, entryId, cancellationToken)
                       ?? throw new KeyNotFoundException($"No GL entry {entryId}.");
        if (original.Kind == GlEntryKind.Reversal)
            throw new GlStateException($"GL entry {entryId} is itself a reversal; post a new entry instead of reversing it.");

        var date = (entryDate ?? Now).Date;
        if (date < original.EntryDate.Date)
            throw new ArgumentException($"A reversal is dated on or after the entry it reverses ({original.EntryDate:yyyy-MM-dd}).");
        var period = GlJournal.PeriodOf(date);
        if (await _periods.IsClosedAsync(tenantId, period, cancellationToken))
            throw new GlStateException($"Period {period} is closed; date the reversal in an open period.");

        var reversal = new GlJournalEntry
        {
            Id = $"{original.Id}:reversal",
            TenantId = tenantId,
            Kind = GlEntryKind.Reversal,
            SourceEventId = original.SourceEventId,
            SourceType = original.SourceType,
            SourceDocumentId = original.SourceDocumentId,
            SourceReference = original.SourceReference,
            EntryDate = date,
            Period = period,
            PostedAt = Now,
            PostedBy = actor,
            LineOfBusiness = original.LineOfBusiness,
            Description = $"Reversal of {original.Id}: {original.Description}",
            ReversesEntryId = original.Id,
            Reason = Clean(reason),
            Lines = original.Lines.Select(l => new GlJournalLine
            {
                LineNumber = l.LineNumber,
                Role = l.Role,
                AccountId = l.AccountId,
                AccountNumber = l.AccountNumber,
                Debit = l.Credit,
                Credit = l.Debit,
                Memo = $"reversal: {l.Memo}",
            }).ToList(),
            TotalDebit = original.TotalCredit,
            TotalCredit = original.TotalDebit,
        };
        if (!await _journal.TryInsertAsync(reversal, cancellationToken))
            throw new GlStateException($"GL entry {entryId} has already been reversed.");
        _logger.LogWarning(ReversedEvent, "AUDIT GL entry {EntryId} of tenant {TenantId} reversed by {User} on {Date:yyyy-MM-dd}: {Reason}",
            original.Id, Clean(tenantId), Clean(actor), date, Clean(reason));
        return reversal;
    }

    /// <summary>Closes a period (yyyy-MM, not in the future). Idempotent. Nothing posts into a closed period.</summary>
    public async Task<GlClosedPeriod> ClosePeriodAsync(string tenantId, string period, string reason, string actor, CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        if (!IsPeriod(period))
            throw new ArgumentException("A period is yyyy-MM.");
        if (string.CompareOrdinal(period, GlJournal.PeriodOf(Now)) > 0)
            throw new ArgumentException($"Period {period} is in the future; it cannot be closed yet.");
        var closed = new GlClosedPeriod { TenantId = tenantId, Period = period, ClosedAt = Now, ClosedBy = actor, Reason = Clean(reason) };
        if (await _periods.TryCloseAsync(closed, cancellationToken))
            _logger.LogWarning(PeriodClosedEvent, "AUDIT GL period {Period} of tenant {TenantId} closed by {User}: {Reason}",
                period, Clean(tenantId), Clean(actor), Clean(reason));
        return closed;
    }

    /// <summary>Builds, resolves and inserts the entry; sets <paramref name="record"/>'s status. Never throws for a postable-later event.</summary>
    private async Task PostAsync(GlEventEnvelope envelope, GlSourceEvent record, DateTime? entryDateOverride, string? reason, string actor,
        CancellationToken cancellationToken)
    {
        GlProposedEntry? proposal;
        try
        {
            proposal = GlEntryBuilder.Build(envelope);
        }
        catch (GlInvalidPayloadException ex)
        {
            Park(record, GlParkReason.InvalidPayload, ex.Message);
            return;
        }
        if (proposal == null)
        {
            record.Status = GlSourceEventStatus.NothingToPost;
            record.ParkReason = null;
            record.ParkDetail = "every amount is zero";
            return;
        }

        record.SourceDocumentId = proposal.SourceDocumentId;
        record.SourceReference = proposal.SourceReference;
        record.ClaimedAmount = proposal.ClaimedAmount;

        var entryDate = entryDateOverride ?? proposal.EntryDate.Date;
        var lines = new List<GlJournalLine>();
        var missing = new List<string>();
        foreach (var (role, debit, credit, memo) in proposal.Lines)
        {
            var number = _options.AccountFor(record.TenantId, role);
            if (number == null)
            {
                missing.Add($"{role} has no account mapped (GlPosting:Tenants:{record.TenantId}:Accounts:{role})");
                continue;
            }
            var account = await _chart.FindAsync(record.TenantId, number, cancellationToken);
            var problem = AccountProblem(account, number, entryDate);
            if (problem != null)
            {
                missing.Add($"{role}: {problem}");
                continue;
            }
            lines.Add(new GlJournalLine
            {
                LineNumber = lines.Count + 1,
                Role = role,
                AccountId = account!.Id,
                AccountNumber = account.AccountNumber,
                Debit = debit,
                Credit = credit,
                Memo = memo,
            });
        }
        if (missing.Count > 0)
        {
            Park(record, GlParkReason.NeedsMapping, string.Join("; ", missing));
            return;
        }

        var period = GlJournal.PeriodOf(entryDate);
        if (await _periods.IsClosedAsync(record.TenantId, period, cancellationToken))
        {
            Park(record, GlParkReason.ClosedPeriod,
                $"its date {entryDate:yyyy-MM-dd} is in closed period {period}; retry it with a date in an open period");
            return;
        }

        var entry = new GlJournalEntry
        {
            Id = GlJournal.KeyFor(record.TenantId, proposal.Key),
            TenantId = record.TenantId,
            Kind = proposal.Kind,
            SourceEventId = record.EventId,
            SourceType = record.Type,
            SourceDocumentId = proposal.SourceDocumentId,
            SourceReference = proposal.SourceReference,
            EntryDate = DateTime.SpecifyKind(entryDate, DateTimeKind.Utc),
            Period = period,
            PostedAt = Now,
            PostedBy = string.IsNullOrEmpty(proposal.PostedBy) ? actor : proposal.PostedBy,
            LineOfBusiness = proposal.LineOfBusiness,
            Description = proposal.Description,
            Reason = entryDateOverride != null ? $"dated {entryDate:yyyy-MM-dd} instead of {proposal.EntryDate:yyyy-MM-dd} by {actor}: {reason}" : null,
            Lines = lines,
            TotalDebit = lines.Sum(l => l.Debit),
            TotalCredit = lines.Sum(l => l.Credit),
        };
        GlJournal.EnsureBalanced(entry);

        if (!await _journal.TryInsertAsync(entry, cancellationToken))
        {
            var existing = await _journal.GetAsync(record.TenantId, entry.Id, cancellationToken);
            if (existing == null || !string.Equals(existing.SourceEventId, record.EventId, StringComparison.Ordinal))
            {
                Park(record, GlParkReason.DuplicateBusinessKey,
                    $"{proposal.SourceReference} already has {proposal.Kind} entry {entry.Id} from event {existing?.SourceEventId}; " +
                    "this event would post it twice. Review it and dismiss it, or reverse the existing entry first.");
                return;
            }
            entry = existing;
        }

        record.Status = GlSourceEventStatus.Posted;
        record.ParkReason = null;
        record.ParkDetail = null;
        record.EntryId = entry.Id;
        _logger.LogInformation(PostedEvent,
            "AUDIT GL entry {EntryId} ({Kind}) of tenant {TenantId} posted from event {EventId} ({Reference}) on {Date:yyyy-MM-dd}: {Total:F2}",
            entry.Id, entry.Kind, Clean(record.TenantId), record.EventId, Clean(entry.SourceReference), entry.EntryDate, entry.TotalDebit);
    }

    private static string? AccountProblem(GlAccount? account, string number, DateTime entryDate)
    {
        if (account == null)
            return $"account {number} is not in the chart (or is there more than once)";
        if (account.Status != GlAccountStatus.Active)
            return $"account {number} is {account.Status}";
        if (account.EffectiveDate.Date > entryDate.Date)
            return $"account {number} is effective only from {account.EffectiveDate:yyyy-MM-dd}";
        if (account.TerminationDate != null && account.TerminationDate.Value.Date <= entryDate.Date)
            return $"account {number} was terminated on {account.TerminationDate:yyyy-MM-dd}";
        return null;
    }

    private void Park(GlSourceEvent record, GlParkReason reason, string detail)
    {
        record.Status = GlSourceEventStatus.Parked;
        record.ParkReason = reason;
        record.ParkDetail = Clean(detail);
        record.EntryId = null;
        _logger.LogWarning(ParkedEvent, "AUDIT GL event {EventId} ({Type}) of tenant {TenantId} parked ({Reason}): {Detail}",
            record.EventId, record.Type, Clean(record.TenantId), reason, Clean(detail));
    }

    private static GlProposedEntry? SafeBuild(GlEventEnvelope envelope)
    {
        try { return GlEntryBuilder.Build(envelope); }
        catch (GlInvalidPayloadException) { return null; }
    }

    private void EnsureEnabled()
    {
        if (!_options.Enabled)
            throw new GlPostingDisabledException();
    }

    private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string Clean(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", " ");
}
