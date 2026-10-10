using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudHealthOffice.NachaTransmission;
using Microsoft.Extensions.Options;
using PaymentService.Models;
using PaymentService.Repositories;

namespace PaymentService.Services;

/// <summary>
/// Configuration section <c>BankTransmission</c>. Off by default: with
/// <see cref="Enabled"/> false nothing is ever sent, no transmission record is
/// written, the file is not regenerated and no bank or Key Vault is contacted.
/// The tenant's own switch (tenant-service
/// <c>paymentControls.nachaTransmission.enabled</c>) must also be on.
/// </summary>
public sealed class BankTransmissionOptions
{
    public const string SectionName = "BankTransmission";

    public bool Enabled { get; set; }

    /// <summary>
    /// How long an attempt may hold a record in Transmitting. An attempt older
    /// than this (the process died mid-send) is presumed of unknown outcome and
    /// parked as NeedsReview, never re-sent. Far above the SFTP timeouts (30 s per operation).
    /// </summary>
    public TimeSpan TransmittingLease { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The bank's time zone, for "today" in the effective-entry-date check
    /// (IANA id; default America/New_York, the Federal Reserve's settlement clock).
    /// </summary>
    public string BankTimeZone { get; set; } = "America/New_York";

    /// <summary>
    /// False (default): a file is sent only while its effective entry date is a
    /// banking day after today. True: today is accepted too (same-day ACH, which
    /// has its own bank cut-offs).
    /// </summary>
    public bool AllowSameDayEffectiveDate { get; set; }
}

/// <summary>
/// The approval no longer matches the money: the effective entry date passed (or is
/// not a banking day), or a payment in the file was reversed, reissued, voided or
/// changed since approval. Nothing was sent; a new file needs a new approval.
/// </summary>
public sealed class PaymentFileApprovalStaleException : Exception
{
    public PaymentFileApprovalStaleException(string message) : base(message) { }
}

/// <summary>Bank transmission is switched off (<c>BankTransmission:Enabled</c>). Nothing was done.</summary>
public sealed class BankTransmissionDisabledException : Exception
{
    public BankTransmissionDisabledException()
        : base("Bank transmission of NACHA files is disabled (BankTransmission:Enabled is false). Nothing was sent or recorded.") { }
}

/// <summary>The file's bytes no longer hash to the approved SHA-256. Nothing was sent.</summary>
public sealed class PaymentFileHashMismatchException : Exception
{
    public PaymentFileHashMismatchException(string message) : base(message) { }
}

/// <summary>The transmission is not in a state that allows the request (in progress, needs review, already final).</summary>
public sealed class PaymentFileTransmissionStateException : Exception
{
    public PaymentFileTransmissionStateException(string message) : base(message) { }
}

/// <summary>
/// Sends a completed ACH payment run's NACHA file to the tenant's bank,
/// exactly once. See <see cref="PaymentFileTransmissionStatus"/> for the state
/// machine. Dual control extends the payment-run maker-checker
/// (<see cref="IRunSeparationOfDuties.EnsureMayTransmit"/>): neither the run's
/// creator nor its executor may approve, retry or reconcile its transmission, a run
/// without a recorded creator is refused, and a service token never can. A
/// NeedsReview file is settled by a user who neither approved, attempted nor
/// reconciled it. Every attempt re-checks the approval against the money
/// (effective entry date, payments unreversed and not reissued).
/// </summary>
public interface IPaymentFileTransmissionService
{
    /// <summary>The run's transmission record, or null when its file was never approved for transmission.</summary>
    Task<PaymentFileTransmission?> GetAsync(string paymentRunId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves (first call) and transmits the run's pinned NACHA file, or retries
    /// a Failed one. Idempotent: a Transmitted file is returned as is, never re-sent.
    /// </summary>
    Task<PaymentFileTransmission> TransmitAsync(string paymentRunId, CancellationToken cancellationToken = default);

    /// <summary>
    /// For a NeedsReview file: lists the bank's drop (read-only). The file there
    /// with the expected size settles it as Transmitted; otherwise it stays NeedsReview.
    /// </summary>
    Task<PaymentFileTransmission> ReconcileAsync(string paymentRunId, CancellationToken cancellationToken = default);

    /// <summary>
    /// For a NeedsReview file: records what the bank said. Received: Transmitted.
    /// Not received: Failed, so it may be retried.
    /// </summary>
    Task<PaymentFileTransmission> ResolveAsync(string paymentRunId, bool bankReceived, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-dates a file whose effective entry date can no longer be sent: the old
    /// file's record (created if it was never approved) becomes Superseded, linked to
    /// the new file, and a new file is pinned that needs a fresh approval. Allowed only
    /// when the old record is absent, Pending or Failed (Failed includes a resolved
    /// "bank did not receive it"); never Transmitting, Transmitted or NeedsReview.
    /// Returns the new pinned file (facts only).
    /// </summary>
    Task<PaymentRunEftFile> RedateAsync(string paymentRunId, string reason, CancellationToken cancellationToken = default);
}

public sealed class PaymentFileTransmissionService : IPaymentFileTransmissionService
{
    public static readonly EventId ApprovedEvent = new(4931, "PaymentFileTransmissionApproved");
    public static readonly EventId TransmittedEvent = new(4932, "PaymentFileTransmitted");
    public static readonly EventId FailedEvent = new(4933, "PaymentFileTransmissionFailed");
    public static readonly EventId NeedsReviewEvent = new(4934, "PaymentFileTransmissionNeedsReview");
    public static readonly EventId RefusedEvent = new(4935, "PaymentFileTransmissionRefused");
    public static readonly EventId ReconciledEvent = new(4936, "PaymentFileTransmissionReconciled");
    public static readonly EventId ResolvedEvent = new(4937, "PaymentFileTransmissionResolved");
    public static readonly EventId UnrecordedEvent = new(4938, "PaymentFileTransmissionOutcomeNotRecorded");
    public static readonly EventId DisabledEvent = new(4939, "PaymentFileTransmissionDisabled");
    public static readonly EventId SupersededEvent = new(4940, "PaymentFileSuperseded");

    private const string SystemActor = "payment-service";

    private static readonly JsonSerializerOptions EventJson = new(JsonSerializerDefaults.Web);

    private readonly IPaymentRunRepository _runs;
    private readonly IPaymentRepository _payments;
    private readonly IClaimReservationRepository _reservations;
    private readonly IFfsEftFileService _eftFiles;
    private readonly IPaymentFileTransmissionRepository _store;
    private readonly INachaTransmitter _transmitter;
    private readonly INachaRemoteFileProbe _probe;
    private readonly IRunSeparationOfDuties _separation;
    private readonly BankTransmissionOptions _options;
    private readonly AchEffectiveDatePolicy _dates;
    private readonly TimeProvider _clock;
    private readonly ILogger<PaymentFileTransmissionService> _logger;

    public PaymentFileTransmissionService(
        IPaymentRunRepository runs,
        IPaymentRepository payments,
        IClaimReservationRepository reservations,
        IFfsEftFileService eftFiles,
        IPaymentFileTransmissionRepository store,
        INachaTransmitter transmitter,
        INachaRemoteFileProbe probe,
        IRunSeparationOfDuties separation,
        IOptions<BankTransmissionOptions> options,
        AchEffectiveDatePolicy effectiveDates,
        ILogger<PaymentFileTransmissionService> logger,
        TimeProvider? clock = null)
    {
        _dates = effectiveDates;
        _runs = runs;
        _payments = payments;
        _reservations = reservations;
        _eftFiles = eftFiles;
        _store = store;
        _transmitter = transmitter;
        _probe = probe;
        _separation = separation;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public async Task<PaymentFileTransmission?> GetAsync(string paymentRunId, CancellationToken cancellationToken = default)
    {
        var run = await LoadRunAsync(paymentRunId);
        return await _store.GetAsync(run.TenantId, FileReferenceOf(run), cancellationToken);
    }

    public async Task<PaymentFileTransmission> TransmitAsync(string paymentRunId, CancellationToken cancellationToken = default)
    {
        EnsureEnabled(paymentRunId);

        var run = await LoadRunAsync(paymentRunId);
        var user = _separation.EnsureMayTransmit(run.PaymentRunNumber, run.CreatedBy, run.ExecutedBy);
        var pinned = RequireTransmittableFile(run);
        // A re-dated file is approved (and retried) by someone other than its re-dater.
        if (pinned.Revision > 0 && string.Equals(pinned.FirstGeneratedBy, user, StringComparison.OrdinalIgnoreCase))
            throw new SeparationOfDutiesException(
                $"Separation of duties: you re-dated NACHA file {pinned.FileReference}, so you cannot approve or send it. " +
                "Another user with payments:approve must.");

        var record = await _store.GetAsync(run.TenantId, pinned.FileReference, cancellationToken);
        if (record != null)
        {
            if (!HashEquals(record.ApprovedSha256, pinned.Sha256))
            {
                await RefuseAsync(record, user, pinned.Sha256, "the run's pinned EFT file no longer has the approved SHA-256");
                throw new PaymentFileHashMismatchException(
                    $"The EFT file of payment run {run.PaymentRunNumber} is not the file approved for transmission " +
                    $"(approved sha256 {record.ApprovedSha256}, now {pinned.Sha256}). Nothing was sent.");
            }

            switch (record.Status)
            {
                case PaymentFileTransmissionStatus.Transmitted:
                    _logger.LogInformation(
                        "AUDIT NACHA file {FileReference} of payment run {RunNumber}: transmit requested by {User}, already transmitted at {At:o}; not sent again",
                        Clean(record.FileReference), Clean(run.PaymentRunNumber), Clean(user), record.TransmittedAt);
                    return record;
                case PaymentFileTransmissionStatus.NeedsReview:
                    throw new PaymentFileTransmissionStateException(NeedsReviewMessage(record));
                case PaymentFileTransmissionStatus.Superseded:
                    // Only reachable if a re-date was interrupted between superseding the
                    // record and pinning the new file: finish the re-date instead.
                    throw new PaymentFileTransmissionStateException(
                        $"NACHA file {record.FileReference} was superseded by a re-date and is never sent. Re-date it again " +
                        "(POST .../eft-file/redate) to pin its replacement.");
                case PaymentFileTransmissionStatus.Transmitting:
                    if (record.LeaseUntil > Now)
                        throw new PaymentFileTransmissionStateException(
                            $"NACHA file {record.FileReference} is being transmitted (attempt {record.AttemptCount}). Check its status.");
                    record = await ExpireLeaseAsync(record, user, cancellationToken);
                    throw new PaymentFileTransmissionStateException(NeedsReviewMessage(record));
            }
        }

        // The approval covers money, not just bytes: before every attempt (first or
        // retry) the effective entry date must still be ahead and the payments in the
        // file still those approved, unreversed and not reissued elsewhere.
        if (await StaleReasonAsync(run, pinned, record, cancellationToken) is { } stale)
        {
            if (record != null)
                await RefuseAsync(record, user, pinned.Sha256, stale);
            else
                _logger.LogWarning(RefusedEvent,
                    "AUDIT NACHA file {FileReference} of payment run {RunNumber}: approval by {User} refused ({Why})",
                    Clean(pinned.FileReference), Clean(run.PaymentRunNumber), Clean(user), Clean(stale));
            throw new PaymentFileApprovalStaleException(
                $"NACHA file {pinned.FileReference} of payment run {run.PaymentRunNumber} cannot be sent: {stale}. Nothing was sent. " +
                "Sending it now would not pay what was approved; a new file needs a new approval.");
        }

        // Regenerate the approved bytes. The EFT-file service itself refuses when
        // they no longer reproduce the run's pinned SHA-256.
        FfsEftFileOutcome outcome;
        try
        {
            outcome = await _eftFiles.GenerateAsync(run.Id, user, cancellationToken);
        }
        catch (RunConflictException ex)
        {
            if (record != null)
                await RefuseAsync(record, user, string.Empty, "the regenerated EFT file differs from the approved file");
            _logger.LogWarning(RefusedEvent,
                "AUDIT NACHA file {FileReference} of payment run {RunNumber}: transmit by {User} refused, the regenerated file differs from the approved sha256 {Sha256}",
                Clean(pinned.FileReference), Clean(run.PaymentRunNumber), Clean(user), pinned.Sha256);
            throw new PaymentFileHashMismatchException(ex.Message);
        }

        var file = outcome.File
            ?? throw new InvalidOperationException($"Payment run {run.PaymentRunNumber} has no EFT credits to transmit.");
        var facts = NachaFileFacts.From(file.Content);
        var approved = record?.ApprovedSha256 ?? pinned.Sha256;
        if (!HashEquals(facts.Sha256, approved) || !HashEquals(file.Sha256, approved) || !HashEquals(outcome.Run.EftFile?.Sha256, approved))
        {
            if (record != null)
                await RefuseAsync(record, user, facts.Sha256, "the bytes to send do not hash to the approved SHA-256");
            _logger.LogWarning(RefusedEvent,
                "AUDIT NACHA file {FileReference} of payment run {RunNumber}: transmit by {User} refused, bytes hash {Actual} but {Approved} was approved",
                Clean(pinned.FileReference), Clean(run.PaymentRunNumber), Clean(user), facts.Sha256, approved);
            throw new PaymentFileHashMismatchException(
                $"The NACHA file of payment run {run.PaymentRunNumber} hashes to {facts.Sha256}, not the approved {approved}. Nothing was sent.");
        }

        if (record == null)
        {
            record = new PaymentFileTransmission
            {
                TenantId = run.TenantId,
                PaymentRunId = run.Id,
                PaymentRunNumber = run.PaymentRunNumber,
                FileReference = pinned.FileReference,
                FileName = pinned.FileName,
                ApprovedSha256 = approved,
                ByteSize = facts.ByteSize,
                EntryCount = facts.EntryCount,
                TotalCreditAmount = facts.TotalCreditAmount,
                TotalDebitAmount = facts.TotalDebitAmount,
                EffectiveEntryDate = pinned.EffectiveEntryDate,
                RunCreatedBy = run.CreatedBy,
                RunExecutedBy = run.ExecutedBy,
                ApprovedPaymentIds = PaymentIdsOf(pinned),
                RemittanceDateNotices = pinned.RemittanceDateNotices,
                ApprovedBy = user,
                ApprovedAt = Now,
                Status = PaymentFileTransmissionStatus.Pending,
                CreatedAt = Now,
                UpdatedAt = Now,
            };
            if (!await _store.TryInsertAsync(record, cancellationToken))
                throw new PaymentFileTransmissionStateException(
                    $"Another approver just approved NACHA file {pinned.FileReference} for transmission. Check its status.");
            _logger.LogInformation(ApprovedEvent,
                "AUDIT NACHA file {FileReference} of payment run {RunNumber} (created by {Maker}) approved for bank transmission by {User}: " +
                "{Entries} credits, {Total:F2}, sha256 {Sha256}",
                Clean(record.FileReference), Clean(run.PaymentRunNumber), Clean(run.CreatedBy), Clean(user),
                record.EntryCount, record.TotalCreditAmount, record.ApprovedSha256);
        }

        // Claim: Pending/Failed -> Transmitting, conditional on the version read.
        // Two concurrent requests both get here; only one claim succeeds.
        var readVersion = record.Version;
        var started = Now;
        record.Status = PaymentFileTransmissionStatus.Transmitting;
        record.AttemptCount++;
        record.LeaseUntil = started.Add(_options.TransmittingLease);
        record.Reason = null;
        record.UpdatedAt = started;
        Append(record, PaymentFileTransmissionAction.Transmit, user, facts.Sha256, PaymentFileTransmissionStatus.Transmitting,
            $"attempt {record.AttemptCount} started");
        // Not cancellable: a claim written but not acknowledged would park the file
        // as Transmitting (then NeedsReview) although nothing was sent.
        if (!await _store.TryReplaceAsync(record, readVersion, CancellationToken.None))
            throw new PaymentFileTransmissionStateException(
                $"NACHA file {record.FileReference} changed while this request prepared it (another attempt is under way). Check its status.");
        var heldVersion = record.Version;

        // Send. The caller's cancellation is not passed on: an attempt abandoned
        // half-way would be an unknown outcome.
        PaymentFileTransmissionStatus result;
        string detail;
        NachaTransmissionReceipt? receipt = null;
        try
        {
            receipt = await _transmitter.TransmitAsync(new NachaTransmissionRequest
            {
                TenantId = record.TenantId,
                FileReference = record.FileReference,
                FileName = record.FileName,
                Content = file.Content,
                RunId = record.PaymentRunId,
                BatchId = record.FileReference,
                TransmittedBy = user,
            }, CancellationToken.None);

            if (HashEquals(receipt.Sha256, approved))
            {
                result = PaymentFileTransmissionStatus.Transmitted;
                detail = $"delivered to {receipt.Destination} as {receipt.RemoteFileName}";
            }
            else
            {
                result = PaymentFileTransmissionStatus.NeedsReview;
                detail = $"the transmitter reported sending bytes with sha256 {receipt.Sha256}, not the approved file; verify with the bank";
            }
        }
        catch (NachaTransmissionException ex) when (ex.DeliveryUnknown)
        {
            result = PaymentFileTransmissionStatus.NeedsReview;
            detail = ex.Message;
        }
        catch (NachaTransmissionException ex)
        {
            // Definitely not in the bank's drop: not configured, host key refused,
            // or the upload failed before the rename into place.
            result = PaymentFileTransmissionStatus.Failed;
            detail = ex.HostKeyRejected ? "host key refused: " + ex.Message : ex.Message;
        }
        catch (Exception ex)
        {
            result = PaymentFileTransmissionStatus.NeedsReview;
            detail = $"the attempt ended with an unexpected {ex.GetType().Name}; whether the file reached the bank is unknown";
        }

        var finished = Now;
        record.Status = result;
        record.LeaseUntil = null;
        record.UpdatedAt = finished;
        record.Reason = result == PaymentFileTransmissionStatus.Transmitted ? null : Clean(detail);
        if (result == PaymentFileTransmissionStatus.Transmitted)
            MarkTransmitted(record, receipt!.RemoteFileName, receipt.Destination, receipt.TransmittedAt, user, PaymentFileDeliveryEvidence.Upload);
        Append(record, PaymentFileTransmissionAction.Transmit, user, facts.Sha256, result, detail);

        if (!await SaveOutcomeAsync(record, heldVersion, result))
            return await RecordLateOutcomeAsync(record, user, facts.Sha256, result, detail);
        LogOutcome(record, user, result, detail);
        return record;
    }

    public async Task<PaymentFileTransmission> ReconcileAsync(string paymentRunId, CancellationToken cancellationToken = default)
    {
        EnsureEnabled(paymentRunId);

        var run = await LoadRunAsync(paymentRunId);
        var user = _separation.EnsureMayTransmit(run.PaymentRunNumber, run.CreatedBy, run.ExecutedBy);
        var record = await RequireNeedsReviewAsync(run, user, cancellationToken);

        PaymentFileTransmissionStatus result = PaymentFileTransmissionStatus.NeedsReview;
        string detail;
        NachaRemoteFileCheck? check = null;
        try
        {
            check = await _probe.CheckAsync(record.TenantId, record.FileName, record.ByteSize, cancellationToken);
            switch (check.Presence)
            {
                case NachaRemoteFilePresence.Present:
                    result = PaymentFileTransmissionStatus.Transmitted;
                    detail = $"{record.FileName} ({record.ByteSize} bytes) found in {check.Destination}";
                    break;
                case NachaRemoteFilePresence.DifferentSize:
                    detail = $"{record.FileName} is in {check.Destination} but is {check.RemoteByteSize?.ToString() ?? "of unknown size"} bytes, " +
                             $"not {record.ByteSize}; confirm with the bank";
                    break;
                default:
                    detail = $"{record.FileName} is not in {check.Destination}: it may never have arrived, or the bank may already have " +
                             "collected it. Confirm with the bank and record its answer";
                    break;
            }
        }
        catch (NachaTransmissionException ex)
        {
            detail = "the bank's drop could not be checked: " + ex.Message;
        }

        var readVersion = record.Version;
        record.UpdatedAt = Now;
        if (result == PaymentFileTransmissionStatus.Transmitted)
        {
            record.Status = PaymentFileTransmissionStatus.Transmitted;
            record.Reason = null;
            MarkTransmitted(record, record.FileName, check!.Destination, Now, LastTransmitter(record) ?? user, PaymentFileDeliveryEvidence.RemoteListing);
        }
        Append(record, PaymentFileTransmissionAction.Reconcile, user, record.ApprovedSha256, record.Status, detail);
        if (!await _store.TryReplaceAsync(record, readVersion, cancellationToken))
            throw new PaymentFileTransmissionStateException($"NACHA file {record.FileReference} changed meanwhile. Check its status.");

        _logger.Log(result == PaymentFileTransmissionStatus.Transmitted ? LogLevel.Information : LogLevel.Warning, ReconciledEvent,
            "AUDIT NACHA file {FileReference} of payment run {RunNumber} reconciled by {User}: {Status} ({Detail}); sha256 {Sha256}",
            Clean(record.FileReference), Clean(record.PaymentRunNumber), Clean(user), record.Status, Clean(detail), record.ApprovedSha256);
        return record;
    }

    public async Task<PaymentFileTransmission> ResolveAsync(string paymentRunId, bool bankReceived, string reason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A reason (what the bank said, who, their reference) is required.");
        reason = Clean(reason.Trim());
        if (reason.Length > 500) reason = reason[..500];

        // Deliberately NOT gated by BankTransmission:Enabled: after an emergency stop
        // operators must still be able to record what the bank said about a file
        // whose outcome was unknown. Resolving sends nothing and contacts nothing.
        var run = await LoadRunAsync(paymentRunId);
        var user = _separation.EnsureMayTransmit(run.PaymentRunNumber, run.CreatedBy, run.ExecutedBy);
        var record = await RequireNeedsReviewAsync(run, user, cancellationToken);

        // The bank's answer is recorded by someone who did not approve, attempt or
        // reconcile this file.
        var involved = record.Attempts
            .Where(a => a.Action is PaymentFileTransmissionAction.Transmit or PaymentFileTransmissionAction.Reconcile)
            .Select(a => a.By)
            .Append(record.ApprovedBy);
        if (involved.Any(u => string.Equals(u, user, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning(RefusedEvent,
                "AUDIT NACHA file {FileReference} of payment run {RunNumber}: {User} may not record the bank's answer (approved, attempted or reconciled it)",
                Clean(record.FileReference), Clean(record.PaymentRunNumber), Clean(user));
            throw new SeparationOfDutiesException(
                "Separation of duties: you approved, attempted or reconciled this NACHA file's transmission, so you cannot record whether " +
                "the bank received it. Another user with payments:approve must.");
        }

        var readVersion = record.Version;
        record.UpdatedAt = Now;
        if (bankReceived)
        {
            record.Status = PaymentFileTransmissionStatus.Transmitted;
            record.Reason = null;
            MarkTransmitted(record, record.FileName, record.Destination, Now, LastTransmitter(record) ?? user, PaymentFileDeliveryEvidence.BankConfirmation);
        }
        else
        {
            record.Status = PaymentFileTransmissionStatus.Failed;
            record.Reason = "the bank confirmed it did not receive the file: " + reason;
        }
        Append(record, PaymentFileTransmissionAction.Resolve, user, record.ApprovedSha256, record.Status,
            (bankReceived ? "bank received: " : "bank did not receive: ") + reason);
        if (!await _store.TryReplaceAsync(record, readVersion, cancellationToken))
            throw new PaymentFileTransmissionStateException($"NACHA file {record.FileReference} changed meanwhile. Check its status.");

        _logger.LogWarning(ResolvedEvent,
            "AUDIT NACHA file {FileReference} of payment run {RunNumber}: {User} recorded that the bank {Answer} it ({Reason}); now {Status}; sha256 {Sha256}",
            Clean(record.FileReference), Clean(record.PaymentRunNumber), Clean(user), bankReceived ? "received" : "did not receive",
            reason, record.Status, record.ApprovedSha256);
        return record;
    }

    public async Task<PaymentRunEftFile> RedateAsync(string paymentRunId, string reason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A reason is required to re-date a NACHA file.");
        reason = Clean(reason.Trim());
        if (reason.Length > 500) reason = reason[..500];

        // Not gated by BankTransmission:Enabled: re-dating builds a file and sends nothing.
        var run = await LoadRunAsync(paymentRunId);
        var user = _separation.EnsureMayTransmit(run.PaymentRunNumber, run.CreatedBy, run.ExecutedBy);
        var pinned = RequireTransmittableFile(run);
        var nextReference = $"FFS-{run.PaymentRunNumber}-R{pinned.Revision + 1}";

        var record = await _store.GetAsync(run.TenantId, pinned.FileReference, cancellationToken);
        var resuming = record?.Status == PaymentFileTransmissionStatus.Superseded
                       && record.SupersededByFileReference == nextReference;
        if (!resuming && _dates.SendProblem(pinned.EffectiveEntryDate) == null)
            throw new PaymentFileTransmissionStateException(
                $"NACHA file {pinned.FileReference} (effective {pinned.EffectiveEntryDate:yyyy-MM-dd}) can still be sent; it is not re-dated.");

        // 1. Make the old file unsendable first: its record becomes Superseded (or a
        //    Superseded record is created, so nobody can approve it meanwhile).
        if (record == null)
        {
            var placeholder = new PaymentFileTransmission
            {
                TenantId = run.TenantId,
                PaymentRunId = run.Id,
                PaymentRunNumber = run.PaymentRunNumber,
                FileReference = pinned.FileReference,
                FileName = pinned.FileName,
                ApprovedSha256 = pinned.Sha256,
                ByteSize = pinned.ByteSize,
                EntryCount = pinned.EntryCount,
                TotalCreditAmount = pinned.TotalCreditAmount,
                EffectiveEntryDate = pinned.EffectiveEntryDate,
                RunCreatedBy = run.CreatedBy,
                RunExecutedBy = run.ExecutedBy,
                ApprovedPaymentIds = PaymentIdsOf(pinned),
                Status = PaymentFileTransmissionStatus.Superseded,
                SupersededByFileReference = nextReference,
                SupersededAt = Now,
                CreatedAt = Now,
                UpdatedAt = Now,
            };
            Append(placeholder, PaymentFileTransmissionAction.Superseded, user, pinned.Sha256, PaymentFileTransmissionStatus.Superseded,
                $"never approved; superseded by {nextReference}: {reason}");
            if (!await _store.TryInsertAsync(placeholder, cancellationToken))
                throw new PaymentFileTransmissionStateException(
                    $"NACHA file {pinned.FileReference} was just approved by someone else. Check its status before re-dating.");
        }
        else if (!resuming)
        {
            if (record.Status is not (PaymentFileTransmissionStatus.Pending or PaymentFileTransmissionStatus.Failed))
            {
                await RefuseAsync(record, user, pinned.Sha256, $"re-date refused: the file is {record.Status}");
                throw new PaymentFileTransmissionStateException(
                    $"NACHA file {record.FileReference} is {record.Status}; it cannot be re-dated" +
                    (record.Status == PaymentFileTransmissionStatus.NeedsReview
                        ? " until the bank's answer is recorded (it may already be at the bank)."
                        : record.Status == PaymentFileTransmissionStatus.Transmitted ? " (it is at the bank)." : "."));
            }
            // A Failed file had an upload attempt (or the bank said it did not get it):
            // before it is superseded, look in the drop (read-only) for its name. If it is
            // there, re-dating would pay twice.
            if (record.Status == PaymentFileTransmissionStatus.Failed)
            {
                string? present = null;
                string probeDetail;
                try
                {
                    var check = await _probe.CheckAsync(record.TenantId, record.FileName, record.ByteSize, cancellationToken);
                    probeDetail = $"re-date probe: {record.FileName} {check.Presence} in {check.Destination}" +
                                  (check.RemoteByteSize is { } size ? $" ({size} bytes)" : string.Empty);
                    if (check.Presence != NachaRemoteFilePresence.Absent)
                        present = probeDetail;
                }
                catch (NachaTransmissionException ex)
                {
                    probeDetail = "re-date probe: the bank's drop could not be checked: " + ex.Message;
                    present = probeDetail;
                }

                var probeVersion = record.Version;
                Append(record, PaymentFileTransmissionAction.Reconcile, user, record.ApprovedSha256, record.Status, probeDetail);
                record.UpdatedAt = Now;
                if (!await _store.TryReplaceAsync(record, probeVersion, CancellationToken.None))
                    throw new PaymentFileTransmissionStateException($"NACHA file {record.FileReference} changed meanwhile. Check its status.");
                if (present != null)
                {
                    _logger.LogWarning(RefusedEvent,
                        "AUDIT NACHA file {FileReference} of payment run {RunNumber}: re-date by {User} refused ({Detail})",
                        Clean(record.FileReference), Clean(run.PaymentRunNumber), Clean(user), Clean(present));
                    throw new PaymentFileTransmissionStateException(
                        $"NACHA file {record.FileReference} cannot be re-dated: {present}. It may be at the bank; confirm with the bank " +
                        "before anything else (re-dating would send a second file).");
                }
            }

            var readVersion = record.Version;
            var previous = record.Status;
            record.Status = PaymentFileTransmissionStatus.Superseded;
            record.SupersededByFileReference = nextReference;
            record.SupersededAt = Now;
            record.UpdatedAt = Now;
            Append(record, PaymentFileTransmissionAction.Superseded, user, record.ApprovedSha256, PaymentFileTransmissionStatus.Superseded,
                $"was {previous}; superseded by {nextReference}: {reason}");
            if (!await _store.TryReplaceAsync(record, readVersion, CancellationToken.None))
                throw new PaymentFileTransmissionStateException($"NACHA file {record.FileReference} changed meanwhile. Check its status.");
        }

        // 2. Pin the new file. If this fails the old record stays Superseded (never
        //    sendable) and the re-date can simply be repeated.
        FfsEftFileOutcome outcome;
        try
        {
            outcome = await _eftFiles.RepinAsync(run.Id, user, reason, pinned.FileReference, pinned.Sha256, cancellationToken);
        }
        catch (RunConflictException ex)
        {
            throw new PaymentFileTransmissionStateException(ex.Message);
        }
        var file = outcome.Run.EftFile!;
        _logger.LogWarning(SupersededEvent,
            "AUDIT NACHA file {Old} of payment run {RunNumber} superseded by {User}: re-dated as {New} effective {Effective:yyyy-MM-dd}, " +
            "sha256 {Sha256}; it needs a fresh approval ({Reason})",
            Clean(pinned.FileReference), Clean(run.PaymentRunNumber), Clean(user), Clean(file.FileReference), file.EffectiveEntryDate,
            file.Sha256, reason);
        return file;
    }

    private void EnsureEnabled(string paymentRunId)
    {
        if (_options.Enabled)
            return;
        _logger.LogInformation(DisabledEvent,
            "NACHA bank transmission requested for payment run {RunId} but BankTransmission:Enabled is false; nothing done", Clean(paymentRunId));
        throw new BankTransmissionDisabledException();
    }

    private async Task<PaymentRun> LoadRunAsync(string paymentRunId)
        => await _runs.GetByIdAsync(paymentRunId) ?? throw new KeyNotFoundException($"Payment run {paymentRunId} not found");

    private static string FileReferenceOf(PaymentRun run) => run.EftFile?.FileReference ?? $"FFS-{run.PaymentRunNumber}";

    private static PaymentRunEftFile RequireTransmittableFile(PaymentRun run)
    {
        if (run.Status != PaymentRunStatus.Completed)
            throw new InvalidOperationException($"Payment run {run.PaymentRunNumber} is {run.Status}; only a completed run's file goes to the bank.");
        if (!string.Equals(run.PaymentMethod, "ACH", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Payment run {run.PaymentRunNumber} pays by {run.PaymentMethod}; it has no NACHA file.");
        var file = run.EftFile
            ?? throw new InvalidOperationException(
                $"Payment run {run.PaymentRunNumber} has no EFT file yet. Generate it (POST /api/paymentruns/{{id}}/eft-file) and check it before approving transmission.");
        if (file.EntryCount == 0 || string.IsNullOrEmpty(file.Sha256))
            throw new InvalidOperationException($"Payment run {run.PaymentRunNumber}'s EFT file has no credits; there is nothing to transmit.");
        return file;
    }

    private static List<string> PaymentIdsOf(PaymentRunEftFile file)
        => file.Entries.SelectMany(e => e.PaymentIds).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Null while the approval still describes the money; otherwise why not. Checks
    /// the effective entry date (only that it is not past, or today without same-day:
    /// a future weekend or holiday is the bank's to roll, and pinned dates are already
    /// rolled), and that every payment in the file is the one
    /// approved: present, issued by this run, ACH, not a reversal, not in Exception,
    /// its amounts still summing to the file's credits, none of its claims reserved
    /// for reversal, and none of its claims' payment reservation held by another run.
    /// </summary>
    private async Task<string?> StaleReasonAsync(PaymentRun run, PaymentRunEftFile file, PaymentFileTransmission? record, CancellationToken cancellationToken)
    {
        if (_dates.SendProblem(file.EffectiveEntryDate) is { } dateProblem)
            return dateProblem + "; re-date the file (POST .../eft-file/redate) and approve the new one";

        var ids = PaymentIdsOf(file);
        if (record != null && !record.ApprovedPaymentIds.SequenceEqual(ids, StringComparer.Ordinal))
            return "the file no longer pays the payments that were approved";

        var amounts = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payment = await _payments.GetByIdAsync(id);
            if (payment == null)
                return $"payment {id} no longer exists";
            if (payment.IsReversal || !string.Equals(payment.RunId, run.Id, StringComparison.Ordinal))
                return $"payment {payment.CheckNumber} is no longer this run's payment";
            if (!string.Equals(payment.PaymentMethod, "ACH", StringComparison.OrdinalIgnoreCase))
                return $"payment {payment.CheckNumber} is now paid by {payment.PaymentMethod}";
            if (payment.Status == PaymentStatus.Exception)
                return $"payment {payment.CheckNumber} is in Exception";
            amounts[id] = payment.TotalPaymentAmount;

            foreach (var claimId in payment.ClaimPayments.Select(c => c.ClaimId).Distinct(StringComparer.Ordinal))
            {
                var reversal = await _reservations.GetAsync(ClaimReservationKind.Reversal, run.TenantId, claimId);
                if (reversal != null)
                    return $"a claim of payment {payment.CheckNumber} was reversed (or is being reversed) by reversal run {reversal.RunNumber ?? reversal.RunId}";
                var paid = await _reservations.GetAsync(ClaimReservationKind.Payment, run.TenantId, claimId);
                if (paid != null && !string.Equals(paid.RunId, run.Id, StringComparison.Ordinal))
                    return $"a claim of payment {payment.CheckNumber} is now paid by run {paid.RunNumber ?? paid.RunId} (reissued)";
            }
        }

        foreach (var entry in file.Entries)
        {
            if (entry.PaymentIds.Sum(id => amounts.TryGetValue(id, out var a) ? a : 0m) != entry.Amount)
                return $"the payments of credit {entry.AchTraceNumber} no longer add up to its amount";
        }

        return null;
    }

    private async Task<PaymentFileTransmission> RequireNeedsReviewAsync(PaymentRun run, string user, CancellationToken cancellationToken)
    {
        var record = await _store.GetAsync(run.TenantId, FileReferenceOf(run), cancellationToken)
            ?? throw new KeyNotFoundException($"Payment run {run.PaymentRunNumber}'s NACHA file was never approved for transmission.");
        if (record.Status == PaymentFileTransmissionStatus.Transmitting && !(record.LeaseUntil > Now))
            record = await ExpireLeaseAsync(record, user, cancellationToken);
        if (record.Status != PaymentFileTransmissionStatus.NeedsReview)
            throw new PaymentFileTransmissionStateException(
                $"NACHA file {record.FileReference} is {record.Status}, not awaiting review.");
        return record;
    }

    /// <summary>A Transmitting record past its lease: the attempt's outcome is unknown, so NeedsReview (never re-sent).</summary>
    private async Task<PaymentFileTransmission> ExpireLeaseAsync(PaymentFileTransmission record, string observedBy, CancellationToken cancellationToken)
    {
        var readVersion = record.Version;
        record.Status = PaymentFileTransmissionStatus.NeedsReview;
        record.Reason = $"attempt {record.AttemptCount} did not finish within its lease (until {record.LeaseUntil:o}); " +
                        "whether the file reached the bank is unknown";
        record.LeaseUntil = null;
        record.UpdatedAt = Now;
        Append(record, PaymentFileTransmissionAction.LeaseExpired, observedBy, record.ApprovedSha256, record.Status, record.Reason);
        if (!await _store.TryReplaceAsync(record, readVersion, cancellationToken))
            throw new PaymentFileTransmissionStateException($"NACHA file {record.FileReference} changed meanwhile. Check its status.");
        _logger.LogError(NeedsReviewEvent,
            "AUDIT NACHA file {FileReference} of payment run {RunNumber}: attempt {Attempt} outlived its lease; NeedsReview (noticed by {User}); sha256 {Sha256}",
            Clean(record.FileReference), Clean(record.PaymentRunNumber), record.AttemptCount, Clean(observedBy), record.ApprovedSha256);
        return record;
    }

    /// <summary>Records a refusal on an existing record (best effort: the refusal itself stands either way).</summary>
    private async Task RefuseAsync(PaymentFileTransmission record, string user, string sha256, string why)
    {
        var readVersion = record.Version;
        Append(record, PaymentFileTransmissionAction.Refused, user, sha256, record.Status, why);
        record.UpdatedAt = Now;
        try
        {
            await _store.TryReplaceAsync(record, readVersion, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("The refusal of NACHA file {FileReference} could not be added to its record: {Error}",
                Clean(record.FileReference), ex.GetType().Name);
        }
        _logger.LogWarning(RefusedEvent,
            "AUDIT NACHA file {FileReference} of payment run {RunNumber}: transmit by {User} refused ({Why}); approved sha256 {Approved}, seen {Seen}",
            Clean(record.FileReference), Clean(record.PaymentRunNumber), Clean(user), why, record.ApprovedSha256, sha256);
    }

    /// <summary>True when the outcome was recorded; false when the record changed meanwhile (a late outcome).</summary>
    private async Task<bool> SaveOutcomeAsync(PaymentFileTransmission record, string heldVersion, PaymentFileTransmissionStatus result)
    {
        bool saved;
        try
        {
            saved = await _store.TryReplaceAsync(record, heldVersion, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(UnrecordedEvent,
                "AUDIT NACHA file {FileReference} of payment run {RunNumber}: attempt {Attempt} ended {Result} but could not be recorded ({Error}). " +
                "The record stays Transmitting and becomes NeedsReview when its lease expires; it will not be re-sent",
                Clean(record.FileReference), Clean(record.PaymentRunNumber), record.AttemptCount, result, ex.GetType().Name);
            throw;
        }

        return saved;
    }

    /// <summary>
    /// The attempt finished after its record moved on (its lease expired, and the
    /// record may since have been reconciled, resolved or claimed again). A delivered
    /// or unknown outcome is evidence the file may be at the bank: unless the record
    /// is already Transmitted, it is forced to NeedsReview (even over a resolver's
    /// Failed), with an audit entry. A definite failure is only logged.
    /// </summary>
    private async Task<PaymentFileTransmission> RecordLateOutcomeAsync(
        PaymentFileTransmission attempted, string user, string sha256, PaymentFileTransmissionStatus result, string detail)
    {
        var attempt = attempted.AttemptCount;
        _logger.LogCritical(UnrecordedEvent,
            "AUDIT NACHA file {FileReference} of payment run {RunNumber}: attempt {Attempt} by {User} ended {Result} after its record changed " +
            "(its lease was expired); recording it as late delivery evidence",
            Clean(attempted.FileReference), Clean(attempted.PaymentRunNumber), attempt, Clean(user), result);

        for (var tries = 0; tries < 10; tries++)
        {
            var current = await _store.GetAsync(attempted.TenantId, attempted.FileReference, CancellationToken.None)
                ?? throw new PaymentFileTransmissionStateException($"NACHA file {attempted.FileReference}'s record disappeared.");
            var readVersion = current.Version;
            var previous = current.Status;
            // A definite failure is no evidence of delivery: log only, and do not
            // touch the record (a write would disturb an attempt holding it now).
            if (result == PaymentFileTransmissionStatus.Failed)
                return current;
            if (current.Status != PaymentFileTransmissionStatus.Transmitted)
            {
                current.Status = PaymentFileTransmissionStatus.NeedsReview;
                current.LeaseUntil = null;
                current.Reason = $"late delivery evidence: attempt {attempt} ended {result} ({Clean(detail)}) after the record had moved on " +
                                 $"(it was {previous}); the file may be at the bank";
            }
            current.UpdatedAt = Now;
            Append(current, PaymentFileTransmissionAction.LateOutcome, user, sha256, current.Status,
                $"late delivery evidence: attempt {attempt} ended {result} ({detail}); record was {previous}");
            if (await _store.TryReplaceAsync(current, readVersion, CancellationToken.None))
            {
                if (current.Status == PaymentFileTransmissionStatus.NeedsReview)
                    _logger.LogError(NeedsReviewEvent,
                        "AUDIT NACHA file {FileReference} of payment run {RunNumber}: forced to NeedsReview by the late outcome of attempt {Attempt} (was {Previous})",
                        Clean(current.FileReference), Clean(current.PaymentRunNumber), attempt, previous);
                return current;
            }
        }
        throw new PaymentFileTransmissionStateException(
            $"NACHA file {attempted.FileReference}: attempt {attempt} ended {result} but its record kept changing; reconcile it before anything else.");
    }

    private void LogOutcome(PaymentFileTransmission record, string user, PaymentFileTransmissionStatus result, string detail)
    {
        var (level, eventId) = result switch
        {
            PaymentFileTransmissionStatus.Transmitted => (LogLevel.Information, TransmittedEvent),
            PaymentFileTransmissionStatus.Failed => (LogLevel.Warning, FailedEvent),
            _ => (LogLevel.Error, NeedsReviewEvent),
        };
        _logger.Log(level, eventId,
            "AUDIT NACHA file {FileReference} of payment run {RunNumber}, attempt {Attempt} by {User}: {Result} ({Detail}); " +
            "{Entries} credits, {Total:F2}, sha256 {Sha256}",
            Clean(record.FileReference), Clean(record.PaymentRunNumber), record.AttemptCount, Clean(user), result, Clean(detail),
            record.EntryCount, record.TotalCreditAmount, record.ApprovedSha256);
    }

    /// <summary>Final: sets the receipt fields and writes PaymentFileTransmitted to the outbox (once).</summary>
    private static void MarkTransmitted(PaymentFileTransmission record, string? remoteFileName, string? destination, DateTime at, string by,
        PaymentFileDeliveryEvidence evidence)
    {
        record.RemoteFileName = remoteFileName;
        record.Destination = destination;
        record.TransmittedAt = at;
        record.TransmittedBy = by;
        record.ConfirmedBy = evidence;
        record.Acknowledgement = PaymentFileAcknowledgementStatus.Awaiting;

        if (record.Outbox.Any(m => m.Type == PaymentFileOutboxMessage.TransmittedType))
            return;
        var eventId = EventIdFor(record, PaymentFileOutboxMessage.TransmittedType);
        var payload = new PaymentFileTransmittedEvent
        {
            EventId = eventId,
            TenantId = record.TenantId,
            PaymentRunId = record.PaymentRunId,
            PaymentRunNumber = record.PaymentRunNumber,
            FileReference = record.FileReference,
            Sha256 = record.ApprovedSha256,
            EntryCount = record.EntryCount,
            TotalCreditAmount = record.TotalCreditAmount,
            TotalDebitAmount = record.TotalDebitAmount,
            EffectiveEntryDate = record.EffectiveEntryDate,
            TransmittedAt = at,
            ApprovedBy = record.ApprovedBy,
            ConfirmedBy = evidence.ToString(),
        };
        record.Outbox.Add(new PaymentFileOutboxMessage
        {
            EventId = eventId,
            Type = PaymentFileOutboxMessage.TransmittedType,
            PayloadJson = JsonSerializer.Serialize(payload, EventJson),
            CreatedAt = at,
        });
    }

    /// <summary>A stable id per (tenant, file, hash, type), so a replayed event de-duplicates downstream.</summary>
    internal static string EventIdFor(PaymentFileTransmission record, string type)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{record.TenantId}\n{record.FileReference}\n{record.ApprovedSha256}\n{type}"));
        return new Guid(hash.AsSpan(0, 16)).ToString();
    }

    private static string? LastTransmitter(PaymentFileTransmission record)
        => record.Attempts.LastOrDefault(a => a.Action == PaymentFileTransmissionAction.Transmit)?.By;

    private void Append(PaymentFileTransmission record, PaymentFileTransmissionAction action, string by, string sha256,
        PaymentFileTransmissionStatus result, string? detail)
        => record.Attempts.Add(new PaymentFileTransmissionAttempt
        {
            Sequence = record.Attempts.Count + 1,
            Action = action,
            By = string.IsNullOrEmpty(by) ? SystemActor : by,
            At = Now,
            Sha256 = sha256,
            Result = result,
            Detail = detail == null ? null : Clean(detail),
        });

    private static string NeedsReviewMessage(PaymentFileTransmission record)
        => $"NACHA file {record.FileReference} may already be at the bank ({record.Reason}). It will not be sent again: reconcile it " +
           "(POST .../eft-file/transmission/reconcile) or have a second user record what the bank says (POST .../eft-file/transmission/resolve).";

    private static bool HashEquals(string? a, string? b)
        => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Clean(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", " ");
}

/// <summary>
/// Registered while <c>BankTransmission:Enabled</c> is false: the service
/// refuses before reaching it, and this refuses again, connecting nowhere.
/// </summary>
public sealed class DisabledNachaTransmitter : INachaTransmitter, INachaRemoteFileProbe
{
    private static NachaTransmissionException Disabled()
        => new("Bank transmission is disabled (BankTransmission:Enabled is false).", notConfigured: true);

    public Task<NachaTransmissionReceipt> TransmitAsync(NachaTransmissionRequest request, CancellationToken cancellationToken = default)
        => throw Disabled();

    public Task<NachaRemoteFileCheck> CheckAsync(string tenantId, string fileName, long expectedByteSize, CancellationToken cancellationToken = default)
        => throw Disabled();
}
