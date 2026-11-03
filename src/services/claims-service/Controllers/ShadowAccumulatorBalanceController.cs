using System.Text;
using ClaimsService.Services.Adjudication.Shadow;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace ClaimsService.Controllers;

/// <summary>
/// Starting accumulator balances from the plan's core, for shadow adjudication
/// only: what each member had met toward the deductible and out-of-pocket
/// maximum as of a date. They never reach CHO's accumulator store. See
/// <c>docs/integration/SHADOW-ADJUDICATION.md#accumulators</c>.
/// </summary>
[ApiController]
[Route("api/v1/claims/shadow-adjudication/accumulator-balances")]
[Produces("application/json")]
public class ShadowAccumulatorBalanceController : ControllerBase
{
    private readonly ShadowAccumulatorBalanceImporter _importer;
    private readonly ICurrentActor _actor;

    public ShadowAccumulatorBalanceController(ShadowAccumulatorBalanceImporter importer, ICurrentActor actor)
    {
        _importer = importer;
        _actor = actor;
    }

    private string TenantId =>
        HttpContext?.Items["TenantId"]?.ToString() is { Length: > 0 } tenant ? tenant : _actor.TenantId;

    /// <summary>
    /// Loads a balance file (CSV, header row first). Reloading is safe: a
    /// balance for the same member, date and network replaces the earlier one.
    /// </summary>
    /// <param name="file">member_id, as_of_date, network, individual_deductible_met, individual_oop_met, family_deductible_met, family_oop_met.</param>
    /// <param name="sourceSystem">The core the member ids come from, for the member crosswalk.</param>
    /// <param name="dryRun">Check and report only.</param>
    [HttpPost("import")]
    [RequirePermission(ClaimsPermissions.Import)]
    [RequestSizeLimit(100_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 100_000_000)]
    [ProducesResponseType(typeof(ShadowAccumulatorBalanceImportResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ShadowAccumulatorBalanceImportResult), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ShadowAccumulatorBalanceImportResult>> Import(
        [FromForm] IFormFile file,
        [FromForm] string? sourceSystem = null,
        [FromForm] bool dryRun = false,
        CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "A non-empty balance file is required." });

        string content;
        using (var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8))
            content = await reader.ReadToEndAsync(ct);

        var actor = string.IsNullOrWhiteSpace(_actor.UserId) ? "unknown" : _actor.UserId;
        var result = await _importer.ImportAsync(TenantId, content, sourceSystem, dryRun, actor, ct);
        return result.FileErrors.Count > 0 ? UnprocessableEntity(result) : Ok(result);
    }
}
