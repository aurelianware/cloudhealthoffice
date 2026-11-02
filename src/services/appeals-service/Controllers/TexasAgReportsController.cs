using System.Globalization;
using System.Text;
using AppealsService.Middleware;
using AppealsService.Reports;
using AppealsService.Reports.Texas;
using AppealsService.Repositories;
using AppealsService.Services;
using CloudHealthOffice.Infrastructure.Audit;
using CloudHealthOffice.Infrastructure.Reporting;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace AppealsService.Controllers;

/// <summary>
/// Texas HHSC monthly appeals and complaints text files (UMCM 5.24.6 and
/// 5.24.5), and the per-case HHSC codes they need. Reading or setting a
/// case's codes is an appeal read or write (<c>appeals:read</c> /
/// <c>appeals:write</c>); building a file is a report
/// (<c>reports:um</c> or <c>reports:compliance</c>) and is written to the
/// record access log with every case and member in it.
/// </summary>
[ApiController]
[Route("api/appeals")]
[Produces("application/json")]
public class TexasAgReportsController : ControllerBase
{
    public const string MemberAppealsFile = "member-appeals";
    public const string ComplaintsFile = "complaints";

    private readonly IAppealRepository _appeals;
    private readonly ITexasAgCodesStore _codes;
    private readonly IStateFileCatalog _catalog;
    private readonly ICurrentActor _actor;
    private readonly ILogger<TexasAgReportsController> _logger;
    private readonly TimeProvider _clock;

