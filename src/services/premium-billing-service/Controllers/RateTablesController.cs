using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;
using PremiumBillingService.Services;

namespace PremiumBillingService.Controllers;

/// <summary>
/// Premium rate tables, stored as immutable versions. Reads need billing:read;
/// saving or withdrawing a table changes what every later invoice charges, so
/// it needs finance:write. Tenant and actor come from the token.
/// </summary>
[ApiController]
[Route("api/v1/rate-tables")]
[Produces("application/json")]
public class RateTablesController : ControllerBase
{
    private readonly IRateTableService _rateTables;
    private readonly ICurrentActor _actor;

    public RateTablesController(IRateTableService rateTables, ICurrentActor actor)
    {
        _rateTables = rateTables;
        _actor = actor;
    }

    /// <summary>The current version of every rate table (withdrawn tables left out).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<RateTableRecord>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<RateTableRecord>>> ListCurrent() => Ok(await _rateTables.ListCurrentAsync());

    /// <summary>Every version of a rate table, oldest first.</summary>
    [HttpGet("{rateTableId}/versions")]
    [ProducesResponseType(typeof(IEnumerable<RateTableRecord>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<RateTableRecord>>> ListVersions(string rateTableId) =>
        Ok(await _rateTables.ListVersionsAsync(rateTableId));

    /// <summary>One version: the rates an invoice line recording (rateTableId, version) was billed with.</summary>
    [HttpGet("{rateTableId}/versions/{version:int}")]
    [ProducesResponseType(typeof(RateTableRecord), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RateTableRecord>> GetVersion(string rateTableId, int version)
    {
        var record = await _rateTables.GetVersionAsync(rateTableId, version);
        return record == null ? NotFound(new { error = $"Rate table {rateTableId} has no version {version}" }) : Ok(record);
    }

    /// <summary>
    /// Create a rate table (ExpectedCurrentVersion null) or its next version
    /// (ExpectedCurrentVersion = the latest version you read). 409 when
    /// someone saved a version in between.
    /// </summary>
    [HttpPost]
    [RequirePermission("finance:write")]
    [ProducesResponseType(typeof(RateTableRecord), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RateTableRecord>> Save([FromBody] SaveRateTableRequest request)
    {
        try
        {
            var record = await _rateTables.SaveAsync(request, _actor.UserId);
            return CreatedAtAction(nameof(GetVersion), new { rateTableId = record.RateTableId, version = record.Version }, record);
        }
        catch (RateTableRejectedException ex)
        {
            return BadRequest(new { error = ex.Message, errors = ex.Errors });
        }
        catch (RateTableVersionConflictException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>Withdraw a rate table: a new version that no longer rates anything.</summary>
    [HttpPost("{rateTableId}/withdraw")]
    [RequirePermission("finance:write")]
    [ProducesResponseType(typeof(RateTableRecord), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RateTableRecord>> Withdraw(string rateTableId, [FromBody] WithdrawRateTableRequest request)
    {
        try
        {
            return Ok(await _rateTables.WithdrawAsync(rateTableId, request, _actor.UserId));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (RateTableRejectedException ex)
        {
            return BadRequest(new { error = ex.Message, errors = ex.Errors });
        }
        catch (RateTableVersionConflictException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }
}
