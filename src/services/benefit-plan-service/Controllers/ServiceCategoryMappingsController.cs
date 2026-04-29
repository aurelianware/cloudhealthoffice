using BenefitPlanService.HostedServices;
using BenefitPlanService.Middleware;
using BenefitPlanService.Models;
using BenefitPlanService.Repositories;
using CloudHealthOffice.BenefitEngine.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BenefitPlanService.Controllers;

/// <summary>
/// Admin write API for service-category mappings (capability BP 5.6 —
/// Service Category Mapping). Exposes CRUD over
/// <see cref="ServiceCategoryMapping"/> documents and a one-shot seed
/// trigger that stamps the curated <c>system-defaults.json</c> bundle onto
/// a tenant.
///
/// <para>
/// <b>Authorization model.</b> Read endpoints are open (consumers include
/// the future portal mapping editor and operational diagnostics). Write
/// endpoints sit behind the
/// <see cref="ServiceCategoryMappingOptions.AdminWriteEnabled"/>
/// defence-in-depth gate. The deployment layer (NetworkPolicy / gateway
/// ACL) is the load-bearing control. When the flag is false the controller
/// returns 503; when the flag is true the route accepts writes from any
/// caller permitted by the gateway. Claim-based authorization is a
/// service-wide initiative that has not landed in this service yet
/// (see <c>docs/architecture/service-category-mapping.md</c>).
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/service-category-mappings")]
public sealed class ServiceCategoryMappingsController : ControllerBase
{
    private readonly IServiceCategoryMappingWriteRepository _writeRepo;
    private readonly SystemDefaultMappingSeeder _seeder;
    private readonly IOptionsMonitor<ServiceCategoryMappingOptions> _options;
    private readonly ILogger<ServiceCategoryMappingsController> _logger;

    public ServiceCategoryMappingsController(
        IServiceCategoryMappingWriteRepository writeRepo,
        SystemDefaultMappingSeeder seeder,
        IOptionsMonitor<ServiceCategoryMappingOptions> options,
        ILogger<ServiceCategoryMappingsController> logger)
    {
        _writeRepo = writeRepo;
        _seeder = seeder;
        _options = options;
        _logger = logger;
    }

    // ── Reads (always available) ────────────────────────────────────────────

