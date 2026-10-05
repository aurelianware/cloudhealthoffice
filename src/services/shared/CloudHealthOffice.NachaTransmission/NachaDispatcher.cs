using System.Text.Json.Serialization;
using CloudHealthOffice.FieldProtection;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.NachaTransmission;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NachaTransmissionStatus
{
    /// <summary>Delivered to the bank; see the receipt.</summary>
    Transmitted,

    /// <summary>
    /// Not delivered (not configured or the upload failed). Held encrypted for
    /// up to 7 days; a platform admin must retrieve it or a second approver retry it.
    /// </summary>
    AwaitingRetrieval,

    /// <summary>Not delivered and could not be held: the payments stay Pending.</summary>
    NotSent
}

/// <summary>What happened to a file. Never carries the file.</summary>
public sealed class NachaDispatchOutcome
{
    public NachaTransmissionStatus Status { get; init; }
    public NachaTransmissionReceipt? Receipt { get; init; }

    /// <summary>Why it was not delivered (safe to show).</summary>
    public string? Reason { get; init; }

    /// <summary>When a held file is deleted.</summary>
    public DateTime? HeldUntil { get; init; }
}

/// <summary>A held file, for a platform admin who must deliver it by hand.</summary>
public sealed class NachaRetrievedFile
{
    public required string FileName { get; init; }
    public required string Content { get; init; }
    public required NachaHeldFile Record { get; init; }

    /// <summary>True for the first retrieval: the payments it carries are now the admin's to deliver.</summary>
    public bool FirstRetrieval { get; init; }
}

public sealed class NachaHeldFileNotFoundException : Exception
{
    public NachaHeldFileNotFoundException(string fileReference) : base($"No held NACHA file {fileReference}.") { }
}

public sealed class NachaHeldFileExpiredException : Exception
{
    public NachaHeldFileExpiredException(string fileReference)
        : base($"Held NACHA file {fileReference} has expired and was deleted.") { }
}

public sealed class NachaHeldFileStateException : Exception
{
    public NachaHeldFileStateException(string message) : base(message) { }
}

/// <summary>Retrieval or retry by someone who may not do it (403).</summary>
public sealed class NachaSeparationOfDutiesException : Exception
{
    public NachaSeparationOfDutiesException(string message) : base(message) { }
}

/// <summary>The acting user, as the service's token check established it.</summary>
public sealed record NachaActor(string UserId, bool IsService);

public sealed class NachaTransmissionOptions
{
    /// <summary>The calling service (premium-billing-service, capitation-service).</summary>
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>How long an undelivered file is held. 7 days.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);
}

