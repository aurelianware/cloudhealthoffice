using CloudHealthOffice.Infrastructure.Edi.Files;
using CloudHealthOffice.Infrastructure.Edi.Interchange;
using CloudHealthOffice.Infrastructure.Logging;
using EnrollmentImportService.Models;
using Microsoft.Extensions.Options;

namespace EnrollmentImportService.Services.Runs;

/// <summary>What the intake made of a raw 834.</summary>
public sealed class Raw834IntakeOutcome
{
    public InterchangeIntakeResult? Envelope { get; init; }
    public EdiFile? EdiFile { get; init; }

    /// <summary>The import run, unless the envelope was rejected.</summary>
    public EnrollmentImportRun? Run { get; init; }

    public bool IsRejected => Run is null;
}

/// <summary>
/// The one intake path for raw 834s of every size and source
/// (edi-operations.md §11): the file is streamed to the raw-file store, its
/// envelope checked from a streamed skeleton (duplicate ISA13, TA1), the EDI
/// file record written, and an import run queued. Nothing holds the file in
/// memory.
/// </summary>
public sealed class Raw834Intake(
    IRawEdiFileStore rawFiles,
    IEnrollmentImportRunRepository runs,
    IEnrollmentImportRunScheduler scheduler,
    IOptions<EnrollmentImportRunOptions> options,
    TimeProvider clock,
    ILogger<Raw834Intake> logger,
    IX12InterchangeIntake? interchangeIntake = null,
    IOptions<X12InterchangeOptions>? interchangeOptions = null,
    IEdiFileRecorder? ediFiles = null)
{
    private readonly IX12InterchangeIntake? _intake = interchangeOptions?.Value.Enabled == false ? null : interchangeIntake;

    public async Task<Raw834IntakeOutcome> ReceiveAsync(
        string tenantId, string? actorId, Stream content, string? fileName, string? source, string? sourcePath, CancellationToken ct = default)
    {
        // 1. Stage: the bytes go straight to storage.
        var key = await rawFiles.SaveAsync(tenantId, "834", content, ct);

        // 2. The envelope, from a skeleton read back from storage as a stream.
        X12EnvelopeSkeletonResult skeleton;
        await using (var stored = await rawFiles.OpenAsync(tenantId, key, ct))
            skeleton = await X12EnvelopeSkeleton.BuildAsync(stored, ct);

        InterchangeIntakeResult? envelope = null;
        InterchangeIntakeRequest? request = null;
        if (_intake is not null)
        {
            request = new InterchangeIntakeRequest
            {
                TenantId = tenantId,
                Content = skeleton.Skeleton,
                TransactionType = "834",
                FileName = fileName,
            };
            envelope = await _intake.ReceiveAsync(request, ct);
        }

        // 3. The file record, for every file whatever its envelope.
        var ediFile = ediFiles is null ? null : await ediFiles.RecordReceivedAsync(new EdiFileIntake
        {
            TenantId = tenantId,
            Content = skeleton.Skeleton,
            SizeBytes = skeleton.SizeBytes,
            Sha256 = skeleton.Sha256,
            RawDocumentId = key,
            TransactionType = "834",
            Source = source,
            FileName = fileName,
            SourcePath = sourcePath,
            ReceivedBy = actorId,
        }, envelope, ct);

        if (envelope is { IsRejected: true })
        {
            logger.LogWarning("834 file {FileName} for tenant {TenantId} rejected at the interchange envelope",
                LogSanitizer.Sanitize(fileName), LogSanitizer.Sanitize(tenantId));
            return new Raw834IntakeOutcome { Envelope = envelope, EdiFile = ediFile };
        }

        // 4. The run.
        var now = clock.GetUtcNow().UtcDateTime;
        var run = new EnrollmentImportRun
        {
            TenantId = tenantId,
            BatchId = $"BATCH-{now:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..40],
            FileName = fileName,
            EdiFileId = ediFile?.Id,
            ActorId = actorId,
            Status = EnrollmentImportRunStatus.Queued,
            RawStore = rawFiles.Kind,
            RawDocumentKey = key,
            Source = source,
            SourcePath = sourcePath,
            StartedAt = now,
            QueuedAt = now,
            UpdatedAt = now,
        };
        if (envelope is not null)
        {
            run.AcceptedInterchanges = envelope.Interchanges.Select((o, i) => (o, i)).Where(x => !x.o.IsRejected).Select(x => x.i).ToList();
            run.ClaimedReceipts = envelope.Interchanges
                .Where(o => o.ReceiptClaimedAt is not null && o.Envelope.Header is not null)
                .Select(o => new ClaimedInterchangeReceipt
                {
                    PartyKey = o.Envelope.Header!.PartyKey,
                    ControlNumber = o.Envelope.Header.ControlNumber,
                    ClaimedAt = o.ReceiptClaimedAt!.Value,
                }).ToList();
            run.InterchangeAcknowledgmentCode = envelope.Interchanges.Any(i => i.IsRejected) ? Ta1AckCodes.Rejected
                : envelope.Interchanges.Any(i => i.Decision.AckCode == Ta1AckCodes.AcceptedWithErrors) ? Ta1AckCodes.AcceptedWithErrors
                : Ta1AckCodes.Accepted;
            run.AcknowledgmentTa1 = envelope.Ta1;
            run.Ta1AcknowledgmentIds = envelope.AcknowledgmentIds.ToList();
        }

        try
        {
            await runs.CreateAsync(run);
        }
        catch when (envelope is not null && request is not null)
        {
            // Nothing will import it: give the ISA13 back so a resend is accepted.
            await _intake!.ReleaseAsync(request, envelope, CancellationToken.None);
            throw;
        }

        await scheduler.ScheduleAsync(run, ct);
        return new Raw834IntakeOutcome { Envelope = envelope, EdiFile = ediFile, Run = run };
    }

    /// <summary>The run once it finishes, or as it stands when <see cref="EnrollmentImportRunOptions.WaitSeconds"/> pass.</summary>
    public async Task<EnrollmentImportRun> WaitAsync(EnrollmentImportRun run, CancellationToken ct = default)
    {
        var deadline = clock.GetUtcNow().AddSeconds(Math.Max(0, options.Value.WaitSeconds));
        var current = run;
        while (true)
        {
            current = await runs.GetAsync(run.TenantId, run.Id, ct) ?? current;
            if (EnrollmentImportRunStatus.IsFinished(current.Status) || clock.GetUtcNow() >= deadline)
                return current;
            await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
        }
    }
}
