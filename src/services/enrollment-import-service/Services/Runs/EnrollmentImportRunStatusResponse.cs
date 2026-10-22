using EnrollmentImportService.Models;

namespace EnrollmentImportService.Services.Runs;

/// <summary>An import run as callers see it: status, progress and counts. No storage keys.</summary>
public sealed class EnrollmentImportRunStatusResponse
{
    public string ImportRunId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string? EdiFileId { get; init; }
    public string? FileName { get; init; }
    public string BatchId { get; init; } = string.Empty;
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public int? ExpectedUnits { get; init; }
    public int CompletedUnits { get; init; }
    public int FailedUnits { get; init; }
    public int SuccessCount { get; init; }
    public int FailedCount { get; init; }
    public int SkippedCount { get; init; }
    public string? FailureKind { get; init; }
    public string? FailureMessage { get; init; }
    public string? InterchangeAcknowledgmentCode { get; init; }
    public string? AcknowledgmentTa1 { get; init; }
    public string StatusUrl { get; init; } = string.Empty;

    /// <summary>Failed units (first 100, file order), each with the enrollment exception its change is worked in.</summary>
    public IReadOnlyList<FailedUnitView> FailedUnitDetails { get; init; } = [];

    public static EnrollmentImportRunStatusResponse From(EnrollmentImportRun run, IReadOnlyList<EnrollmentImportUnitRecord>? failedUnits = null) => new()
    {
        FailedUnitDetails = (failedUnits ?? []).Select(u => new FailedUnitView
        {
            Sequence = u.Sequence, FilePosition = u.FilePosition, Error = u.Error, ExceptionId = u.ExceptionId,
        }).ToList(),
        ImportRunId = run.Id,
        Status = run.Status,
        EdiFileId = run.EdiFileId,
        FileName = run.FileName,
        BatchId = run.BatchId,
        StartedAt = run.StartedAt,
        CompletedAt = run.CompletedAt,
        ExpectedUnits = run.ExpectedUnits,
        CompletedUnits = run.CompletedUnits,
        FailedUnits = run.FailedUnits,
        SuccessCount = run.SuccessCount,
        FailedCount = run.FailedCount,
        SkippedCount = run.SkippedCount,
        FailureKind = run.FailureKind,
        FailureMessage = run.FailureMessage,
        InterchangeAcknowledgmentCode = run.InterchangeAcknowledgmentCode,
        AcknowledgmentTa1 = run.AcknowledgmentTa1,
        StatusUrl = $"api/v1/enrollment/import/runs/{run.Id}",
    };
}

public sealed class FailedUnitView
{
    public int Sequence { get; init; }
    public string FilePosition { get; init; } = string.Empty;
    public string? Error { get; init; }
    public string? ExceptionId { get; init; }
}