/// <summary>
/// Sends a released NACHA file to the bank, and when that is not possible
/// holds it encrypted for retrieval or retry. The releasing user never gets
/// the file: a platform admin may retrieve it (with a reason, audited), and a
/// different approver may retry it.
/// </summary>
public interface INachaDispatcher
{
    Task<NachaDispatchOutcome> DispatchAsync(NachaTransmissionRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NachaHeldFile>> ListHeldAsync(string tenantId, CancellationToken cancellationToken = default);

    Task<NachaHeldFile?> GetHeldAsync(string tenantId, string fileReference, CancellationToken cancellationToken = default);

    /// <summary>Re-sends a held file. The actor must be a user who did not release it.</summary>
    Task<NachaDispatchOutcome> RetryAsync(string tenantId, string fileReference, NachaActor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// The held file for a platform admin (the caller checks platform:admin).
    /// Never for a service or the releasing user; a reason is required; every
    /// retrieval is recorded and logged.
    /// </summary>
    Task<NachaRetrievedFile> RetrieveAsync(string tenantId, string fileReference, NachaActor actor, string reason, CancellationToken cancellationToken = default);
}

public sealed class NachaDispatcher : INachaDispatcher
{
    public static readonly EventId TransmittedEvent = new(4901, "NachaFileTransmitted");
    public static readonly EventId HeldEvent = new(4902, "NachaFileHeldForRetrieval");
    public static readonly EventId NotSentEvent = new(4903, "NachaFileNotSent");
    public static readonly EventId RetriedEvent = new(4904, "NachaFileRetried");
    public static readonly EventId RetrievedEvent = new(4905, "NachaFileRetrievedByPlatformAdmin");
    public static readonly EventId RefusedEvent = new(4906, "NachaFileAccessRefused");

    private readonly INachaTransmitter _transmitter;
    private readonly INachaHeldFileStore _store;
    private readonly IFieldProtector _protector;
    private readonly NachaTransmissionOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<NachaDispatcher> _logger;

    public NachaDispatcher(
        INachaTransmitter transmitter,
        INachaHeldFileStore store,
        IFieldProtector protector,
        NachaTransmissionOptions options,
        ILogger<NachaDispatcher> logger,
        TimeProvider? clock = null)
    {
        _transmitter = transmitter;
        _store = store;
        _protector = protector;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public async Task<NachaDispatchOutcome> DispatchAsync(NachaTransmissionRequest request, CancellationToken cancellationToken = default)
    {
        var facts = NachaFileFacts.From(request.Content);
        string reason;
        try
        {
            var receipt = await _transmitter.TransmitAsync(request, cancellationToken);
            _logger.LogInformation(TransmittedEvent,
                "AUDIT NACHA file {FileReference} ({Service}) released by {User} for tenant {TenantId} delivered to the bank: " +
                "{EntryCount} entries, debits {Debits}, credits {Credits}, sha256 {Sha256}",
                Sanitize(request.FileReference), _options.ServiceName, Sanitize(request.TransmittedBy), Sanitize(request.TenantId),
                receipt.EntryCount, receipt.TotalDebitAmount, receipt.TotalCreditAmount, receipt.Sha256);
            return new NachaDispatchOutcome { Status = NachaTransmissionStatus.Transmitted, Receipt = receipt };
        }
        catch (NachaTransmissionException ex)
        {
            reason = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            reason = $"Transmission failed ({ex.GetType().Name}).";
        }

        // Not delivered: hold it encrypted for retrieval or retry.
        var heldUntil = Now.Add(_options.Retention);
        try
        {
            var held = new NachaHeldFile
            {
                TenantId = request.TenantId,
                SourceService = _options.ServiceName,
                FileReference = request.FileReference,
                FileName = request.FileName,
                ProtectedContent = _protector.Protect(request.Content),
                Sha256 = facts.Sha256,
                ByteSize = facts.ByteSize,
                EntryCount = facts.EntryCount,
                TotalDebitAmount = facts.TotalDebitAmount,
                TotalCreditAmount = facts.TotalCreditAmount,
                RunId = request.RunId,
                BatchId = request.BatchId,
                ReleasedBy = request.TransmittedBy,
                Reason = reason,
                Status = NachaHeldFileStatus.AwaitingRetrieval,
                CreatedAt = Now,
                ExpiresAt = heldUntil,
            };
            if (!_protector.IsProtected(held.ProtectedContent))
                throw new FieldProtectionException("The NACHA file could not be encrypted.");
            await _store.SaveAsync(held, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(NotSentEvent,
                "AUDIT NACHA file {FileReference} ({Service}) for tenant {TenantId} was not delivered ({Reason}) and could not be held " +
                "({Error}); its payments stay Pending",
                Sanitize(request.FileReference), _options.ServiceName, Sanitize(request.TenantId), Sanitize(reason), ex.GetType().Name);
            return new NachaDispatchOutcome
            {
                Status = NachaTransmissionStatus.NotSent,
                Reason = $"{reason} The file could not be held for retrieval ({ex.GetType().Name}); nothing was sent and the payments stay Pending.",
            };
        }

        _logger.LogWarning(HeldEvent,
            "AUDIT NACHA file {FileReference} ({Service}) released by {User} for tenant {TenantId} was not delivered: {Reason}. " +
            "Held encrypted until {HeldUntil:o} for platform-admin retrieval or retry by another approver",
            Sanitize(request.FileReference), _options.ServiceName, Sanitize(request.TransmittedBy), Sanitize(request.TenantId), Sanitize(reason), heldUntil);
        return new NachaDispatchOutcome { Status = NachaTransmissionStatus.AwaitingRetrieval, Reason = reason, HeldUntil = heldUntil };
    }

    public Task<IReadOnlyList<NachaHeldFile>> ListHeldAsync(string tenantId, CancellationToken cancellationToken = default)
        => _store.ListOpenAsync(tenantId, _options.ServiceName, cancellationToken);

    public async Task<NachaHeldFile?> GetHeldAsync(string tenantId, string fileReference, CancellationToken cancellationToken = default)
    {
        var held = await _store.GetAsync(tenantId, fileReference, cancellationToken);
        return held != null && held.SourceService == _options.ServiceName ? held : null;
    }

    public async Task<NachaDispatchOutcome> RetryAsync(string tenantId, string fileReference, NachaActor actor, CancellationToken cancellationToken = default)
    {
        var held = await LoadAsync(tenantId, fileReference, cancellationToken);
        if (actor.IsService)
            throw Refuse(held, actor, "retry", "Retrying a NACHA file needs a user with payments:approve, not a service token.");
        if (string.Equals(actor.UserId, held.ReleasedBy, StringComparison.OrdinalIgnoreCase))
            throw Refuse(held, actor, "retry",
                "Separation of duties: you released this NACHA file, so you cannot retry it. Another user with payments:approve must.");
        if (held.Status != NachaHeldFileStatus.AwaitingRetrieval)
            throw new NachaHeldFileStateException($"NACHA file {fileReference} is {held.Status}, not awaiting retrieval.");

        var content = Decrypt(held);
        if (!await _store.TryClaimAsync(tenantId, fileReference, actor.UserId, Now, cancellationToken))
            throw new NachaHeldFileStateException($"NACHA file {fileReference} is already being retried or was resolved.");

        try
        {
            var receipt = await _transmitter.TransmitAsync(new NachaTransmissionRequest
            {
                TenantId = tenantId,
                FileReference = held.FileReference,
                FileName = held.FileName,
                Content = content,
                RunId = held.RunId,
                BatchId = held.BatchId,
                TransmittedBy = actor.UserId,
            }, cancellationToken);
            await _store.MarkTransmittedAsync(tenantId, fileReference, receipt, cancellationToken);
            _logger.LogInformation(RetriedEvent,
                "AUDIT NACHA file {FileReference} ({Service}) for tenant {TenantId}, released by {ReleasedBy}, retried by {User} and delivered: sha256 {Sha256}",
                Sanitize(fileReference), _options.ServiceName, Sanitize(tenantId), Sanitize(held.ReleasedBy), Sanitize(actor.UserId), receipt.Sha256);
            return new NachaDispatchOutcome { Status = NachaTransmissionStatus.Transmitted, Receipt = receipt };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var reason = ex is NachaTransmissionException ? ex.Message : $"Transmission failed ({ex.GetType().Name}).";
            await _store.ReleaseClaimAsync(tenantId, fileReference, reason, CancellationToken.None);
            _logger.LogWarning(RetriedEvent,
                "AUDIT NACHA file {FileReference} ({Service}) for tenant {TenantId} retried by {User} and still not delivered: {Reason}",
                Sanitize(fileReference), _options.ServiceName, Sanitize(tenantId), Sanitize(actor.UserId), Sanitize(reason));
            return new NachaDispatchOutcome { Status = NachaTransmissionStatus.AwaitingRetrieval, Reason = reason, HeldUntil = held.ExpiresAt };
        }
    }

    public async Task<NachaRetrievedFile> RetrieveAsync(
        string tenantId, string fileReference, NachaActor actor, string reason, CancellationToken cancellationToken = default)
    {
        var held = await LoadAsync(tenantId, fileReference, cancellationToken);
        if (actor.IsService)
            throw Refuse(held, actor, "retrieve", "Retrieving a NACHA file needs a platform admin's user token, not a service token.");
        if (string.Equals(actor.UserId, held.ReleasedBy, StringComparison.OrdinalIgnoreCase))
            throw Refuse(held, actor, "retrieve",
                "Separation of duties: you released this NACHA file, so you cannot retrieve it. Another platform admin must.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A reason is required to retrieve a NACHA file.");
        reason = Sanitize(reason.Trim());
        if (reason.Length > 500) reason = reason[..500];
        if (held.Status is not (NachaHeldFileStatus.AwaitingRetrieval or NachaHeldFileStatus.Retrieved))
            throw new NachaHeldFileStateException($"NACHA file {fileReference} is {held.Status}; there is nothing to retrieve.");

        var content = Decrypt(held);
        var first = held.Status == NachaHeldFileStatus.AwaitingRetrieval;
        if (!await _store.RecordRetrievalAsync(tenantId, fileReference, new NachaRetrieval { By = actor.UserId, At = Now, Reason = reason }, cancellationToken))
            throw new NachaHeldFileStateException($"NACHA file {fileReference} changed state; try again.");

        _logger.LogWarning(RetrievedEvent,
            "AUDIT NACHA file {FileReference} ({Service}) for tenant {TenantId}, released by {ReleasedBy}, retrieved by platform admin {User}" +
            " (retrieval {Count}) for: {Reason}. sha256 {Sha256}",
            Sanitize(fileReference), _options.ServiceName, Sanitize(tenantId), Sanitize(held.ReleasedBy), Sanitize(actor.UserId),
            held.Retrievals.Count + 1, reason, held.Sha256);
        return new NachaRetrievedFile { FileName = held.FileName, Content = content, Record = held, FirstRetrieval = first };
    }

    private async Task<NachaHeldFile> LoadAsync(string tenantId, string fileReference, CancellationToken cancellationToken)
    {
        var held = await GetHeldAsync(tenantId, fileReference, cancellationToken)
            ?? throw new NachaHeldFileNotFoundException(fileReference);
        if (held.ExpiresAt <= Now)
            throw new NachaHeldFileExpiredException(fileReference);
        return held;
    }

    private string Decrypt(NachaHeldFile held)
    {
        if (string.IsNullOrEmpty(held.ProtectedContent) || !_protector.IsProtected(held.ProtectedContent))
            throw new NachaHeldFileStateException($"NACHA file {held.FileReference} is not held encrypted; refusing to use it.");
        var content = _protector.Unprotect(held.ProtectedContent)!;
        if (NachaFileFacts.From(content).Sha256 != held.Sha256)
            throw new NachaHeldFileStateException($"NACHA file {held.FileReference} does not match its recorded SHA-256; refusing to use it.");
        return content;
    }

    private NachaSeparationOfDutiesException Refuse(NachaHeldFile held, NachaActor actor, string action, string message)
    {
        _logger.LogWarning(RefusedEvent,
            "AUDIT refused: {User} (service: {IsService}) may not {Action} NACHA file {FileReference} ({Service}) for tenant {TenantId}",
            Sanitize(actor.UserId), actor.IsService, action, Sanitize(held.FileReference), _options.ServiceName, Sanitize(held.TenantId));
        return new NachaSeparationOfDutiesException(message);
    }

    private static string Sanitize(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", " ");
}
