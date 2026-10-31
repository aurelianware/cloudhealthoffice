using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Security;
using EnrollmentImportService.Services;
using EnrollmentImportService.Services.Cpcds;
using Microsoft.AspNetCore.Mvc;

namespace EnrollmentImportService.Controllers;

/// <summary>
/// Members and coverage from a core's CPCDS extract: the companion to
/// claims-service's CPCDS claim import (docs/integration/CPCDS-FILE-DROP-IMPORTER.md).
/// The files are turned into the 834 enrollment shape and processed by the
/// same import as an 834, so members, sponsors and coverage are created and
/// reconciled exactly as they would be from one.
/// </summary>
[ApiController]
[Route("api/v1/enrollment/import/cpcds")]
[Produces("application/json")]
public class CpcdsEnrollmentController : ControllerBase
{
    private readonly IEnrollmentImportService _import;
    private readonly IPlanCodeGapReportService _gapReport;
    private readonly ICurrentActor _actor;
    private readonly ILogger<CpcdsEnrollmentController> _logger;

    public CpcdsEnrollmentController(
        IEnrollmentImportService import,
        IPlanCodeGapReportService gapReport,
        ICurrentActor actor,
        ILogger<CpcdsEnrollmentController> logger)
    {
        _import = import;
        _gapReport = gapReport;
        _actor = actor;
        _logger = logger;
    }

    /// <summary>
    /// Loads a CPCDS member file and, optionally, its coverage file. Every
    /// member is filed under the core's own member id; reloading is safe
    /// (create when new, otherwise update). Coverage rows need the tenant's
    /// plan-code mapping; send <c>dryRun=true</c> first to get the plan-code
    /// gap report without writing anything.
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(EnrollmentController.MaxRawUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = EnrollmentController.MaxRawUploadBytes)]
    [ProducesResponseType(typeof(CpcdsEnrollmentImportResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(CpcdsEnrollmentImportResult), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CpcdsEnrollmentImportResult>> Import(
        [FromForm] IFormFile members,
        [FromForm] IFormFile? coverages = null,
        [FromForm] bool dryRun = false,
        CancellationToken ct = default)
    {
        if (members is null || members.Length == 0)
            return BadRequest(new { error = "A non-empty CPCDS member file is required." });

        var membersContent = await ReadAsync(members, ct);
        var coveragesContent = coverages is { Length: > 0 } ? await ReadAsync(coverages, ct) : null;

        var projection = CpcdsEnrollmentProjector.Project(membersContent, coveragesContent, members.FileName);
        var result = new CpcdsEnrollmentImportResult
        {
            DryRun = dryRun,
            MemberRows = projection.MemberRows,
            CoverageRows = projection.CoverageRows,
            MembersInFile = projection.Enrollment.Enrollments.Count,
            FileErrors = projection.FileErrors,
            RowErrors = projection.RowErrors,
            Warnings = projection.Warnings,
        };
        if (projection.FileErrors.Count > 0)
            return UnprocessableEntity(result);

        var tenantId = _actor.TenantId;
        result.PlanCodes = await _gapReport.BuildReportAsync(projection.Enrollment, tenantId, ct);
        if (dryRun)
            return Ok(result);

        projection.Enrollment.ActorId = _actor.UserId;
        _logger.LogInformation(
            "Importing CPCDS members {FileName} for tenant {TenantId}: {Members} members, {Coverage} coverage rows",
            LogSanitizer.Sanitize(members.FileName), LogSanitizer.Sanitize(tenantId),
            result.MembersInFile, result.CoverageRows);
        result.Import = await _import.ImportEnrollmentAsync(projection.Enrollment, tenantId);
        return Ok(result);
    }

    private static async Task<string> ReadAsync(IFormFile file, CancellationToken ct)
    {
        using var reader = new StreamReader(file.OpenReadStream());
        return await reader.ReadToEndAsync(ct);
    }
}

/// <summary>What a CPCDS member and coverage load found and did.</summary>
public sealed class CpcdsEnrollmentImportResult
{
    public bool DryRun { get; init; }
    public int MemberRows { get; init; }
    public int CoverageRows { get; init; }

    /// <summary>Members that will be (or were) processed: member rows less duplicates and rows refused below.</summary>
    public int MembersInFile { get; init; }
    public List<string> FileErrors { get; init; } = [];

    /// <summary>Members or coverage rows kept out of the load, and why.</summary>
    public List<string> RowErrors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];

    /// <summary>Which of the file's plan codes are mapped to CHO benefit plans and which are not. Unmapped coverage is not loaded.</summary>
    public PlanCodeGapReport? PlanCodes { get; set; }

    /// <summary>The 834 import's own result (counts, errors, batch id). Null on a dry run.</summary>
    public ImportResult? Import { get; set; }
}
