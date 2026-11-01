using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Security;
using CoverageService.Middleware;
using CoverageService.Models.Tpl;
using CoverageService.Repositories;
using CoverageService.Services.Tpl;
using Microsoft.AspNetCore.Mvc;

namespace CoverageService.Controllers;

/// <summary>
/// Medicaid third-party liability: load the state's TPL file and read a
/// member's TPL resources. Claims adjudication cost-avoids or pays and chases
/// against these rows. See docs/integration/MEDICAID-TPL.md.
/// </summary>
[ApiController]
[Route("api/v1/coverage")]
public sealed class TplController : ControllerBase
{
    /// <summary>A state's monthly TPL file for a large plan is tens of MB.</summary>
    private const long MaxUploadBytes = 300L * 1024 * 1024;

    private static readonly JsonSerializerOptions LayoutJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ITplImportService _importer;
    private readonly ITplPolicyRepository _policies;
    private readonly ICurrentActor _actor;
    private readonly ILogger<TplController> _logger;
    private readonly ITplDiscoveryService? _discovery;

    public TplController(
        ITplImportService importer, ITplPolicyRepository policies, ICurrentActor actor, ILogger<TplController> logger,
        ITplDiscoveryService? discovery = null)
    {
        _importer = importer;
        _policies = policies;
        _actor = actor;
        _logger = logger;
        _discovery = discovery;
    }

