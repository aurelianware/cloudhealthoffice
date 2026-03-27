using Microsoft.AspNetCore.Mvc;
using ProviderContractsService.Models;
using ProviderContractsService.Repositories;

namespace ProviderContractsService.Controllers;

[ApiController]
[Route("api/v1/contracts")]
[Produces("application/json")]
public class ProviderContractsController : ControllerBase
{
    private readonly IProviderContractRepository _contractRepository;
    private readonly ILogger<ProviderContractsController> _logger;

    public ProviderContractsController(
        IProviderContractRepository contractRepository,
        ILogger<ProviderContractsController> logger)
    {
        _contractRepository = contractRepository;
        _logger = logger;
    }

    /// <summary>
    /// Search provider contracts with optional filters.
    /// IMPORTANT: ProviderTin is masked to last 4 digits in list responses.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<ProviderContract>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<ProviderContract>>> SearchContracts(
        [FromQuery] string? npi = null,
        [FromQuery] LineOfBusiness? lob = null,
        [FromQuery] ProviderContractStatus? status = null,
        [FromQuery] PaymentMethodology? paymentMethodology = null,
        [FromQuery] NetworkParticipationStatus? networkStatus = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var results = await _contractRepository.SearchAsync(npi, lob, status, paymentMethodology, networkStatus, page, pageSize);

        // Mask TIN in list responses — full TIN only via GET /{id}
        var masked = results.Select(c =>
        {
            c.ProviderTin = MaskTin(c.ProviderTin);
            return c;
        });

