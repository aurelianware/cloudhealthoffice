using BenefitPlanService.Import.FeeSchedules;
using BenefitPlanService.Import.PriorAuthRules;
using BenefitPlanService.Services;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.PlanConfiguration;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.PriorAuthRuleEngine.Persistence;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.SeedRules;
using CloudHealthOffice.PriorAuthRuleEngine.Services;
using Microsoft.AspNetCore.Mvc;

namespace BenefitPlanService.Controllers;

[ApiController]
[Route("api/v1/prior-auth-rules")]
public sealed class PriorAuthRulesController : ControllerBase
{
    private readonly IPaRuleRepository _repository;
    private readonly ICurrentActor _actor;
    private readonly ILogger<PriorAuthRulesController> _logger;

    private readonly IPriorAuthRuleImportService? _importer;

    /// <summary>A plan's PA list is a few thousand rows; 20 MB leaves room for long description columns.</summary>
    private const long MaxImportBytes = 20L * 1024 * 1024;

    public PriorAuthRulesController(
        IPaRuleRepository repository,
        ICurrentActor actor,
        ILogger<PriorAuthRulesController> logger,
        IPriorAuthRuleImportService? importer = null)
    {
        _repository = repository;
        _actor = actor;
        _logger = logger;
        _importer = importer;
    }