    /// <summary>
    /// Records coverage the plan found itself. It counts for cost avoidance
    /// at once and is queued for the state report (California: due in 10
    /// calendar days).
    /// </summary>
    [HttpPost("member/{memberId}/tpl")]
    [ProducesResponseType(typeof(TplPolicy), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RecordDiscovery(
        [FromRoute] string memberId, [FromBody] TplDiscoveryRequest? request, CancellationToken ct = default)
    {
        if (_discovery is null) return StatusCode(StatusCodes.Status501NotImplemented);
        if (request is null) return BadRequest(new { message = "Body required." });
        try
        {
            var policy = await _discovery.RecordDiscoveryAsync(HttpContext.GetTenantId(), memberId, request, _actor.UserId, ct);
            return StatusCode(StatusCodes.Status201Created, policy);
        }
        catch (TplDiscoveryException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Asks the state to remove a TPL record the plan found to be wrong. The
    /// record stays in force until the state's file drops it. A plan-found row
    /// never reported is simply withdrawn.
    /// </summary>
    [HttpPost("tpl/{policyId}/removal-request")]
    [ProducesResponseType(typeof(TplPolicy), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RequestRemoval(
        [FromRoute] string policyId, [FromBody] TplRemovalRequest? request, CancellationToken ct = default)
    {
        if (_discovery is null) return StatusCode(StatusCodes.Status501NotImplemented);
        try
        {
            var policy = await _discovery.RequestRemovalAsync(
                HttpContext.GetTenantId(), policyId, request?.Reason ?? string.Empty, request?.DiscoveredAt, _actor.UserId, ct);
            return policy is null ? NotFound() : Ok(policy);
        }
        catch (TplDiscoveryException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Additions and removal requests owed to the state, as CSV, with due dates and an overdue flag.</summary>
    [HttpGet("tpl/state-report")]
    [Produces("text/csv")]
    public async Task<IActionResult> StateReport(
        [FromQuery] string state, [FromQuery] bool includeReported = false, CancellationToken ct = default)
    {
        if (_discovery is null) return StatusCode(StatusCodes.Status501NotImplemented);
        if (string.IsNullOrWhiteSpace(state) || state.Trim().Length != 2)
            return BadRequest(new { message = "state (two letters) is required." });
        var csv = await _discovery.BuildStateReportCsvAsync(HttpContext.GetTenantId(), state.Trim(), includeReported, DateTime.UtcNow, ct);
        return File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv",
            $"tpl-state-report-{state.Trim().ToUpperInvariant()}-{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    /// <summary>Marks rows as reported to the state, with the state's batch or form reference.</summary>
    [HttpPost("tpl/state-report/mark-reported")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> MarkReported([FromBody] TplMarkReportedRequest? request, CancellationToken ct = default)
    {
        if (_discovery is null) return StatusCode(StatusCodes.Status501NotImplemented);
        if (request?.PolicyIds is not { Count: > 0 } ids) return BadRequest(new { message = "policyIds required." });
        var count = await _discovery.MarkReportedAsync(
            HttpContext.GetTenantId(), ids, request.ReportedAt ?? DateTime.UtcNow, request.Reference, _actor.UserId, ct);
        return Ok(new { marked = count });
    }

    /// <summary>The built-in layouts, their code tables and whether each has
    /// been checked against the state's record layout.</summary>
    [HttpGet("tpl/layouts")]
    [ProducesResponseType(typeof(IEnumerable<TplFileLayout>), StatusCodes.Status200OK)]
    public IActionResult GetLayouts() => Ok(TplLayouts.BuiltIn.Values);

    /// <summary>
    /// Loads a state TPL file. A dry run (the default) writes nothing and
    /// reports what would change, rows rejected with the reason, and codes
    /// whose scope still needs checking.
    /// </summary>
    /// <param name="file">The state file (delimited text or fixed width).</param>
    /// <param name="layoutFile">Optional layout JSON overriding the built-in
    /// columns or positions; the built-in code table is kept for codes it
    /// does not name.</param>
    /// <param name="layout"><c>cho-csv</c>, <c>tx-tpr</c> or <c>ca-ohc</c>.</param>
    /// <param name="state">Required for <c>cho-csv</c>; must match the layout's state otherwise.</param>
    /// <param name="fileDate">The date the state produced the file; defaults to today.</param>
    /// <param name="mode"><c>Incremental</c> (default) or <c>Full</c>.</param>
    [HttpPost("tpl/import")]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    [ProducesResponseType(typeof(TplImportReport), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(TplImportReport), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Import(
        IFormFile? file,
        IFormFile? layoutFile,
        [FromQuery] string layout = TplLayouts.ChoCsv,
        [FromQuery] string? state = null,
        [FromQuery] DateTime? fileDate = null,
        [FromQuery] TplImportMode mode = TplImportMode.Incremental,
        [FromQuery] bool dryRun = true,
        [FromQuery] bool allowLargeDrop = false,
        CancellationToken ct = default)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrWhiteSpace(tenantId))
            return BadRequest(new { message = "Tenant context missing." });
        if (file is null || file.Length == 0)
            return BadRequest(new { message = "Upload the state TPL file as 'file'." });

        var builtIn = TplLayouts.Find(layout);
        if (builtIn is null)
            return BadRequest(new { message = $"Unknown layout '{layout}'. Use one of: {string.Join(", ", TplLayouts.BuiltIn.Keys)}." });

        var effectiveLayout = builtIn;
        if (layoutFile is { Length: > 0 })
        {
            try
            {
                await using var stream = layoutFile.OpenReadStream();
                var custom = await JsonSerializer.DeserializeAsync<TplFileLayout>(stream, LayoutJson, ct);
                if (custom is null) return BadRequest(new { message = "layoutFile is empty." });
                effectiveLayout = Merge(builtIn, custom);
            }
            catch (JsonException ex)
            {
                return BadRequest(new { message = $"layoutFile is not valid layout JSON: {ex.Message}" });
            }
        }

        var resolvedState = (string.IsNullOrWhiteSpace(effectiveLayout.State) ? state : effectiveLayout.State)?.Trim().ToUpperInvariant();
        if (resolvedState is null || resolvedState.Length != 2 || !resolvedState.All(char.IsLetter))
            return BadRequest(new { message = "state (two letters) is required for this layout." });
        if (!string.IsNullOrWhiteSpace(state) && !string.Equals(state.Trim(), resolvedState, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = $"Layout '{effectiveLayout.Id}' is for {resolvedState}; state '{state}' does not match." });

        string content;
        using (var reader = new StreamReader(file.OpenReadStream(), detectEncodingFromByteOrderMarks: true))
            content = await reader.ReadToEndAsync(ct);

        var report = await _importer.ImportAsync(new TplImportRequest(
            tenantId,
            content,
            effectiveLayout,
            resolvedState,
            Path.GetFileName(file.FileName),
            fileDate ?? DateTime.UtcNow.Date,
            mode,
            dryRun,
            allowLargeDrop,
            _actor.UserId), ct);

        _logger.LogInformation("TPL {Mode} import ({Layout}, {State}, dryRun={DryRun}) by {Actor}",
            mode, LogSanitizer.Sanitize(effectiveLayout.Id), resolvedState, dryRun, LogSanitizer.Sanitize(_actor.UserId));

        return report.Error is null ? Ok(report) : UnprocessableEntity(report);
    }

    /// <summary>
    /// The member's TPL resources. With <c>asOfDate</c>, only those covering
    /// that date; otherwise every span, ended and removed included, newest first.
    /// </summary>
    [HttpGet("member/{memberId}/tpl")]
    [ProducesResponseType(typeof(IEnumerable<TplPolicy>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMemberTpl(
        [FromRoute] string memberId, [FromQuery] DateTime? asOfDate = null, CancellationToken ct = default)
    {
        var tenantId = HttpContext.GetTenantId();
        var rows = await _policies.GetByMemberAsync(tenantId, memberId, ct);
        if (asOfDate is { } date)
            rows = rows.Where(p => p.IsActiveOn(date)).ToList();
        return Ok(rows);
    }

    /// <summary>A custom layout replaces the built-in's columns or positions
    /// and format; codes it names win, the rest of the built-in table stays.
    /// It stays unverified unless it says otherwise.</summary>
    public static TplFileLayout Merge(TplFileLayout builtIn, TplFileLayout custom)
    {
        var codes = new Dictionary<string, TplCodeMapping>(builtIn.Codes, StringComparer.OrdinalIgnoreCase);
        foreach (var (code, mapping) in custom.Codes ?? new())
            codes[code] = mapping;
        var fields = new Dictionary<TplField, TplFieldSpec>(builtIn.Fields);
        foreach (var (field, spec) in custom.Fields ?? new())
            fields[field] = spec;
        // Switching to fixed width drops the built-in header aliases: a
        // positional file has no header to find them in.
        if (custom.Format == TplFileFormat.FixedWidth)
            fields = (custom.Fields ?? new()).ToDictionary(kv => kv.Key, kv => kv.Value);

        return new TplFileLayout
        {
            Id = builtIn.Id,
            State = builtIn.State,
            Description = string.IsNullOrWhiteSpace(custom.Description) ? builtIn.Description : custom.Description,
            Format = custom.Format,
            Delimiter = string.IsNullOrEmpty(custom.Delimiter) ? builtIn.Delimiter : custom.Delimiter,
            Fields = fields,
            Codes = codes,
            DateFormats = custom.DateFormats is { Count: > 0 } ? custom.DateFormats : builtIn.DateFormats,
            OpenEndDates = custom.OpenEndDates is { Count: > 0 } ? custom.OpenEndDates : builtIn.OpenEndDates,
            Verified = custom.Verified,
            VerificationNote = string.IsNullOrWhiteSpace(custom.VerificationNote)
                ? "Custom layout uploaded with the file. " + builtIn.VerificationNote
                : custom.VerificationNote,
            References = builtIn.References.Concat(custom.References ?? new()).ToList(),
        };
    }
}

public sealed record TplRemovalRequest(string? Reason, DateTime? DiscoveredAt);

public sealed record TplMarkReportedRequest(IReadOnlyList<string>? PolicyIds, DateTime? ReportedAt, string? Reference);
