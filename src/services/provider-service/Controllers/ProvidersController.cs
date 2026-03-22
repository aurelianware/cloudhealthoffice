using Microsoft.AspNetCore.Mvc;
using ProviderService.Models;
using ProviderService.Repositories;

namespace ProviderService.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class ProvidersController : ControllerBase
{
    private readonly IProviderRepository _providerRepository;
    private readonly ILogger<ProvidersController> _logger;

    public ProvidersController(
        IProviderRepository providerRepository,
        ILogger<ProvidersController> logger)
    {
        _providerRepository = providerRepository;
        _logger = logger;
    }

    /// <summary>
    /// Get provider by NPI
    /// </summary>
    [HttpGet("npi/{npi}")]
    [ProducesResponseType(typeof(Provider), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Provider>> GetByNPI(string npi)
    {
        _logger.LogInformation("Fetching provider by NPI: {NPI}", SanitizeForLog(npi));

        var provider = await _providerRepository.GetByNPIAsync(npi);
        if (provider == null)
        {
            return NotFound($"Provider with NPI {npi} not found");
        }

        return Ok(provider);
    }

    /// <summary>
    /// Search providers (by name, specialty, location, network)
    /// </summary>
    [HttpGet("search")]
    [ProducesResponseType(typeof(IEnumerable<Provider>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<Provider>>> SearchProviders(
        [FromQuery] string? name = null,
        [FromQuery] string? specialty = null,
        [FromQuery] string? zipCode = null,
        [FromQuery] string? state = null,
        [FromQuery] string? planId = null,
        [FromQuery] LineOfBusiness? lineOfBusiness = null,
        [FromQuery] ProviderType? providerType = null,
        [FromQuery] bool? acceptingNewPatients = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        _logger.LogInformation(
            "Searching providers: name={Name}, specialty={Specialty}, zip={Zip}, state={State}, plan={Plan}, lob={LOB}",
            SanitizeForLog(name), SanitizeForLog(specialty), SanitizeForLog(zipCode), SanitizeForLog(state), SanitizeForLog(planId), lineOfBusiness);

        var providers = await _providerRepository.SearchAsync(
            name, specialty, zipCode, state, planId, lineOfBusiness, providerType, acceptingNewPatients, page, pageSize);

        return Ok(providers);
    }

    /// <summary>
    /// Check if provider is in-network for plan/LOB
    /// CRITICAL for claims adjudication: determines if provider is in-network
    /// </summary>
    [HttpGet("{id}/network-status")]
    [ProducesResponseType(typeof(NetworkStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NetworkStatusResponse>> GetNetworkStatus(
        string id,
        [FromQuery] string? planId = null,
        [FromQuery] LineOfBusiness? lineOfBusiness = null,
        [FromQuery] DateTime? serviceDate = null)
    {
        var checkDate = serviceDate ?? DateTime.UtcNow;

        _logger.LogInformation(
            "Checking network status for provider {Id}, plan={Plan}, lob={LOB}, date={Date}",
            SanitizeForLog(id), SanitizeForLog(planId), lineOfBusiness, checkDate);

        var provider = await _providerRepository.GetByIdAsync(id);
        if (provider == null)
        {
            return NotFound($"Provider {id} not found");
        }

        // Find active network participation
        var participation = provider.NetworkParticipations
            .Where(np => (planId == null || np.PlanId == planId) &&
                        (lineOfBusiness == null || np.LineOfBusiness == lineOfBusiness) &&
                        np.EffectiveDate <= checkDate &&
                        (np.TerminationDate == null || np.TerminationDate >= checkDate))
            .OrderBy(np => np.NetworkTier) // Tier 1 first (lowest cost-sharing)
            .FirstOrDefault();

        var response = new NetworkStatusResponse
        {
            ProviderId = provider.Id,
            NPI = provider.NPI,
            ProviderName = provider.FullName,
            IsInNetwork = participation != null,
            NetworkTier = participation?.NetworkTier,
            EffectiveDate = participation?.EffectiveDate,
            TerminationDate = participation?.TerminationDate,
            AcceptingNewPatients = participation?.AcceptingNewPatients ?? false,
            CredentialingStatus = provider.CredentialingStatus,
            ProviderStatus = provider.Status
        };

        return Ok(response);
    }

    /// <summary>
    /// Get contracted rates for provider (for claims adjudication)
    /// </summary>
    [HttpGet("{id}/rates")]
    [ProducesResponseType(typeof(ContractedRates), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ContractedRates>> GetContractedRates(
        string id,
        [FromQuery] string? planId = null,
        [FromQuery] LineOfBusiness? lineOfBusiness = null)
    {
        _logger.LogInformation(
            "Fetching contracted rates for provider {Id}, plan={Plan}, lob={LOB}",
            SanitizeForLog(id), SanitizeForLog(planId), lineOfBusiness);

        var provider = await _providerRepository.GetByIdAsync(id);
        if (provider == null)
        {
            return NotFound($"Provider {id} not found");
        }

        // Find active network participation with rates
        var participation = provider.NetworkParticipations
            .Where(np => (planId == null || np.PlanId == planId) &&
                        (lineOfBusiness == null || np.LineOfBusiness == lineOfBusiness) &&
                        np.EffectiveDate <= DateTime.UtcNow &&
                        (np.TerminationDate == null || np.TerminationDate >= DateTime.UtcNow))
            .OrderBy(np => np.NetworkTier)
            .FirstOrDefault();

        if (participation?.Rates == null)
        {
            return NotFound($"No contracted rates found for provider {id}");
        }

        return Ok(participation.Rates);
    }

    /// <summary>
    /// Get provider by ID
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(Provider), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Provider>> GetById(string id)
    {
        _logger.LogInformation("Fetching provider by ID: {Id}", SanitizeForLog(id));

        var provider = await _providerRepository.GetByIdAsync(id);
        if (provider == null)
        {
            return NotFound($"Provider {id} not found");
        }

        return Ok(provider);
    }

    /// <summary>
    /// Create new provider
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(Provider), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Provider>> CreateProvider([FromBody] Provider provider)
    {
        _logger.LogInformation("Creating provider with NPI: {NPI}", SanitizeForLog(provider.NPI));

        // Check if NPI already exists
        var existing = await _providerRepository.GetByNPIAsync(provider.NPI);
        if (existing != null)
        {
            return BadRequest($"Provider with NPI {provider.NPI} already exists");
        }

        provider.Id = Guid.NewGuid().ToString();
        provider.CreatedDate = DateTime.UtcNow;
        provider.LastUpdatedDate = DateTime.UtcNow;

        var created = await _providerRepository.CreateAsync(provider);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Update provider
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(Provider), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Provider>> UpdateProvider(string id, [FromBody] Provider provider)
    {
        _logger.LogInformation("Updating provider: {Id}", SanitizeForLog(id));

        var existing = await _providerRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return NotFound($"Provider {id} not found");
        }

        provider.Id = id;
        provider.CreatedDate = existing.CreatedDate; // Preserve
        provider.LastUpdatedDate = DateTime.UtcNow;

        var updated = await _providerRepository.UpdateAsync(provider);
        return Ok(updated);
    }

    /// <summary>
    /// Delete provider (soft delete - set status to Terminated)
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteProvider(string id)
    {
        _logger.LogInformation("Deleting provider: {Id}", SanitizeForLog(id));

        var provider = await _providerRepository.GetByIdAsync(id);
        if (provider == null)
        {
            return NotFound($"Provider {id} not found");
        }

        // Soft delete: set status to Terminated
        provider.Status = ProviderStatus.Terminated;
        provider.TerminationDate = DateTime.UtcNow;
        provider.LastUpdatedDate = DateTime.UtcNow;

        await _providerRepository.UpdateAsync(provider);

        return NoContent();
    }

    /// <summary>
    /// Add network participation to provider
    /// </summary>
    [HttpPost("{id}/network-participations")]
    [ProducesResponseType(typeof(Provider), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Provider>> AddNetworkParticipation(
        string id,
        [FromBody] NetworkParticipation participation)
    {
        _logger.LogInformation(
            "Adding network participation for provider {Id}, plan={Plan}, lob={LOB}",
            SanitizeForLog(id), SanitizeForLog(participation.PlanId), participation.LineOfBusiness);

        var provider = await _providerRepository.GetByIdAsync(id);
        if (provider == null)
        {
            return NotFound($"Provider {id} not found");
        }

        provider.NetworkParticipations.Add(participation);
        provider.LastUpdatedDate = DateTime.UtcNow;

        var updated = await _providerRepository.UpdateAsync(provider);
        return Ok(updated);
    }

    /// <summary>
    /// Update provider credentialing status
    /// </summary>
    [HttpPut("{id}/credentialing")]
    [ProducesResponseType(typeof(Provider), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Provider>> UpdateCredentialing(
        string id,
        [FromBody] CredentialingUpdateRequest request)
    {
        _logger.LogInformation(
            "Updating credentialing for provider {Id}, status={Status}",
            SanitizeForLog(id), request.Status);

        var provider = await _providerRepository.GetByIdAsync(id);
        if (provider == null)
        {
            return NotFound($"Provider {id} not found");
        }

        provider.CredentialingStatus = request.Status;
        provider.CredentialingDate = request.CredentialingDate ?? DateTime.UtcNow;
        provider.RecredentialingDueDate = request.RecredentialingDueDate;
        provider.LastUpdatedDate = DateTime.UtcNow;

        var updated = await _providerRepository.UpdateAsync(provider);
        return Ok(updated);
    }

    /// <summary>
    /// Get provider bank account / EFT disbursement info by NPI.
    /// Used by capitation-service to look up payment details before disbursement.
    /// </summary>
    [HttpGet("npi/{npi}/bank-account")]
    [ProducesResponseType(typeof(ProviderBankAccount), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProviderBankAccount>> GetBankAccount(string npi)
    {
        _logger.LogInformation("Fetching bank account for provider NPI: {NPI}", SanitizeForLog(npi));

        var provider = await _providerRepository.GetByNPIAsync(npi);
        if (provider == null)
        {
            return NotFound($"Provider with NPI {npi} not found");
        }

        if (provider.BankAccount == null)
        {
            return NotFound($"No bank account on file for provider NPI {npi}");
        }

        return Ok(provider.BankAccount);
    }

    /// <summary>
    /// Upsert provider bank account / EFT disbursement info by NPI.
    /// Updates only the BankAccount sub-document on the existing Provider record.
    /// </summary>
    [HttpPut("npi/{npi}/bank-account")]
    [ProducesResponseType(typeof(ProviderBankAccount), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ProviderBankAccount>> UpsertBankAccount(
        string npi,
        [FromBody] ProviderBankAccount bankAccount)
    {
        _logger.LogInformation("Upserting bank account for provider NPI: {NPI}", SanitizeForLog(npi));

        var provider = await _providerRepository.GetByNPIAsync(npi);
        if (provider == null)
        {
            return NotFound($"Provider with NPI {npi} not found");
        }

        // Derive last-4 display fields from full values when provided
        if (!string.IsNullOrEmpty(bankAccount.RoutingNumber))
        {
            bankAccount.RoutingNumberLast4 = bankAccount.RoutingNumber.Length >= 4
                ? bankAccount.RoutingNumber[^4..]
                : bankAccount.RoutingNumber;
        }

        if (!string.IsNullOrEmpty(bankAccount.AccountNumber))
        {
            bankAccount.AccountNumberLast4 = bankAccount.AccountNumber.Length >= 4
                ? bankAccount.AccountNumber[^4..]
                : bankAccount.AccountNumber;
        }

        provider.BankAccount = bankAccount;
        provider.LastUpdatedDate = DateTime.UtcNow;

        await _providerRepository.UpdateAsync(provider);

        _logger.LogInformation(
            "Bank account updated for provider NPI: {NPI}, method={Method}, eftEnabled={EftEnabled}",
            SanitizeForLog(npi), bankAccount.PreferredDisbursementMethod, bankAccount.EftEnabled);

        return Ok(provider.BankAccount);
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

/// <summary>
/// Network status response (for claims adjudication)
/// </summary>
public class NetworkStatusResponse
{
    public string ProviderId { get; set; } = string.Empty;
    public string NPI { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public bool IsInNetwork { get; set; }
    public string? NetworkTier { get; set; }
    public DateTime? EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public bool AcceptingNewPatients { get; set; }
    public CredentialingStatus CredentialingStatus { get; set; }
    public ProviderStatus ProviderStatus { get; set; }
}

/// <summary>
/// Credentialing update request
/// </summary>
public class CredentialingUpdateRequest
{
    public CredentialingStatus Status { get; set; }
    public DateTime? CredentialingDate { get; set; }
    public DateTime? RecredentialingDueDate { get; set; }
}