    /// <summary>
    /// Loads the plan's own list of items and services that need prior
    /// authorization (CSV, or a .zip holding one) as this tenant's rule set for
    /// one state, line of business and (optionally) program. CRD, PAS
    /// <c>$submit</c> and the published required-services list all read that
    /// rule set. Accepts the CSV <c>required-services?format=csv</c> returns,
    /// and typical payer PA lists (a code column, optional category, place of
    /// service, diagnosis and limit columns). The file is the whole list for
    /// its scope: an earlier import's rules it no longer has are disabled.
    /// Dry run by default. Tenant configuration: <c>settings:manage</c>.
    /// See docs/integration/PRIOR-AUTH-RULES-IMPORTER.md.
    /// </summary>
    [HttpPost("import/csv")]
    [RequirePermission("settings:manage")]
    [RequestSizeLimit(MaxImportBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxImportBytes)]
    [ProducesResponseType(typeof(PriorAuthRuleImportReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ImportCsv(
        IFormFile? file,
        [FromQuery] string? stateCode,
        [FromQuery] PaLineOfBusiness lob,
        [FromQuery] string? program = null,
        [FromQuery] bool keepExemptions = true,
        [FromQuery] bool dryRun = true,
        [FromQuery] bool includeCodes = false,
        CancellationToken ct = default)
    {
        if (_importer is null)
            return StatusCode(StatusCodes.Status501NotImplemented, new { error = "The prior-auth list importer is not registered." });
        if (string.IsNullOrWhiteSpace(_actor.TenantId))
            return BadRequest(new { error = "Tenant context missing." });

        var errors = new List<string>();
        if (file is null || file.Length == 0) errors.Add("Upload the list as 'file' (CSV, or a .zip holding one).");
        if (string.IsNullOrWhiteSpace(stateCode) || stateCode.Trim().Length != 2 || !stateCode.Trim().All(char.IsAsciiLetter))
            errors.Add("Supply a two-letter state code.");
        if (!Enum.IsDefined(lob)) errors.Add("Supply a line of business.");
        if (program is not null && (program.Trim().Length is 0 or > 32 || !program.Trim().All(char.IsAsciiLetterOrDigit)))
            errors.Add("A program code is letters and digits only.");
        if (errors.Count > 0) return BadRequest(new { error = "Invalid prior-auth list import.", details = errors });

        var options = new PriorAuthRuleImportOptions
        {
            StateCode = stateCode!.Trim().ToUpperInvariant(),
            LineOfBusiness = lob,
            Program = string.IsNullOrWhiteSpace(program) ? null : program.Trim(),
            SourceName = Path.GetFileName(file!.FileName),
            KeepExemptions = keepExemptions,
            DryRun = dryRun,
            IncludeCodes = includeCodes,
        };

        try
        {
            await using var csv = FeeScheduleCsvReader.OpenCsv(file.OpenReadStream(), file.FileName);
            return Ok(await _importer.ImportAsync(csv, options, _actor.TenantId, _actor.UserId, ct));
        }
        catch (PriorAuthRuleFormatException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (FeeScheduleFormatException ex)
        {
            return BadRequest(new { error = ex.Message.Replace("fee schedule", "prior-auth list", StringComparison.Ordinal) });
        }
        catch (InvalidDataException ex)
        {
            return BadRequest(new { error = $"Could not read the upload: {ex.Message}" });
        }
    }

    /// <summary>
    /// The PA-required list for one benefit plan: the rule set its program
    /// (state, line of business, program code) resolves to, the same one CRD
    /// and PAS evaluate a member of the plan against. Claims adjudication reads
    /// it to decide which lines need an authorization, so a claim edit and a
    /// CRD answer come from one source.
    /// <para>
    /// <c>resolved: false</c> (200) when the plan has no program, or its
    /// program is unknown or names a line of business the rule engine has no
    /// rule sets for: there is no rule set to read, which is an answer, not an
    /// outage. 404 when there is no such plan; 503 when tenant-service could
    /// not be asked. <c>includeDrugs=true</c> keeps HCPCS J codes on the
    /// entries (claims); the public list leaves them out.
    /// </para>
    /// </summary>
    [HttpGet("plans/{planId}/required-services")]
    [RequirePermission("benefits:read")]
    [ProducesResponseType(typeof(PlanPaRequiredServices), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> PlanRequiredServices(string planId, [FromQuery] bool includeDrugs,
        // Per action, not in the constructor: the plan service's dependencies
        // (ACA limits file etc.) must not be built for the controller's other actions.
        [FromServices] IBenefitPlanService plans, [FromServices] IProgramDirectory? programs, CancellationToken ct)
    {
        var tenantId = _actor.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId)) return BadRequest(new { error = "Tenant context missing." });

        var plan = await plans.GetPlanAsync(planId, tenantId);
        if (plan is null) return NotFound(new { error = "No such plan." });

        if (string.IsNullOrWhiteSpace(plan.ProgramId))
            return Ok(PlanPaRequiredServices.Unresolved(planId, null,
                "The plan has no program, so its state and program are not stated."));
        if (programs is null)
            return Ok(PlanPaRequiredServices.Unresolved(planId, plan.ProgramId, "No program directory is configured."));

        ProgramDescriptor? program;
        try
        {
            program = await programs.GetAsync(tenantId, plan.ProgramId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Program lookup failed for a plan's PA rule set");
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "The plan's program could not be read from tenant-service." });
        }
        if (program is null)
            return Ok(PlanPaRequiredServices.Unresolved(planId, plan.ProgramId, "The plan's program was not found."));
        if (!Enum.TryParse<PaLineOfBusiness>(program.LineOfBusiness, ignoreCase: true, out var lob) || !Enum.IsDefined(lob))
            return Ok(PlanPaRequiredServices.Unresolved(planId, plan.ProgramId,
                $"The program's line of business ({program.LineOfBusiness}) has no PA rule sets."));

        var (key, rules) = await PaRuleSetResolver.ResolveAsync(_repository, program.StateCode, lob, program.Code,
            tenantId, program.Id, ct);
        return Ok(new PlanPaRequiredServices
        {
            PlanId = planId,
            ProgramId = program.Id,
            Resolved = true,
            List = PaRequiredServicesList.Build(program.StateCode, lob, program.Code, key, rules, DateTime.UtcNow, includeDrugs),
        });
    }

    /// <summary>
    /// CMS-0057-F: the list of items and services, excluding drugs, that require
    /// prior authorization for one state, line of business and (optionally)
    /// program, built from the rule set this tenant's requests are evaluated
    /// against. <c>format=csv</c> returns one row per entry for posting as a file.
    /// The list is a draft for the plan's publication review: exemptions,
    /// excluded drug codes and rules it cannot describe are returned so a
    /// reviewer sees everything that shapes it.
    /// </summary>
    [HttpGet("required-services")]
    [RequirePermission("benefits:read")]
    [ProducesResponseType(typeof(PaRequiredServicesList), StatusCodes.Status200OK)]
    public async Task<IActionResult> RequiredServices([FromQuery] string stateCode, [FromQuery] PaLineOfBusiness lob,
        [FromQuery] string? program, [FromQuery] string? format, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(stateCode) || stateCode.Trim().Length != 2 || !stateCode.Trim().All(char.IsAsciiLetter))
            return BadRequest(new { error = "Supply a two-letter state code." });
        if (!Enum.IsDefined(lob))
            return BadRequest(new { error = "Supply a line of business." });
        if (program is not null && (program.Trim().Length is 0 or > 32 || !program.Trim().All(char.IsAsciiLetterOrDigit)))
            return BadRequest(new { error = "A program code is letters and digits only." });
        if (format is not (null or "json" or "csv"))
            return BadRequest(new { error = "format is json or csv." });
        var state = stateCode.Trim().ToUpperInvariant();
        program = string.IsNullOrWhiteSpace(program) ? null : program.Trim();

        var (key, rules) = await PaRuleSetResolver.ResolveAsync(_repository, state, lob, program, _actor.TenantId,
            programId: null, ct);
        var list = PaRequiredServicesList.Build(state, lob, program, key, rules, DateTime.UtcNow);
        return format == "csv"
            ? File(System.Text.Encoding.UTF8.GetBytes(list.ToCsv()), "text/csv",
                $"prior-auth-required-services-{state}-{lob}{(program is null ? "" : "-" + program)}.csv".ToLowerInvariant())
            : Ok(list);
    }

    /// <summary>
    /// Idempotently seeds platform prior-auth rules used by local validation.
    ///
    /// Platform rules are shared: every tenant's PA decisions fall back to
    /// them. Writing them is therefore a platform act and requires the
    /// existing <c>platform:admin</c> permission (PlatformAdmin only; the
    /// reserved <c>platform</c> resource is not covered by <c>*:*</c>).
    /// Without it the service default (<c>settings:manage</c>) applied, which
    /// every TenantAdmin holds (T-5).
    /// </summary>
    [HttpPost("seed-platform")]
    [RequirePermission("platform:admin")]
    [ProducesResponseType(typeof(PriorAuthSeedResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PriorAuthSeedResponse>> SeedPlatformRules(CancellationToken ct)
    {
        var platformRules = TxMedicaidSeedRules.GetAll();
        var existingRuleIdsByState = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var rulesToWrite = new List<PaRuleDocument>();

        foreach (var rule in platformRules)
        {
            if (!existingRuleIdsByState.TryGetValue(rule.StateCode, out var existingRuleIds))
            {
                var existing = await _repository.ListAsync(tenantId: null, stateCode: rule.StateCode, ct);
                existingRuleIds = new HashSet<string>(
                    existing
                        .Where(r => r.TenantId is null)
                        .Select(r => r.RuleId),
                    StringComparer.OrdinalIgnoreCase);
                existingRuleIdsByState[rule.StateCode] = existingRuleIds;
            }

            if (existingRuleIds.Add(rule.RuleId))
            {
                rulesToWrite.Add(rule);
            }
        }

        if (rulesToWrite.Count > 0)
        {
            await _repository.BulkUpsertAsync(rulesToWrite, ct);
            _logger.LogInformation("Seeded {Count} platform prior-auth rules", rulesToWrite.Count);
        }

        return Ok(new PriorAuthSeedResponse(platformRules.Count, rulesToWrite.Count));
    }
}

public sealed record PriorAuthSeedResponse(int TotalPlatformRules, int SeededRules);

/// <summary>A plan's PA-required list, or why there is none (<see cref="Resolved"/> false).</summary>
public sealed record PlanPaRequiredServices
{
    public required string PlanId { get; init; }
    public string? ProgramId { get; init; }
    public required bool Resolved { get; init; }
    public string? Reason { get; init; }
    public PaRequiredServicesList? List { get; init; }

    public static PlanPaRequiredServices Unresolved(string planId, string? programId, string reason) =>
        new() { PlanId = planId, ProgramId = programId, Resolved = false, Reason = reason };
}