        return Ok(masked);
    }

    /// <summary>
    /// Get provider contract by ID (includes full ProviderTin)
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ProviderContract), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProviderContract>> GetContractById(string id)
    {
        var contract = await _contractRepository.GetByIdAsync(id);
        if (contract == null)
            return NotFound(new { error = $"Contract {id} not found" });
        return Ok(contract);
    }

    /// <summary>
    /// Get provider contract by contract number
    /// </summary>
    [HttpGet("number/{number}")]
    [ProducesResponseType(typeof(ProviderContract), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProviderContract>> GetContractByNumber(string number)
    {
        var contract = await _contractRepository.GetByContractNumberAsync(number);
        if (contract == null)
            return NotFound(new { error = $"Contract number {number} not found" });
        return Ok(contract);
    }

    /// <summary>
    /// Create a new provider contract. ContractNumber is auto-generated: CTR-{NPI}-{Year}
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(ProviderContract), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ProviderContract>> CreateContract([FromBody] ProviderContract contract)
    {
        if (contract.TerminationDate.HasValue && contract.TerminationDate.Value < contract.EffectiveDate)
            return BadRequest(new { error = "Termination date cannot be before effective date" });

        // Auto-generate contract number if not provided
        if (string.IsNullOrEmpty(contract.ContractNumber))
            contract.ContractNumber = $"CTR-{contract.ProviderNPI}-{DateTime.UtcNow.Year}";

        contract.Status = ProviderContractStatus.Draft;
        _logger.LogInformation("Creating provider contract {ContractNumber} for provider {NPI}",
            SanitizeForLog(contract.ContractNumber), SanitizeForLog(contract.ProviderNPI));

        var created = await _contractRepository.CreateAsync(contract);
        return CreatedAtAction(nameof(GetContractById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Update a provider contract
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(ProviderContract), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProviderContract>> UpdateContract(string id, [FromBody] ProviderContract contract)
    {
        var existing = await _contractRepository.GetByIdAsync(id);
        if (existing == null)
            return NotFound(new { error = $"Contract {id} not found" });

        contract.Id = id;
        contract.TenantId = existing.TenantId;
        contract.CreatedAt = existing.CreatedAt;
        contract.CreatedBy = existing.CreatedBy;

        var updated = await _contractRepository.UpdateAsync(contract);
        return Ok(updated);
    }

    /// <summary>
    /// Activate a draft contract (Draft → Active)
    /// </summary>
    [HttpPut("{id}/activate")]
    [ProducesResponseType(typeof(ProviderContract), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ProviderContract>> ActivateContract(string id)
    {
        var contract = await _contractRepository.GetByIdAsync(id);
        if (contract == null)
            return NotFound(new { error = $"Contract {id} not found" });

        if (contract.Status != ProviderContractStatus.Draft && contract.Status != ProviderContractStatus.Suspended)
            return BadRequest(new { error = $"Can only activate Draft or Suspended contracts, current: {contract.Status}" });

        contract.Status = ProviderContractStatus.Active;
        _logger.LogInformation("Activated provider contract {ContractNumber}", SanitizeForLog(contract.ContractNumber));

        var updated = await _contractRepository.UpdateAsync(contract);
        return Ok(updated);
    }

    /// <summary>
    /// Suspend an active contract (Active → Suspended)
    /// </summary>
    [HttpPut("{id}/suspend")]
    [ProducesResponseType(typeof(ProviderContract), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ProviderContract>> SuspendContract(string id, [FromBody] SuspendContractRequest request)
    {
        var contract = await _contractRepository.GetByIdAsync(id);
        if (contract == null)
            return NotFound(new { error = $"Contract {id} not found" });

        if (contract.Status != ProviderContractStatus.Active)
            return BadRequest(new { error = $"Can only suspend Active contracts, current: {contract.Status}" });

        contract.Status = ProviderContractStatus.Suspended;
        _logger.LogInformation("Suspended provider contract {ContractNumber}: {Reason}",
            contract.ContractNumber, SanitizeForLog(request.Reason));

        var updated = await _contractRepository.UpdateAsync(contract);
        return Ok(updated);
    }

    /// <summary>
    /// Terminate a provider contract (Any → Terminated)
    /// </summary>
    [HttpPut("{id}/terminate")]
    [ProducesResponseType(typeof(ProviderContract), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ProviderContract>> TerminateContract(string id, [FromBody] TerminateContractRequest request)
    {
        var contract = await _contractRepository.GetByIdAsync(id);
        if (contract == null)
            return NotFound(new { error = $"Contract {id} not found" });

        if (contract.Status == ProviderContractStatus.Terminated)
            return BadRequest(new { error = "Contract is already terminated" });

        contract.Status = ProviderContractStatus.Terminated;
        contract.TerminationDate = request.TerminationDate ?? DateTime.UtcNow;
        contract.TerminationReason = request.Reason;
        _logger.LogInformation("Terminated provider contract {ContractNumber}: {Reason}",
            contract.ContractNumber, SanitizeForLog(request.Reason));

        var updated = await _contractRepository.UpdateAsync(contract);
        return Ok(updated);
    }

    /// <summary>
    /// Reinstate a suspended contract (Suspended → Active)
    /// </summary>
    [HttpPut("{id}/reinstate")]
    [ProducesResponseType(typeof(ProviderContract), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ProviderContract>> ReinstateContract(string id)
    {
        var contract = await _contractRepository.GetByIdAsync(id);
        if (contract == null)
            return NotFound(new { error = $"Contract {id} not found" });

        if (contract.Status != ProviderContractStatus.Suspended)
            return BadRequest(new { error = $"Can only reinstate Suspended contracts, current: {contract.Status}" });

        contract.Status = ProviderContractStatus.Active;
        _logger.LogInformation("Reinstated provider contract {ContractNumber}", SanitizeForLog(contract.ContractNumber));

        var updated = await _contractRepository.UpdateAsync(contract);
        return Ok(updated);
    }

    /// <summary>
    /// Add a mid-term amendment to the contract
    /// </summary>
    [HttpPost("{id}/amendments")]
    [ProducesResponseType(typeof(ProviderContract), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProviderContract>> AddAmendment(string id, [FromBody] ContractAmendment amendment)
    {
        var contract = await _contractRepository.GetByIdAsync(id);
        if (contract == null)
            return NotFound(new { error = $"Contract {id} not found" });

        amendment.Id = Guid.NewGuid().ToString();
        amendment.CreatedAt = DateTime.UtcNow;
        contract.Amendments.Add(amendment);
        _logger.LogInformation("Added amendment to provider contract {ContractNumber}: {Type}",
            contract.ContractNumber, SanitizeForLog(amendment.AmendmentType));

        var updated = await _contractRepository.UpdateAsync(contract);
        return Ok(updated);
    }

    /// <summary>
    /// Propagate denormalized fields to all child rate configs.
    /// Calls capitation-service and ffs-service to update children.
    /// </summary>
    [HttpPut("{id}/sync-children")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> SyncChildren(string id)
    {
        var contract = await _contractRepository.GetByIdAsync(id);
        if (contract == null)
            return NotFound(new { error = $"Contract {id} not found" });

        // TODO: Propagate ContractNumber, ProviderNPI, ProviderName, LineOfBusiness
        // to all CapitationRateConfig children via capitation-service
        // /api/v1/capitation/rate-configs?contractId={id} PATCH endpoint,
        // and to FfsRateConfig children via ffs-service when implemented.
        // Until then, returns 501 so callers know propagation did not occur.
        _logger.LogInformation("Sync-children requested for provider contract {ContractNumber}",
            SanitizeForLog(contract.ContractNumber));

        return StatusCode(StatusCodes.Status501NotImplemented, new
        {
            message = "Child sync not yet implemented. ContractNumber, ProviderNPI, " +
                      "ProviderName, and LineOfBusiness must be manually kept in sync " +
                      "with child rate configs until this is implemented.",
            contractId = id,
            childCount = contract.CapitationRateConfigIds.Count + contract.FfsRateConfigIds.Count
        });
    }

    /// <summary>
    /// Get rate config IDs (capitation + FFS) for this contract
    /// </summary>
    [HttpGet("{id}/rate-configs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetRateConfigs(string id)
    {
        var contract = await _contractRepository.GetByIdAsync(id);
        if (contract == null)
            return NotFound(new { error = $"Contract {id} not found" });

        return Ok(new
        {
            contractId = id,
            capitationRateConfigIds = contract.CapitationRateConfigIds,
            ffsRateConfigIds = contract.FfsRateConfigIds
        });
    }

    /// <summary>
    /// Masks TIN to ***-**-XXXX format (last 4 digits only).
    /// Strips non-digits to handle both formatted (12-3456789) and raw (123456789) input.
    /// </summary>
    private static string? MaskTin(string? tin)
    {
        if (string.IsNullOrWhiteSpace(tin))
            return tin;

        var digits = new string(tin.Where(char.IsDigit).ToArray());
        if (digits.Length < 4)
            return "***-**-****";

        var last4 = digits[^4..];
        return $"***-**-{last4}";
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

public class SuspendContractRequest
{
    public string Reason { get; set; } = string.Empty;
}

public class TerminateContractRequest
{
    public string Reason { get; set; } = string.Empty;
    public DateTime? TerminationDate { get; set; }
}