    public TexasAgReportsController(IAppealRepository appeals, ITexasAgCodesStore codes, IStateFileCatalog catalog, ICurrentActor actor,
        ILogger<TexasAgReportsController> logger, TimeProvider? clock = null)
    {
        _appeals = appeals;
        _codes = codes;
        _catalog = catalog;
        _actor = actor;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    private string TenantId => HttpContext.GetTenantId();

    /// <summary>The case's Texas codes (empty when none are recorded).</summary>
    [HttpGet("{id}/texas-codes")]
    [AuditRecordAccess("Appeal", RecordAccessActions.View, RecordIdRouteValue = "id")]
    public async Task<IActionResult> GetCodes([FromRoute] string id, CancellationToken ct)
    {
        var appeal = await _appeals.GetByIdAsync(TenantId, id, ct);
        if (appeal is null) return NotFound();
        return Ok(await _codes.GetAsync(TenantId, id, ct) ?? new TexasAgCodes { TenantId = TenantId, AppealId = id });
    }

    /// <summary>Replaces the case's Texas codes. Each code must be one HHSC lists for its field.</summary>
    [HttpPut("{id}/texas-codes")]
    public async Task<IActionResult> PutCodes([FromRoute] string id, [FromBody] TexasAgCodesRequest request, CancellationToken ct)
    {
        var appeal = await _appeals.GetByIdAsync(TenantId, id, ct);
        if (appeal is null) return NotFound();

        var errors = request.Validate();
        if (errors.Count > 0)
        {
            foreach (var (field, message) in errors) ModelState.AddModelError(field, message);
            return ValidationProblem(ModelState);
        }

        var codes = request.ToCodes();
        codes.TenantId = TenantId;
        codes.AppealId = id;
        codes.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        codes.UpdatedBy = _actor.UserId;
        return Ok(await _codes.SaveAsync(codes, ct));
    }

    /// <summary>
    /// Builds one month's file for one program. <c>format=txt</c> answers the
    /// fixed-width file to upload to TexConnect (with the count of cases
    /// left out in <c>X-Report-Problems</c>); otherwise JSON lists the cases
    /// in it and the problems that kept others out. Every txt file handed out
    /// is first kept in the document catalog (its id in <c>X-Document-Id</c>):
    /// if it can't be kept, it isn't handed out (503). A blank file (no cases)
    /// has nothing to keep.
    /// </summary>
    [HttpGet("reports/texas/{file}")]
    [RequirePermission(AgReportCatalog.Permission)]
    [AuditRecordAccess("Appeal", "Report", ResultIdProperty = "AppealId")]
    public async Task<IActionResult> BuildFile(
        [FromRoute] string file,
        [FromQuery] string? month,
        [FromQuery] string? program,
        [FromQuery] string? mcoCode,
        [FromQuery] string? planCode,
        [FromQuery] string? format,
        CancellationToken ct)
    {
        TexasAgFile? kind = file switch
        {
            MemberAppealsFile => TexasAgFile.MemberAppeals,
            ComplaintsFile => TexasAgFile.Complaints,
            _ => null,
        };
        if (kind is null) return NotFound(new { error = $"file is {MemberAppealsFile} or {ComplaintsFile}." });
        if (!DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first))
            return BadRequest(new { error = "month is required, as yyyy-MM." });

        var request = new TexasAgFileRequest(first.Year, first.Month, program?.Trim() ?? "", mcoCode?.Trim().ToUpperInvariant() ?? "", planCode?.Trim() ?? "");
        var invalid = TexasAgTextFiles.Validate(request).ToList();
        if (invalid.Count > 0) return BadRequest(new { error = string.Join(" ", invalid) });
        var (start, end) = TexasAgTextFiles.MonthUtc(request.Year, request.Month);
        if (start > _clock.GetUtcNow().UtcDateTime) return BadRequest(new { error = "The month has not begun." });

        var cases = await _appeals.ListForReportingMonthAsync(TenantId, start, end, ct);
        var codes = await _codes.GetManyAsync(TenantId, cases.Select(c => c.Id).ToList(), ct);
        var result = TexasAgTextFiles.Build(kind.Value, request, cases, codes);

        if (!string.Equals(format, "txt", StringComparison.OrdinalIgnoreCase))
        {
            return Ok(new TexasAgFileView(file, $"{request.Year:0000}-{request.Month:00}", result.StateFiscalYear, request.Program,
                result.Complete, result.Items.Count, result.Items, result.Problems));
        }

        // The access log reads the cases from here (a file is not an object it can inspect).
        HttpContext.Items[StandardReports.AuditedRowsItemsKey] = result.Items;
        var name = $"tx-{file}-{request.Program}-{request.Year:0000}{request.Month:00}.txt";
        var bytes = Encoding.ASCII.GetBytes(result.Text);
        if (bytes.Length > 0)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            var runId = $"tx-{file}-{request.Program}-{request.Year:0000}{request.Month:00}-{now:yyyyMMdd'T'HHmmss'Z'}-{Guid.NewGuid():N}"[..^24];
            try
            {
                var stored = await _catalog.StoreAsync(new StateFileUpload(
                    TenantId, _actor.UserId, runId,
                    $"Texas HHSC {(kind == TexasAgFile.MemberAppeals ? "member appeals (UMCM 5.24.6)" : "complaints (UMCM 5.24.5)")} {request.Year:0000}-{request.Month:00}, program {request.Program}",
                    bytes, name, "TX",
                    new Dictionary<string, string>
                    {
                        ["Report"] = file, ["Month"] = $"{request.Year:0000}-{request.Month:00}", ["Program"] = request.Program,
                        ["Rows"] = result.Items.Count.ToString(CultureInfo.InvariantCulture),
                    }), ct);
                Response.Headers["X-Document-Id"] = stored.DocumentId;
                Response.Headers["X-State-Report-Run"] = runId;
            }
            catch (StateFileCatalogException ex)
            {
                _logger.LogError(ex, "Texas {File} file not kept in the document catalog; not handed out", file);
                return Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "The file could not be kept in the document catalog, so it was not handed out. Try again shortly.");
            }
        }

        Response.Headers["X-Report-Problems"] = result.Problems.Count.ToString(CultureInfo.InvariantCulture);
        return File(bytes, "text/plain", name);
    }
}