    /// <summary>
    /// List mappings for the tenant on the request. When
    /// <paramref name="planId"/> is null returns tenant-default mappings
    /// (the inheritance base); when supplied returns plan-specific
    /// overrides for that plan.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ServiceCategoryMappingResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<ServiceCategoryMappingResponse>>> List(
        [FromQuery] Guid? planId,
        CancellationToken ct)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return BadRequest(new { error = "tenant_required", message = "X-Tenant-ID header is required." });
        }

        var rows = await _writeRepo.ListAsync(tenantId, planId, ct);
        return Ok(rows.Select(ServiceCategoryMappingResponse.From).ToList());
    }

    /// <summary>
    /// Fetch a single mapping by id. Returns 404 when the row does not
    /// exist or belongs to a different tenant.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ServiceCategoryMappingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ServiceCategoryMappingResponse>> GetById(Guid id, CancellationToken ct)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return BadRequest(new { error = "tenant_required" });
        }

        var existing = await _writeRepo.GetByIdAsync(tenantId, id, ct);
        if (existing is null) return NotFound();
        return Ok(ServiceCategoryMappingResponse.From(existing));
    }

    // ── Writes (gated by AdminWriteEnabled) ─────────────────────────────────

    /// <summary>
    /// Create a new mapping. Use a null <c>planId</c> to author a
    /// tenant-default; use a populated <c>planId</c> to author a
    /// plan-specific override.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(ServiceCategoryMappingResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ServiceCategoryMappingResponse>> Create(
        [FromBody] ServiceCategoryMappingRequest request,
        CancellationToken ct)
    {
        if (!_options.CurrentValue.AdminWriteEnabled)
        {
            return AdminGateClosed("Write endpoints are gated by ServiceCategoryMapping:AdminWriteEnabled.");
        }

        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return BadRequest(new { error = "tenant_required" });
        }

        var validation = ValidateRequest(request);
        if (validation is not null) return validation;

        var mapping = request.ToEntity(tenantId);
        mapping.Id = Guid.NewGuid();
        var created = await _writeRepo.CreateAsync(mapping, ct);

        _logger.LogInformation(
            "service-category mapping created tenant={Tenant} planId={Plan} serviceTypeCode={Code} id={Id}",
            Sanitize(tenantId),
            mapping.BenefitPlanId?.ToString() ?? "tenant-default",
            Sanitize(mapping.ServiceTypeCode),
            created.Id);

        return CreatedAtAction(
            nameof(GetById),
            new { id = created.Id },
            ServiceCategoryMappingResponse.From(created));
    }

    /// <summary>
    /// Replace an existing mapping by id. The id in the path takes
    /// precedence over any id on the body.
    /// </summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(ServiceCategoryMappingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ServiceCategoryMappingResponse>> Update(
        Guid id,
        [FromBody] ServiceCategoryMappingRequest request,
        CancellationToken ct)
    {
        if (!_options.CurrentValue.AdminWriteEnabled)
        {
            return AdminGateClosed("Write endpoints are gated by ServiceCategoryMapping:AdminWriteEnabled.");
        }

        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return BadRequest(new { error = "tenant_required" });
        }

        var validation = ValidateRequest(request);
        if (validation is not null) return validation;

        var mapping = request.ToEntity(tenantId);
        mapping.Id = id;

        try
        {
            var updated = await _writeRepo.UpdateAsync(mapping, ct);
            _logger.LogInformation(
                "service-category mapping updated tenant={Tenant} id={Id}",
                Sanitize(tenantId), updated.Id);
            return Ok(ServiceCategoryMappingResponse.From(updated));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>Delete a mapping by id.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!_options.CurrentValue.AdminWriteEnabled)
        {
            return AdminGateClosed("Write endpoints are gated by ServiceCategoryMapping:AdminWriteEnabled.");
        }

        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return BadRequest(new { error = "tenant_required" });
        }

        var deleted = await _writeRepo.DeleteAsync(tenantId, id, ct);
        if (!deleted) return NotFound();
        _logger.LogInformation(
            "service-category mapping deleted tenant={Tenant} id={Id}",
            Sanitize(tenantId), id);
        return NoContent();
    }

    // ── Seed admin (gated by AdminWriteEnabled) ─────────────────────────────

    /// <summary>
    /// Apply the curated system-default mapping bundle to the tenant on
    /// the request. Idempotent — repeated calls at the same bundle
    /// version return zero-mappings-written.
    /// </summary>
    [HttpPost("seed-system-defaults")]
    [ProducesResponseType(typeof(SeedTenantResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<SeedTenantResponse>> SeedSystemDefaults(CancellationToken ct)
    {
        if (!_options.CurrentValue.AdminWriteEnabled)
        {
            return AdminGateClosed("Seed-system-defaults is gated by ServiceCategoryMapping:AdminWriteEnabled.");
        }

        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return BadRequest(new { error = "tenant_required" });
        }

        if (_seeder.LoadedBundle is null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new
                {
                    error = "seed_bundle_unavailable",
                    message = "System-default seed bundle failed to load at startup. " +
                              "Check service logs for the bundle parse error and restart " +
                              "the service after fixing the bundle.",
                });
        }

        var written = await _seeder.EnsureTenantSeededAsync(tenantId, ct);
        return Ok(new SeedTenantResponse
        {
            TenantId = tenantId,
            BundleVersion = _seeder.LoadedBundle.Version,
            MappingsWritten = written,
        });
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private ActionResult AdminGateClosed(string message)
    {
        return StatusCode(
            StatusCodes.Status503ServiceUnavailable,
            new
            {
                error = "admin_write_disabled",
                message = message + " Set the flag to true in configuration AND restrict " +
                          "the route at the deployment layer (NetworkPolicy / gateway ACL).",
            });
    }

    private ActionResult? ValidateRequest(ServiceCategoryMappingRequest request)
    {
        if (request is null)
        {
            return BadRequest(new { error = "body_required" });
        }
        if (string.IsNullOrWhiteSpace(request.ServiceTypeCode))
        {
            return BadRequest(new { error = "service_type_code_required" });
        }
        if (string.IsNullOrWhiteSpace(request.ServiceTypeDescription))
        {
            return BadRequest(new { error = "service_type_description_required" });
        }
        if (request.Rules is null || request.Rules.Count == 0)
        {
            return BadRequest(new { error = "at_least_one_rule_required" });
        }
        var max = _options.CurrentValue.MaxRulesPerMapping;
        if (max > 0 && request.Rules.Count > max)
        {
            return BadRequest(new
            {
                error = "too_many_rules",
                message = $"Rules count {request.Rules.Count} exceeds MaxRulesPerMapping={max}.",
            });
        }
        foreach (var r in request.Rules)
        {
            if (string.IsNullOrWhiteSpace(r.CodePattern))
            {
                return BadRequest(new { error = "rule_code_pattern_required" });
            }
            if (string.IsNullOrWhiteSpace(r.CodeType))
            {
                return BadRequest(new { error = "rule_code_type_required" });
            }
        }
        // Capability BP 5.10 — reject impossible effective windows at
        // the producer boundary so the resolver doesn't have to silently
        // filter them out at adjudication time.
        if (request.EffectiveStart is { } start && request.EffectiveEnd is { } end && end < start)
        {
            return BadRequest(new
            {
                error = "effective_window_invalid",
                message = "effectiveEnd must be on or after effectiveStart",
            });
        }
        return null;
    }

    private static string Sanitize(string value)
        => value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}

/// <summary>Request body for create/update.</summary>
public sealed class ServiceCategoryMappingRequest
{
    public Guid? PlanId { get; set; }
    public string ServiceTypeCode { get; set; } = default!;
    public string ServiceTypeDescription { get; set; } = default!;
    public List<ProcedureCodeRuleDto> Rules { get; set; } = [];
    public DateOnly? EffectiveStart { get; set; }
    public DateOnly? EffectiveEnd { get; set; }
    public bool? IsActive { get; set; }

    public ServiceCategoryMapping ToEntity(string tenantId) => new()
    {
        TenantId = tenantId,
        BenefitPlanId = PlanId,
        ServiceTypeCode = ServiceTypeCode,
        ServiceTypeDescription = ServiceTypeDescription,
        Rules = Rules.Select(r => new ProcedureCodeRule
        {
            Id = Guid.NewGuid(),
            Priority = r.Priority,
            CodeType = r.CodeType,
            CodePattern = r.CodePattern,
            CodeRangeEnd = r.CodeRangeEnd,
            PlaceOfServiceCode = r.PlaceOfServiceCode,
            RequiredModifier = r.RequiredModifier,
            RevenueCode = r.RevenueCode,
        }).ToList(),
        EffectiveStart = EffectiveStart,
        EffectiveEnd = EffectiveEnd,
        IsActive = IsActive ?? true,
    };
}

public sealed class ProcedureCodeRuleDto
{
    public int Priority { get; set; }
    public string CodeType { get; set; } = "CPT";
    public string CodePattern { get; set; } = default!;
    public string? CodeRangeEnd { get; set; }
    public string? PlaceOfServiceCode { get; set; }
    public string? RequiredModifier { get; set; }
    public string? RevenueCode { get; set; }
}

/// <summary>Response body — same shape as the entity, exposed flat for clients.</summary>
public sealed class ServiceCategoryMappingResponse
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = default!;
    public Guid? PlanId { get; set; }
    public string ServiceTypeCode { get; set; } = default!;
    public string ServiceTypeDescription { get; set; } = default!;
    public List<ProcedureCodeRuleDto> Rules { get; set; } = [];
    public DateOnly? EffectiveStart { get; set; }
    public DateOnly? EffectiveEnd { get; set; }
    public bool IsActive { get; set; }

    public static ServiceCategoryMappingResponse From(ServiceCategoryMapping m) => new()
    {
        Id = m.Id,
        TenantId = m.TenantId,
        PlanId = m.BenefitPlanId,
        ServiceTypeCode = m.ServiceTypeCode,
        ServiceTypeDescription = m.ServiceTypeDescription,
        Rules = m.Rules.Select(r => new ProcedureCodeRuleDto
        {
            Priority = r.Priority,
            CodeType = r.CodeType,
            CodePattern = r.CodePattern,
            CodeRangeEnd = r.CodeRangeEnd,
            PlaceOfServiceCode = r.PlaceOfServiceCode,
            RequiredModifier = r.RequiredModifier,
            RevenueCode = r.RevenueCode,
        }).ToList(),
        EffectiveStart = m.EffectiveStart,
        EffectiveEnd = m.EffectiveEnd,
        IsActive = m.IsActive,
    };
}

public sealed class SeedTenantResponse
{
    public string TenantId { get; set; } = default!;
    public int BundleVersion { get; set; }
    public int MappingsWritten { get; set; }
}
