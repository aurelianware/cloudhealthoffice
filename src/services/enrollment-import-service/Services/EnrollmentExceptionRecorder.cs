using System.Text.Json;
using CloudHealthOffice.Infrastructure.Logging;
using EnrollmentImportService.Models;
using EnrollmentImportService.Repositories;

namespace EnrollmentImportService.Services;

/// <summary>What a rejected transaction would re-run, captured when it was recorded.</summary>
internal sealed record ExceptionSource(EnrollmentExceptionSourceKind Kind, MemberEnrollment? Subscriber, Dependent? Dependent, bool TopLevel)
{
    private static readonly JsonSerializerOptions Copy = new();

    public static ExceptionSource ForSubscriber(MemberEnrollment subscriber) =>
        new(EnrollmentExceptionSourceKind.Subscriber, DeepCopy(subscriber), null, TopLevel: true);

    public static ExceptionSource ForStandaloneDependent(Dependent dependent) =>
        new(EnrollmentExceptionSourceKind.Dependent, null, DeepCopy(dependent), TopLevel: true);

    /// <summary>
    /// An attached dependent that failed on its own: kept as a standalone
    /// dependent of its subscriber, with what it inherited from the
    /// subscriber's loop filled in, so it can be reprocessed by itself.
    /// </summary>
    public static ExceptionSource ForAttachedDependent(Dependent dependent, string subscriberMemberId, MemberEnrollment? subscriber)
    {
        var copy = DeepCopy(dependent)!;
        copy.SubscriberId ??= subscriberMemberId;
        if (subscriber is not null)
        {
            copy.MaintenanceType ??= subscriber.MaintenanceType;
            copy.MaintenanceReason ??= subscriber.MaintenanceReason;
            copy.BenefitStatus ??= subscriber.BenefitStatus;
            copy.GroupNumber ??= subscriber.GroupNumber;
            copy.EnrollmentDate ??= subscriber.EnrollmentDate;
            copy.TerminationDate ??= subscriber.TerminationDate;
        }
        return new(EnrollmentExceptionSourceKind.Dependent, null, copy, TopLevel: false);
    }

    private static T? DeepCopy<T>(T? value) where T : class =>
        value is null ? null : JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Copy), Copy);
}

/// <summary>
/// Turns rejected transactions into <see cref="EnrollmentException"/>s, and
/// records the outcome of reprocessing one. Like the transaction log, a
/// failure here is logged and never fails the import.
/// </summary>
internal sealed class EnrollmentExceptionRecorder
{
    private readonly IEnrollmentExceptionRepository? _exceptions;
    private readonly ILogger _logger;

    public EnrollmentExceptionRecorder(IEnrollmentExceptionRepository? exceptions, ILogger logger)
    {
        _exceptions = exceptions;
        _logger = logger;
    }

    public async Task RecordAsync(string tenantId, Enrollment834 batch, EnrollmentTransaction txn, ExceptionSource source, DateTime now)
    {
        if (_exceptions is null) return;
        try
        {
            // Reprocessing an exception: its top-level outcome belongs to that exception.
            if (batch.ReprocessExceptionId is { } reprocessing && source.TopLevel)
            {
                await RecordReprocessOutcomeAsync(tenantId, reprocessing, batch.ActorId, txn, now);
                return;
            }
            if (txn.Status != "Rejected") return;

            var exception = new EnrollmentException
            {
                Id = txn.Id,
                TenantId = tenantId,
                Number = EnrollmentException.NumberFor(txn.Id),
                Status = EnrollmentExceptionStatus.Open,
                BatchId = txn.BatchId,
                FileName = txn.FileName,
                EdiFileId = txn.EdiFileId,
                FilePosition = txn.FilePosition,
                TransactionIds = [txn.Id],
                GroupNumber = source.Subscriber?.GroupNumber ?? source.Dependent?.GroupNumber,
                SubscriberId = txn.SubscriberId,
                MemberId = string.IsNullOrEmpty(txn.MemberId) ? null : txn.MemberId,
                MemberName = txn.MemberName,
                MaintenanceTypeCode = txn.MaintenanceTypeCode,
                MaintenanceReasonCode = txn.MaintenanceReasonCode,
                EffectiveDate = txn.EffectiveDate,
                TerminationDate = txn.TerminationDate,
                ReasonCode = txn.ReasonCode ?? EnrollmentRejectionReasons.UnexpectedError,
                Errors = txn.Errors.ToList(),
                SourceKind = source.Kind,
                Subscriber = source.Subscriber,
                Dependent = source.Dependent,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await _exceptions.TryCreateAsync(exception);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record the enrollment exception for transaction {TransactionId}",
                LogSanitizer.Sanitize(txn.Id));
        }
    }

    private async Task RecordReprocessOutcomeAsync(string tenantId, string exceptionId, string? actor, EnrollmentTransaction txn, DateTime now)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var exception = await _exceptions!.GetAsync(tenantId, exceptionId);
            if (exception is null) return;
            var expected = exception.Version;
            exception.TransactionIds.Add(txn.Id);
            exception.UpdatedAt = now;
            if (txn.Status == "Rejected")
            {
                exception.ReasonCode = txn.ReasonCode ?? EnrollmentRejectionReasons.UnexpectedError;
                exception.Errors = txn.Errors.ToList();
                exception.MemberId ??= string.IsNullOrEmpty(txn.MemberId) ? null : txn.MemberId;
                exception.Actions.LastOrDefault(a => a.Action == EnrollmentExceptionActions.Reprocessed)
                    ?.Apply(a => a.OutcomeReasonCode = exception.ReasonCode);
            }
            else
            {
                exception.Status = EnrollmentExceptionStatus.Resolved;
                exception.Resolution = EnrollmentExceptionResolution.Applied;
                exception.ResolvedAt = now;
                exception.ResolvedBy = actor;
                exception.HeldUntil = null;
                exception.MemberId = string.IsNullOrEmpty(txn.MemberId) ? exception.MemberId : txn.MemberId;
            }
            try
            {
                await _exceptions.SaveAsync(exception, expected);
                return;
            }
            catch (EnrollmentExceptionConcurrencyException) when (attempt < 2)
            {
            }
        }
    }
}

public static class EnrollmentExceptionActions
{
    public const string Reprocessed = "Reprocessed";
    public const string Corrected = "Corrected";
    public const string Matched = "Matched";
    public const string Held = "Held";
    public const string SentBack = "SentBackToSponsor";
    public const string NoAction = "NoAction";
}

internal static class ObjectExtensions
{
    public static void Apply<T>(this T value, Action<T> action) => action(value);
}