public sealed record TexasAgFileView(
    string File,
    string ReportingMonth,
    int StateFiscalYear,
    string Program,
    bool Complete,
    int Rows,
    IReadOnlyList<TexasAgFileRow> Items,
    IReadOnlyList<TexasAgFileProblem> Problems);

/// <summary>A case's Texas codes as sent. Codes are checked against HHSC's lists; free text against its field width.</summary>
public sealed class TexasAgCodesRequest
{
    public string? Program { get; set; }
    public string? StateMemberId { get; set; }
    public string? TrackingId { get; set; }
    public string? MethodReceived { get; set; }
    public string? ReceivedBy { get; set; }
    public string? ReceivedBySubcontractor { get; set; }
    public string? BenefitType { get; set; }
    public string? OtherBenefitType { get; set; }
    public string? ServicesImpacted { get; set; }
    public string? OtherServicesImpacted { get; set; }
    public string? ReasonForDenial { get; set; }
    public string? OtherReasonForDenial { get; set; }
    public string? AppealTimeframe { get; set; }
    public string? ExtensionRequestedBy { get; set; }
    public string? AdverseBenefitDetermination { get; set; }
    public string? OtherAdverseBenefitDetermination { get; set; }
    public string? AppealOutcome { get; set; }
    public string? OverturnedOutcome { get; set; }
    public string? PriorAuthorizationNumber { get; set; }
    public string? ComplaintType { get; set; }
    public string? Complainant { get; set; }
    public string? OtherComplainant { get; set; }
    public string? Mdcp { get; set; }
    public string? Disposition { get; set; }
    public string? OtherDispositionDetail { get; set; }
    public string? ComplaintCategory { get; set; }
    public string? ComplaintCategoryDetail { get; set; }
    public string? OtherComplaintCategoryDetail { get; set; }
    public string? DisasterRelated { get; set; }
    public string? ComplaintOutcome { get; set; }

