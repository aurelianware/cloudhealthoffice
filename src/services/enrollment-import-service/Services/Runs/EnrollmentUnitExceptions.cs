using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EnrollmentImportService.Models;
using EnrollmentImportService.Services.Edi;

namespace EnrollmentImportService.Services.Runs;

/// <summary>
/// The enrollment exception for a unit of a run that failed as a whole (its
/// import threw) or was interrupted (a worker stopped while applying it), so
/// the change reaches the Enrollment834 work queues instead of only an error
/// line on the run (enrollment-operations.md §3, edi-operations.md §11).
/// Its id is derived from tenant, run and sequence, so a retry or a
/// redelivery opens the same exception, never a second one.
/// </summary>
public static class EnrollmentUnitExceptions
{
    private static readonly JsonSerializerOptions CopyOptions = new();

    public static string IdFor(string tenantId, string runId, int sequence)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId}\u001f{runId}\u001f{sequence}"));
        return "unit-" + Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    /// <summary>
    /// <paramref name="received"/> is the unit's change as parsed, copied before
    /// the import could touch it: a subscriber with its dependents, or one
    /// standalone dependent. Reprocessing it re-runs exactly that.
    /// </summary>
    public static EnrollmentException Build(EnrollmentUnit unit, Enrollment834 received, string reasonCode, string message, DateTime now)
    {
        var id = IdFor(unit.TenantId, unit.RunId, unit.Sequence);
        var subscriber = received.Enrollments.FirstOrDefault();
        var dependent = subscriber is null ? received.DependentEnrollments.FirstOrDefault() : null;
        var exception = new EnrollmentException
        {
            Id = id,
            TenantId = unit.TenantId,
            Number = EnrollmentException.NumberFor(id),
            Status = EnrollmentExceptionStatus.Open,
            BatchId = unit.BatchId,
            FileName = received.FileName,
            EdiFileId = unit.EdiFileId,
            FilePosition = unit.FilePosition,
            ReasonCode = reasonCode,
            Errors = [new EnrollmentTransactionError { Code = reasonCode, Message = message }],
            CreatedAt = now,
            UpdatedAt = now,
        };

        if (subscriber is not null)
        {
            exception.SourceKind = EnrollmentExceptionSourceKind.Subscriber;
            exception.Subscriber = Copy(subscriber);
            exception.GroupNumber = subscriber.GroupNumber;
            exception.SubscriberId = subscriber.SubscriberId;
            exception.MemberName = $"{subscriber.Demographics?.FirstName} {subscriber.Demographics?.LastName}".Trim() is { Length: > 0 } n ? n : null;
            exception.MaintenanceTypeCode = subscriber.MaintenanceType;
            exception.MaintenanceReasonCode = subscriber.MaintenanceReason;
            exception.EffectiveDate = subscriber.EnrollmentDate;
            exception.TerminationDate = subscriber.TerminationDate;
        }
        else if (dependent is not null)
        {
            exception.SourceKind = EnrollmentExceptionSourceKind.Dependent;
            exception.Dependent = Copy(dependent);
            exception.GroupNumber = dependent.GroupNumber;
            exception.SubscriberId = dependent.SubscriberId;
            exception.MemberName = $"{dependent.FirstName} {dependent.LastName}".Trim() is { Length: > 0 } n ? n : null;
            exception.MaintenanceTypeCode = dependent.MaintenanceType;
            exception.MaintenanceReasonCode = dependent.MaintenanceReason;
            exception.EffectiveDate = dependent.EnrollmentDate;
            exception.TerminationDate = dependent.TerminationDate;
        }
        return exception;
    }

    public static T Copy<T>(T value) where T : class =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, CopyOptions), CopyOptions)!;

    /// <summary>The parsed unit as dispatched, for a unit found failed on resume.</summary>
    public static EnrollmentUnit ToUnit(EnrollmentImportRun run, ParsedEnrollmentUnit parsed) => new()
    {
        TenantId = run.TenantId,
        RunId = run.Id,
        EdiFileId = run.EdiFileId,
        BatchId = run.BatchId,
        ActorId = run.ActorId,
        Sequence = parsed.Sequence,
        FilePosition = parsed.FilePosition,
        OrderingKey = parsed.OrderingKey,
        Kind = parsed.Kind,
        Batch = parsed.Batch,
    };
}
