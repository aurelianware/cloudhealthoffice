using CloudHealthOffice.Infrastructure.ReferenceData.Payers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EligibilityService.Controllers;

/// <summary>
/// Read-only payer reference surface plus an optional on-demand directory sync.
/// Intended for operators and developers; it does not route transactions.
/// </summary>
[ApiController]
[Route("api/payer-references")]
public sealed class PayerReferenceController : ControllerBase
{
    private readonly IPayerReferenceService _payers;
    private readonly IPayerDirectorySynchronizer _synchronizer;
    private readonly IOptions<PayerReferenceOptions> _options;
    private readonly IHostEnvironment _environment;

    public PayerReferenceController(
        IPayerReferenceService payers,
        IPayerDirectorySynchronizer synchronizer,
        IOptions<PayerReferenceOptions> options,
        IHostEnvironment environment)
    {
        _payers = payers;
        _synchronizer = synchronizer;
        _options = options;
        _environment = environment;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PayerReference>>> Search(
        [FromQuery] string? q,
        [FromQuery] string? id,
        [FromQuery] string? externalSystem,
        [FromQuery] string? externalType,
        [FromQuery] string? externalValue,
        [FromQuery] bool? active,
        [FromQuery] int maxResults = 50,
        CancellationToken ct = default)
    {
        var results = await _payers.SearchAsync(new PayerSearchQuery
        {
            Text = q,
            Id = id,
            ExternalSystem = externalSystem,
            ExternalType = externalType,
            ExternalValue = externalValue,
            Active = active,
            MaxResults = maxResults
        }, ct);
        return Ok(results);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<PayerReference>> Get(string id, CancellationToken ct)
    {
        var payer = await _payers.GetByIdAsync(id, ct);
        return payer is null ? NotFound() : Ok(payer);
    }

    [HttpGet("{id}/transactions")]
    public async Task<ActionResult<IReadOnlyList<PayerTransactionCapability>>> Transactions(
        string id, CancellationToken ct)
    {
        var payer = await _payers.GetByIdAsync(id, ct);
        if (payer is null)
        {
            return NotFound();
        }

        return Ok(await _payers.GetSupportedTransactionsAsync(id, ct));
    }

    [HttpGet("sync/status")]
    public async Task<ActionResult<PayerDirectorySyncStatus>> SyncStatus(CancellationToken ct)
    {
        var status = await _synchronizer.GetStatusAsync(ct);
        return status is null ? NotFound() : Ok(status);
    }

    [HttpPost("sync")]
    public async Task<ActionResult<PayerDirectorySyncResult>> Sync(CancellationToken ct)
    {
        if (!_environment.IsDevelopment() && !_options.Value.Sync.AllowOnDemandSync)
        {
            return NotFound();
        }

        var result = await _synchronizer.SynchronizeAsync(ct);
        return result.Succeeded ? Ok(result) : StatusCode(StatusCodes.Status503ServiceUnavailable, result);
    }
}
