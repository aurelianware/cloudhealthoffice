using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ArService.Models;
using CloudHealthOffice.Finance.Contracts;
using Microsoft.Extensions.Options;
using CloudHealthOffice.Infrastructure.Logging;

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

/// <summary>A closed period's journal no longer matches the extract recorded at close: nothing is extracted.</summary>
public sealed class GlIntegrityException : Exception
{
    public GlIntegrityException(string message) : base(message) { }
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
    public static readonly EventId RepostedEvent = new(4966, "GlEventReposted");
    public static readonly EventId IntegrityEvent = new(4967, "GlClosedPeriodIntegrity");

    /// <summary>How long a post may hold its period open against a close (the close ignores an expired lease).</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>A post that ran longer than this before its write is abandoned (well inside <see cref="LeaseDuration"/>).</summary>
    public static readonly TimeSpan MaxPostDuration = TimeSpan.FromMinutes(1);

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
        await ReleaseWaitersAsync(record, cancellationToken);
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
    /// with a reason (both recorded on the entry). A PreCutover event posts only with
    /// <paramref name="includeBeforeCutover"/> and a reason (its accounting must then not be
    /// in the opening balances as well).
    /// </summary>
    public async Task<GlIngestResult> RetryAsync(string tenantId, string eventId, DateTime? entryDate, string? reason, string actor,
        CancellationToken cancellationToken = default, bool includeBeforeCutover = false)
    {
        EnsureEnabled();
        var record = await _events.GetAsync(tenantId, eventId, cancellationToken)
                     ?? throw new KeyNotFoundException($"No GL event {eventId}.");
        if ((entryDate != null || includeBeforeCutover) && string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A reason is required to post an event on another date or from before the cutover.");
        var result = await RetryCoreAsync(record, entryDate, reason, actor, includeBeforeCutover, cancellationToken);
        await ReleaseWaitersAsync(result, cancellationToken);
        return GlIngestResult.From(result, duplicate: false);
    }

    /// <summary>The system actor of automatic re-checks (an event's dependency settled).</summary>
    public const string RecheckActor = "gl-recheck";

    private async Task<GlSourceEvent> RetryCoreAsync(GlSourceEvent record, DateTime? entryDate, string? reason, string actor, bool includeBeforeCutover,
        CancellationToken cancellationToken)
    {
        if (record.Status != GlSourceEventStatus.Parked)
            throw new GlStateException($"GL event {record.EventId} is {record.Status}, not parked.");
        var envelope = EnvelopeOf(record);
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
        await PostAsync(envelope, record, entryDate?.Date, reason == null ? null : FlattenLineBreaks(reason), actor, cancellationToken,
            new PostOptions { IncludeBeforeCutover = includeBeforeCutover });
        if (!await _events.TryReplaceAsync(record, readVersion, cancellationToken))
            throw new GlStateException($"GL event {record.EventId} changed meanwhile; check it and retry.");
        return record;
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
        record.ParkDetail = $"dismissed by {actor}: {FlattenLineBreaks(reason)} (was parked: {record.ParkReason} {record.ParkDetail})";
        record.LastAttemptAt = Now;
        record.LastAttemptBy = actor;
        if (!await _events.TryReplaceAsync(record, readVersion, cancellationToken))
            throw new GlStateException($"GL event {eventId} changed meanwhile; check it and retry.");
        _logger.LogWarning(DismissedEvent, "AUDIT GL event {EventId} ({Type}) of tenant {TenantId} dismissed by {User}: {Reason}",
            LogSanitizer.Sanitize(record.EventId), LogSanitizer.Sanitize(record.Type), LogSanitizer.Sanitize(tenantId), LogSanitizer.Sanitize(actor), LogSanitizer.Sanitize(reason));
        return GlIngestResult.From(record, duplicate: false);
    }

    /// <summary>
    /// Posts an event again after the entry of its business key was reversed: a parked event
    /// (e.g. DuplicateBusinessKey: a second transmitted file for a run whose first ACH entry was
    /// reversed) or a posted one whose own entry was reversed (e.g. to post it on another date).
    /// The new entry is <c>{key}:r{n}</c>, naming the reversed entry it replaces, so the journal
    /// keeps one live entry per business key. Refused while the latest entry of the key stands.
    /// Nothing changes when the repost cannot post (unmapped account, closed period).
    /// </summary>
    public async Task<GlIngestResult> RepostAsync(string tenantId, string eventId, string reason, DateTime? entryDate, string actor,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A reason is required to repost a GL event.");
        var record = await _events.GetAsync(tenantId, eventId, cancellationToken)
                     ?? throw new KeyNotFoundException($"No GL event {eventId}.");
        if (record.Status is not (GlSourceEventStatus.Posted or GlSourceEventStatus.Parked))
            throw new GlStateException($"GL event {eventId} is {record.Status}; only a posted or parked event is reposted.");
        if (record.ParkReason is GlParkReason.InvalidPayload or GlParkReason.PreCutover)
            throw new GlStateException($"GL event {eventId} is parked as {record.ParkReason}; a repost does not post it.");
        var envelope = EnvelopeOf(record);
        var proposal = SafeBuild(envelope)
                       ?? throw new GlStateException($"GL event {eventId} has nothing to post.");
        if (entryDate != null && entryDate.Value.Date < proposal.EntryDate.Date)
            throw new ArgumentException($"An entry is moved to a later date, never an earlier one (its date is {proposal.EntryDate:yyyy-MM-dd}).");

        var baseId = GlJournal.KeyFor(tenantId, proposal.Key);
        GlJournalEntry? latest = null;
        var n = 0;
        for (var i = 0; ; i++)
        {
            var candidate = await _journal.GetAsync(tenantId, GlJournal.RepostId(baseId, i), cancellationToken);
            if (candidate == null)
                break;
            latest = candidate;
            n = i;
        }
        if (latest == null)
            throw new GlStateException($"No entry of {proposal.Key} exists; retry the event instead of reposting it.");
        var reversalOfLatest = await _journal.GetAsync(tenantId, GlJournal.ReversalId(latest.Id), cancellationToken)
                               ?? throw new GlStateException($"GL entry {latest.Id} of {proposal.Key} stands; reverse it before reposting the event.");
        if (string.Equals(reversalOfLatest.PostedBy, actor, StringComparison.OrdinalIgnoreCase))
            throw new GlForbiddenException($"You reversed {latest.Id}; another finance user must repost it.");

        var attempt = Copy(record);
        attempt.Attempts++;
        attempt.LastAttemptAt = Now;
        attempt.LastAttemptBy = actor;
        await PostAsync(envelope, attempt, entryDate?.Date, $"repost of {latest.Id}: {FlattenLineBreaks(reason)}", actor, cancellationToken,
            new PostOptions { EntryId = GlJournal.RepostId(baseId, n + 1), RepostOf = latest.Id });
        if (attempt.Status != GlSourceEventStatus.Posted)
            throw new GlStateException($"GL event {eventId} cannot be reposted: {attempt.ParkReason} {attempt.ParkDetail}. Nothing was posted.");

        if (!await _events.TryReplaceAsync(attempt, record.Version, cancellationToken))
            throw new GlStateException($"GL event {eventId} changed meanwhile; its repost {attempt.EntryId} is in the journal. Check the event.");
        _logger.LogWarning(RepostedEvent, "AUDIT GL event {EventId} of tenant {TenantId} reposted by {User} as {EntryId} (replaces reversed {Previous}): {Reason}",
            LogSanitizer.Sanitize(eventId), LogSanitizer.Sanitize(tenantId), LogSanitizer.Sanitize(actor), LogSanitizer.Sanitize(attempt.EntryId), LogSanitizer.Sanitize(latest.Id), LogSanitizer.Sanitize(reason));
        return GlIngestResult.From(attempt, duplicate: false);
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

        var reversal = new GlJournalEntry
        {
            Id = GlJournal.ReversalId(original.Id),
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
            Reason = FlattenLineBreaks(reason),
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
        bool inserted;
        var lease = await BeginPostAsync(tenantId, period, cancellationToken)
                    ?? throw new GlStateException($"Period {period} is closed; date the reversal in an open period.");
        try
        {
            lease.EnsureFresh();
            inserted = await _journal.TryInsertAsync(reversal, cancellationToken);
        }
        finally
        {
            await EndPostAsync(lease);
        }
        if (!inserted)
            throw new GlStateException($"GL entry {entryId} has already been reversed.");
        _logger.LogWarning(ReversedEvent, "AUDIT GL entry {EntryId} of tenant {TenantId} reversed by {User} on {Date:yyyy-MM-dd}: {Reason}",
            LogSanitizer.Sanitize(original.Id), LogSanitizer.Sanitize(tenantId), LogSanitizer.Sanitize(actor), date, LogSanitizer.Sanitize(reason));
        return reversal;
    }

    /// <summary>
    /// Closes a period (yyyy-MM, not in the future). Idempotent. Serialized with posts: refused
    /// (nothing changes) while a post is in flight in the period; once closed nothing posts into
    /// it. The period's extract control (SHA-256, totals) is recorded at close.
    /// </summary>
    public async Task<GlPeriod> ClosePeriodAsync(string tenantId, string period, string reason, string actor, CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        if (!IsPeriod(period))
            throw new ArgumentException("A period is yyyy-MM.");
        if (string.CompareOrdinal(period, GlJournal.PeriodOf(Now)) > 0)
            throw new ArgumentException($"Period {period} is in the future; it cannot be closed yet.");
        var outcome = await _periods.TryCloseAsync(tenantId, period, Now, actor, FlattenLineBreaks(reason), cancellationToken);
        if (outcome == GlPeriodCloseOutcome.Busy)
            throw new GlStateException($"Entries are being posted into period {period} right now; it was not closed. Close it again in a moment.");
        if (outcome == GlPeriodCloseOutcome.Closed)
            _logger.LogWarning(PeriodClosedEvent, "AUDIT GL period {Period} of tenant {TenantId} closed by {User}: {Reason}",
                LogSanitizer.Sanitize(period), LogSanitizer.Sanitize(tenantId), LogSanitizer.Sanitize(actor), LogSanitizer.Sanitize(reason));
        // Nothing can post into it any more: its extract is final.
        await GetExtractAsync(tenantId, period, cancellationToken);
        return await _periods.GetAsync(tenantId, period, cancellationToken)
               ?? throw new GlStateException($"Period {period} disappeared after closing.");
    }

    /// <summary>
    /// The period's extract. For a closed period the extract control recorded at close must
    /// match (it is recorded on first use for a period closed before that); a difference is an
    /// integrity failure and nothing is returned.
    /// </summary>
    public async Task<GlExtract> GetExtractAsync(string tenantId, string period, CancellationToken cancellationToken = default)
    {
        if (!IsPeriod(period))
            throw new ArgumentException("A period is yyyy-MM.");
        var state = await _periods.GetAsync(tenantId, period, cancellationToken);
        var extract = GlExtractBuilder.Build(tenantId, period, await _journal.ListAsync(tenantId, period, cancellationToken));
        if (state?.Status != GlPeriodStatus.Closed)
            return extract;
        if (state.ExtractSha256 == null)
        {
            if (await _periods.TrySetExtractControlAsync(tenantId, period, extract.Control, cancellationToken))
                return extract;
            state = await _periods.GetAsync(tenantId, period, cancellationToken);
        }
        if (!string.Equals(state?.ExtractSha256, extract.Control.Sha256, StringComparison.Ordinal))
        {
            _logger.LogCritical(IntegrityEvent,
                "AUDIT GL closed period {Period} of tenant {TenantId}: extract sha256 {Now} differs from {AtClose} recorded at close; extract refused",
                LogSanitizer.Sanitize(period), LogSanitizer.Sanitize(tenantId), extract.Control.Sha256, LogSanitizer.Sanitize(state?.ExtractSha256));
            throw new GlIntegrityException(
                $"The journal of closed period {period} no longer matches the extract recorded at close (sha256 {state?.ExtractSha256}). " +
                "Nothing was extracted; this needs a person.");
        }
        return extract;
    }

    /// <summary>How a post differs from the first post of an event.</summary>
    private sealed class PostOptions
    {
        /// <summary>The entry id instead of the business key (a repost: <c>{key}:r{n}</c>).</summary>
        public string? EntryId { get; init; }
        public string? RepostOf { get; init; }
        public bool IncludeBeforeCutover { get; init; }
    }

    /// <summary>A lease held in a period while one entry is written.</summary>
    private sealed class PostLease
    {
        public required string TenantId { get; init; }
        public required string Period { get; init; }
        public required string Token { get; init; }
        public required DateTime StartedAt { get; init; }
        public required Func<DateTime> Clock { get; init; }

        /// <summary>
        /// Refuses to write once the post took longer than <see cref="MaxPostDuration"/>: well
        /// before its lease expires, so a close (which ignores only expired leases) can never
        /// overtake a write that is still running.
        /// </summary>
        public void EnsureFresh()
        {
            if (Clock() - StartedAt > MaxPostDuration)
                throw new GlStateException($"Posting into period {Period} took too long and was abandoned (nothing was written); deliver or retry it again.");
        }
    }

    private async Task<PostLease?> BeginPostAsync(string tenantId, string period, CancellationToken cancellationToken)
    {
        var token = Guid.NewGuid().ToString("N");
        var started = Now;
        return await _periods.TryBeginPostAsync(tenantId, period, token, started.Add(LeaseDuration), cancellationToken)
            ? new PostLease { TenantId = tenantId, Period = period, Token = token, StartedAt = started, Clock = () => Now }
            : null;
    }

    private async Task EndPostAsync(PostLease lease)
    {
        try
        {
            await _periods.EndPostAsync(lease.TenantId, lease.Period, lease.Token, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The lease expires on its own; until then the period cannot be closed.
            _logger.LogWarning("GL period {Period} lease could not be released ({Error}); it expires within {Lease}",
                LogSanitizer.Sanitize(lease.Period), ex.GetType().Name, LeaseDuration);
        }
    }

    /// <summary>Builds, resolves and inserts the entry; sets <paramref name="record"/>'s status. Never throws for a postable-later event.</summary>
    private async Task PostAsync(GlEventEnvelope envelope, GlSourceEvent record, DateTime? entryDateOverride, string? reason, string actor,
        CancellationToken cancellationToken, PostOptions? options = null)
    {
        options ??= new PostOptions();
        if (envelope.Type == GlEventTypes.ProviderRefundOwed)
        {
            HoldForManualPosting(envelope, record);
            return;
        }
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

        if (await CutoverHoldAsync(record, proposal, cancellationToken) is { } hold
            && !(options.IncludeBeforeCutover && hold.Reason == GlParkReason.PreCutover))
        {
            Park(record, hold.Reason, hold.Detail, hold.WaitingFor);
            return;
        }

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
        var notes = new List<string>();
        if (entryDateOverride != null)
            notes.Add($"dated {entryDate:yyyy-MM-dd} instead of {proposal.EntryDate:yyyy-MM-dd} by {actor}");
        if (options.IncludeBeforeCutover)
            notes.Add($"posted from before the GL cutover by {actor}");
        if (options.RepostOf != null)
            notes.Add($"reposted by {actor}");
        var entry = new GlJournalEntry
        {
            Id = options.EntryId ?? GlJournal.KeyFor(record.TenantId, proposal.Key),
            TenantId = record.TenantId,
            Kind = proposal.Kind,
            SourceEventId = record.EventId,
            SourceType = record.Type,
            SourceDocumentId = proposal.SourceDocumentId,
            SourceReference = proposal.SourceReference,
            EntryDate = DateTime.SpecifyKind(entryDate, DateTimeKind.Utc),
            Period = period,
            PostedAt = Now,
            PostedBy = string.IsNullOrEmpty(proposal.PostedBy) || options.RepostOf != null ? actor : proposal.PostedBy,
            LineOfBusiness = proposal.LineOfBusiness,
            Description = proposal.Description,
            Reason = notes.Count == 0 && reason == null ? null
                : string.Join(": ", new[] { string.Join("; ", notes), reason }.Where(x => !string.IsNullOrEmpty(x))),
            RepostOf = options.RepostOf,
            Lines = lines,
            TotalDebit = lines.Sum(l => l.Debit),
            TotalCredit = lines.Sum(l => l.Credit),
        };
        GlJournal.EnsureBalanced(entry);

        // Serialized with the period close: the lease is held from here until the write is done.
        var lease = await BeginPostAsync(record.TenantId, period, cancellationToken);
        if (lease == null)
        {
            Park(record, GlParkReason.ClosedPeriod,
                $"its date {entryDate:yyyy-MM-dd} is in closed period {period}; retry it with a date in an open period");
            return;
        }
        bool inserted;
        try
        {
            lease.EnsureFresh();
            inserted = await _journal.TryInsertAsync(entry, cancellationToken);
        }
        finally
        {
            await EndPostAsync(lease);
        }

        if (!inserted)
        {
            var existing = await _journal.GetAsync(record.TenantId, entry.Id, cancellationToken);
            if (existing == null || !string.Equals(existing.SourceEventId, record.EventId, StringComparison.Ordinal))
            {
                Park(record, GlParkReason.DuplicateBusinessKey,
                    $"{proposal.SourceReference} already has {proposal.Kind} entry {entry.Id} from event {existing?.SourceEventId}; " +
                    "this event would post it twice. Review it and dismiss it, or reverse the existing entry and repost this event.");
                return;
            }
            entry = existing;
        }

        record.Status = GlSourceEventStatus.Posted;
        record.ParkReason = null;
        record.ParkDetail = null;
        record.WaitingForEventId = null;
        record.EntryId = entry.Id;
        _logger.LogInformation(PostedEvent,
            "AUDIT GL entry {EntryId} ({Kind}) of tenant {TenantId} posted from event {EventId} ({Reference}) on {Date:yyyy-MM-dd}: {Total:F2}",
            LogSanitizer.Sanitize(entry.Id), entry.Kind, LogSanitizer.Sanitize(record.TenantId), LogSanitizer.Sanitize(record.EventId), LogSanitizer.Sanitize(entry.SourceReference),
            entry.EntryDate, entry.TotalDebit);
    }

    private sealed record CutoverHold(GlParkReason Reason, string Detail, string? WaitingFor);

    /// <summary>
    /// Why the event cannot post here yet because of the cutover (null when it can): its run was
    /// executed before the cutover (PreCutover), or the event it depends on to tell has not
    /// settled yet (AwaitingSourceEvent, re-checked automatically when that event posts or parks
    /// PreCutover). A void depends on its recoupment(s); a transmission without the run's
    /// execution date on the run's accrual. Never guessed.
    /// </summary>
    private async Task<CutoverHold?> CutoverHoldAsync(GlSourceEvent record, GlProposedEntry proposal, CancellationToken cancellationToken)
    {
        if (_options.CutoverFor(record.TenantId) is not { } cutover)
            return null; // tenant not configured: it parks as NeedsMapping; while enabled the validator requires a cutover
        if (proposal.RecoupmentReversalRunIds.Count > 0)
        {
            // A void settles its recoupment: it is in this GL exactly when the recoupment is.
            var recoupments = new List<(string EventId, GlSourceEvent? Event)>();
            foreach (var reversalRunId in proposal.RecoupmentReversalRunIds)
            {
                var id = GlEventTypes.EventIdFor(record.TenantId, $"reversal-run:{reversalRunId}", GlEventTypes.ReversalRunExecuted);
                recoupments.Add((id, await _events.GetAsync(record.TenantId, id, cancellationToken)));
            }
            if (recoupments.Any(r => r.Event?.Status == GlSourceEventStatus.Posted))
                return null;
            if (recoupments.All(r => r.Event is { Status: GlSourceEventStatus.Parked, ParkReason: GlParkReason.PreCutover }))
                return new CutoverHold(GlParkReason.PreCutover,
                    $"its recoupment (reversal run {string.Join(", ", proposal.RecoupmentReversalRunIds)}) is before the GL cutover {cutover:yyyy-MM-dd}; " +
                    "it is in the opening balances", null);
            var pending = recoupments.First(r => r.Event is not { Status: GlSourceEventStatus.Parked, ParkReason: GlParkReason.PreCutover });
            return new CutoverHold(GlParkReason.AwaitingSourceEvent,
                $"waiting for its recoupment (reversal run {string.Join(", ", proposal.RecoupmentReversalRunIds)}): " +
                (pending.Event == null ? "it has not arrived" : $"it is {pending.Event.Status} {pending.Event.ParkReason}") +
                "; posted automatically once it posts", pending.EventId);
        }
        if (proposal.RunExecutedAt is { } executed)
            return executed < cutover
                ? new CutoverHold(GlParkReason.PreCutover,
                    $"run {proposal.SourceReference} was executed {executed:yyyy-MM-dd}, before the GL cutover {cutover:yyyy-MM-dd}; its accounting is in the opening balances", null)
                : null;
        if (proposal.PaymentRunId == null)
            return null;
        var accrualId = GlEventTypes.EventIdFor(record.TenantId, $"payment-run:{proposal.PaymentRunId}", GlEventTypes.PaymentRunExecuted);
        var accrual = await _events.GetAsync(record.TenantId, accrualId, cancellationToken);
        if (accrual == null)
            return new CutoverHold(GlParkReason.AwaitingSourceEvent,
                $"the execution date of run {proposal.PaymentRunId} is not known yet (its PaymentRunExecuted event has not arrived), " +
                $"so it cannot be placed against the GL cutover {cutover:yyyy-MM-dd}; posted automatically once the accrual is in", accrualId);
        return accrual.Status == GlSourceEventStatus.Parked && accrual.ParkReason == GlParkReason.PreCutover
            ? new CutoverHold(GlParkReason.PreCutover, $"run {proposal.PaymentRunId} was executed before the GL cutover {cutover:yyyy-MM-dd} (its accrual is PreCutover)", null)
            : null;
    }

    /// <summary>
    /// Events parked waiting for <paramref name="settled"/> are re-checked now that it posted (or
    /// parked PreCutover), by the system; each may in turn release its own waiters.
    /// </summary>
    private async Task ReleaseWaitersAsync(GlSourceEvent settled, CancellationToken cancellationToken, int depth = 0)
    {
        if (depth > 5 || !(settled.Status == GlSourceEventStatus.Posted
                           || settled is { Status: GlSourceEventStatus.Parked, ParkReason: GlParkReason.PreCutover }))
            return;
        foreach (var waiter in await _events.ListWaitingForAsync(settled.TenantId, settled.EventId, cancellationToken))
        {
            try
            {
                var rechecked = await RetryCoreAsync(waiter, null, null, RecheckActor, false, cancellationToken);
                await ReleaseWaitersAsync(rechecked, cancellationToken, depth + 1);
            }
            catch (GlStateException ex)
            {
                // Changed meanwhile, or its period lease lapsed: it stays parked and is retried with the next event or by hand.
                _logger.LogWarning("GL event {EventId} waiting for {Settled} was not re-checked ({Error})",
                    LogSanitizer.Sanitize(waiter.EventId), LogSanitizer.Sanitize(settled.EventId), LogSanitizer.Sanitize(ex.Message));
            }
        }
    }

    /// <summary>A refund owed to a provider: registered and parked as an open item; finance posts it by hand.</summary>
    private void HoldForManualPosting(GlEventEnvelope envelope, GlSourceEvent record)
    {
        ProviderRefundOwedEvent? refund = null;
        try { refund = System.Text.Json.JsonSerializer.Deserialize<ProviderRefundOwedEvent>(envelope.PayloadJson, GlEventTypes.Json); }
        catch (System.Text.Json.JsonException)
        {
            // An unreadable payload leaves refund null and is parked as InvalidPayload just below.
        }
        if (refund == null || refund.Amount <= 0m || decimal.Round(refund.Amount, 2) != refund.Amount)
        {
            Park(record, GlParkReason.InvalidPayload, "The ProviderRefundOwed payload has no money amount.");
            return;
        }
        record.SourceDocumentId = refund.PaymentRunId;
        record.SourceReference = $"{refund.PaymentRunNumber}/{refund.VoidedCheckNumber}";
        record.ClaimedAmount = refund.Amount;
        Park(record, GlParkReason.ManualPostingRequired,
            $"refund of {refund.Amount:F2} owed to provider {refund.ProviderNpi}: withheld from payment {refund.RecoveringPaymentId} " +
            $"(run {refund.RecoveringRunNumber}) against receivable {refund.ReceivableId}, which reverses payment {refund.VoidedCheckNumber} that was never made. " +
            "Post the refund by hand (e.g. Dr ProviderReceivable / Cr ClaimsPayable), then dismiss this event naming the posting.");
    }

    private static GlEventEnvelope EnvelopeOf(GlSourceEvent record) => new()
    {
        EventId = record.EventId, Type = record.Type, TenantId = record.TenantId, Source = record.Source, PayloadJson = record.PayloadJson,
    };

    private static GlSourceEvent Copy(GlSourceEvent record)
        => System.Text.Json.JsonSerializer.Deserialize<GlSourceEvent>(System.Text.Json.JsonSerializer.Serialize(record))!;

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

    private void Park(GlSourceEvent record, GlParkReason reason, string detail, string? waitingFor = null)
    {
        record.Status = GlSourceEventStatus.Parked;
        record.ParkReason = reason;
        record.ParkDetail = FlattenLineBreaks(detail);
        record.WaitingForEventId = waitingFor;
        record.EntryId = null;
        _logger.LogWarning(ParkedEvent, "AUDIT GL event {EventId} ({Type}) of tenant {TenantId} parked ({Reason}): {Detail}",
            LogSanitizer.Sanitize(record.EventId), LogSanitizer.Sanitize(record.Type), LogSanitizer.Sanitize(record.TenantId), reason, LogSanitizer.Sanitize(detail));
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

    /// <summary>
    /// Keeps a stored free-text value on one line: CR removed, LF becomes a
    /// space. For log lines use <see cref="LogSanitizer.Sanitize(string?)"/>.
    /// </summary>
    private static string FlattenLineBreaks(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", " ");
}