    public List<(string Field, string Message)> Validate()
    {
        var errors = new List<(string, string)>();
        void Code(string field, string? value, IEnumerable<string> allowed)
        {
            if (value is not null && !allowed.Contains(value.Trim())) errors.Add((field, $"'{value}' is not an HHSC code for {field}."));
        }
        void Free(string field, string? value, int width, bool digitsOnly = false)
        {
            if (value is null) return;
            var v = value.Trim();
            if (v.Length > width) errors.Add((field, $"At most {width} characters."));
            else if (v.Any(c => c < 0x20 || c > 0x7E)) errors.Add((field, "Printable ASCII only."));
            else if (digitsOnly && !v.All(char.IsAsciiDigit)) errors.Add((field, "Digits only."));
        }

        Code(nameof(Program), Program, TexasAgCodeLists.Programs.Keys);
        Free(nameof(StateMemberId), StateMemberId, 9, digitsOnly: true);
        if (StateMemberId is { } sid && sid.Trim().Length != 9) errors.Add((nameof(StateMemberId), "The state Medicaid/CHIP number is 9 digits."));
        Free(nameof(TrackingId), TrackingId, 15);
        Code(nameof(MethodReceived), MethodReceived, TexasAgCodeLists.MethodReceived);
        Code(nameof(ReceivedBy), ReceivedBy, TexasAgCodeLists.ReceivedBy);
        Free(nameof(ReceivedBySubcontractor), ReceivedBySubcontractor, 50);
        Code(nameof(BenefitType), BenefitType, TexasAgCodeLists.AppealBenefitTypes.Concat(TexasAgCodeLists.ComplaintBenefitTypes));
        Free(nameof(OtherBenefitType), OtherBenefitType, 15);
        Code(nameof(ServicesImpacted), ServicesImpacted, TexasAgCodeLists.ServicesImpacted);
        Free(nameof(OtherServicesImpacted), OtherServicesImpacted, 15);
        Code(nameof(ReasonForDenial), ReasonForDenial, TexasAgCodeLists.ReasonsForDenial);
        Free(nameof(OtherReasonForDenial), OtherReasonForDenial, 15);
        Code(nameof(AppealTimeframe), AppealTimeframe, TexasAgCodeLists.AppealTimeframes);
        Code(nameof(ExtensionRequestedBy), ExtensionRequestedBy, TexasAgCodeLists.ExtensionRequestedBy);
        Code(nameof(AdverseBenefitDetermination), AdverseBenefitDetermination, TexasAgCodeLists.AdverseBenefitDeterminations);
        Free(nameof(OtherAdverseBenefitDetermination), OtherAdverseBenefitDetermination, 15);
        Code(nameof(AppealOutcome), AppealOutcome, TexasAgCodeLists.AppealOutcomes);
        Code(nameof(OverturnedOutcome), OverturnedOutcome, TexasAgCodeLists.OverturnedOutcomes);
        Free(nameof(PriorAuthorizationNumber), PriorAuthorizationNumber, 50);
        Code(nameof(ComplaintType), ComplaintType, TexasAgCodeLists.ComplaintTypes);
        Code(nameof(Complainant), Complainant, TexasAgCodeLists.Complainants);
        Free(nameof(OtherComplainant), OtherComplainant, 10);
        Code(nameof(Mdcp), Mdcp, TexasAgCodeLists.YesNo);
        Code(nameof(Disposition), Disposition, TexasAgCodeLists.Dispositions);
        Free(nameof(OtherDispositionDetail), OtherDispositionDetail, 50);
        Code(nameof(ComplaintCategory), ComplaintCategory, TexasAgCodeLists.ComplaintCategories);
        Code(nameof(ComplaintCategoryDetail), ComplaintCategoryDetail, TexasAgCodeLists.ComplaintCategoryDetails);
        if (ComplaintCategory is { } cat && ComplaintCategoryDetail is { } det && !det.Trim().StartsWith(cat.Trim(), StringComparison.Ordinal))
            errors.Add((nameof(ComplaintCategoryDetail), $"{det} is not a {cat} detail."));
        Free(nameof(OtherComplaintCategoryDetail), OtherComplaintCategoryDetail, 50);
        Code(nameof(DisasterRelated), DisasterRelated, TexasAgCodeLists.YesNo);
        Code(nameof(ComplaintOutcome), ComplaintOutcome, TexasAgCodeLists.ComplaintOutcomes);
        return errors;
    }

    public TexasAgCodes ToCodes()
    {
        static string? T(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        return new TexasAgCodes
        {
            Program = T(Program), StateMemberId = T(StateMemberId), TrackingId = T(TrackingId), MethodReceived = T(MethodReceived),
            ReceivedBy = T(ReceivedBy), ReceivedBySubcontractor = T(ReceivedBySubcontractor), BenefitType = T(BenefitType),
            OtherBenefitType = T(OtherBenefitType), ServicesImpacted = T(ServicesImpacted), OtherServicesImpacted = T(OtherServicesImpacted),
            ReasonForDenial = T(ReasonForDenial), OtherReasonForDenial = T(OtherReasonForDenial), AppealTimeframe = T(AppealTimeframe),
            ExtensionRequestedBy = T(ExtensionRequestedBy), AdverseBenefitDetermination = T(AdverseBenefitDetermination),
            OtherAdverseBenefitDetermination = T(OtherAdverseBenefitDetermination), AppealOutcome = T(AppealOutcome),
            OverturnedOutcome = T(OverturnedOutcome), PriorAuthorizationNumber = T(PriorAuthorizationNumber), ComplaintType = T(ComplaintType),
            Complainant = T(Complainant), OtherComplainant = T(OtherComplainant), Mdcp = T(Mdcp), Disposition = T(Disposition),
            OtherDispositionDetail = T(OtherDispositionDetail), ComplaintCategory = T(ComplaintCategory),
            ComplaintCategoryDetail = T(ComplaintCategoryDetail), OtherComplaintCategoryDetail = T(OtherComplaintCategoryDetail),
            DisasterRelated = T(DisasterRelated), ComplaintOutcome = T(ComplaintOutcome),
        };
    }
}
