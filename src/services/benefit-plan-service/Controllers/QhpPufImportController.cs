using BenefitPlanService.Import.QhpPuf;
using BenefitPlanService.Middleware;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace BenefitPlanService.Controllers;

/// <summary>
/// Seeds benefit plans from the CMS Exchange QHP Public Use Files
/// (https://www.cms.gov/marketplace/resources/data/public-use-files): the
/// Plan Attributes PUF and the Benefits and Cost Sharing PUF, uploaded as the
/// .zip CMS publishes or the CSV inside it. A dry run (the default) returns
/// the mapped plans and a per-plan gap report; a real run creates Draft plan
/// versions that a plan administrator reviews and publishes. Writes use the
/// service's default write permission (settings:manage).
/// See docs/integration/QHP-PUF-BENEFIT-PLAN-IMPORTER.md.
/// </summary>
[ApiController]
[Route("api/v1/plans/import/qhp-puf")]
public sealed class QhpPufImportController : ControllerBase
{
    /// <summary>The national Benefits and Cost Sharing CSV is several hundred MB.</summary>
    private const long MaxUploadBytes = 1L * 1024 * 1024 * 1024;

    private readonly IQhpPufImportService _importer;
    private readonly ICurrentActor _actor;

    public QhpPufImportController(IQhpPufImportService importer, ICurrentActor actor)
    {
        _importer = importer;
        _actor = actor;
    }

    [HttpPost]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    [ProducesResponseType(typeof(QhpPufImportReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<QhpPufImportReport>> Import(
        IFormFile? planAttributes,
        IFormFile? benefitsCostSharing,
        [FromQuery] string? state = null,
        [FromQuery] string? issuerId = null,
        [FromQuery] string? planIds = null,
        [FromQuery] string? csrVariants = null,
        [FromQuery] int? businessYear = null,
        [FromQuery] int maxPlans = 200,
        [FromQuery] bool dryRun = true,
        [FromQuery] bool includePlans = false,
        CancellationToken ct = default)
    {
        if (planAttributes is null || benefitsCostSharing is null)
            return BadRequest(new { message = "Upload both files: planAttributes (Plan Attributes PUF) and benefitsCostSharing (Benefits and Cost Sharing PUF)." });

        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrWhiteSpace(tenantId))
            return BadRequest(new { message = "Tenant context missing." });

        var options = new QhpPufImportOptions
        {
            StateCode = state,
            IssuerId = issuerId,
            PlanIds = Split(planIds, StringComparer.OrdinalIgnoreCase),
            CsrVariants = Split(csrVariants, StringComparer.Ordinal),
            BusinessYear = businessYear,
            MaxPlans = maxPlans,
            DryRun = dryRun,
            IncludePlans = includePlans,
        };
        if (!options.HasSelector)
            return BadRequest(new { message = "Choose plans with state, issuerId or planIds; the national files are too large to import whole." });

        try
        {
            await using var attrs = QhpPufFileReader.OpenCsv(planAttributes.OpenReadStream(), planAttributes.FileName);
            await using var benefits = QhpPufFileReader.OpenCsv(benefitsCostSharing.OpenReadStream(), benefitsCostSharing.FileName);
            var report = await _importer.ImportAsync(attrs, benefits, options, tenantId, _actor.UserId, ct);
            return Ok(report);
        }
        catch (QhpPufFormatException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (System.IO.InvalidDataException ex)
        {
            return BadRequest(new { message = $"Could not read the upload: {ex.Message}" });
        }
    }

    private static HashSet<string> Split(string? csv, StringComparer comparer) =>
        string.IsNullOrWhiteSpace(csv)
            ? new HashSet<string>(comparer)
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(comparer);
}
