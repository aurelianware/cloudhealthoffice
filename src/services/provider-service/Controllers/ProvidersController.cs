using Microsoft.AspNetCore.Mvc;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Services;

namespace ProviderService.Controllers;

/// <summary>
/// Providers REST surface. Mounted at both <c>api/v1/providers</c>
/// (canonical, used by all link generation via <c>CreatedAtAction</c>)
/// and <c>api/Providers</c> (legacy, preserved for existing consumers
/// — claims-service, coverage-service, member-portal). The v1 attribute
/// is listed first so URL generation prefers it.
/// </summary>
[ApiController]
[Route("api/v1/providers")]
[Route("api/[controller]")]
[Produces("application/json")]
public class ProvidersController : ControllerBase
{
    private readonly IProviderRepository _providerRepository;
    private readonly IProviderVersioningService _versioning;
    private readonly ILogger<ProvidersController> _logger;

    public ProvidersController(
        IProviderRepository providerRepository,
        IProviderVersioningService versioning,
        ILogger<ProvidersController> logger)
    {
        _providerRepository = providerRepository;
        _versioning = versioning;
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
        [FromQuery] string? q = null,
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

        // Support 'q' as alias for 'name' (portal autocomplete uses q=)
        var searchName = name ?? q;

        var providers = await _providerRepository.SearchAsync(
            searchName, specialty, zipCode, state, planId, lineOfBusiness, providerType, acceptingNewPatients, page, pageSize);

        return Ok(providers);
    }

    /// <summary>
    /// List providers with filters (alternative to search — used by portal grid)
    /// </summary>
    [HttpGet("list")]
    [ProducesResponseType(typeof(IEnumerable<Provider>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<Provider>>> ListProviders(
        [FromQuery] string? specialty = null,
        [FromQuery] string? state = null,
        [FromQuery] ProviderType? providerType = null,
        [FromQuery] bool? acceptingNewPatients = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var providers = await _providerRepository.SearchAsync(
            null, specialty, null, state, null, null, providerType, acceptingNewPatients, page, pageSize);
        return Ok(providers);
    }

    /// <summary>
    /// Get list of distinct provider specialties (for filter dropdowns)
    /// </summary>
    [HttpGet("specialties")]
    [ProducesResponseType(typeof(List<string>), StatusCodes.Status200OK)]
    public ActionResult<List<string>> GetSpecialties()
    {
        // Common NUCC taxonomy specialties for dropdown
        var specialties = new List<string>
        {
            "Internal Medicine", "Family Medicine", "Pediatrics",
            "Cardiology", "Orthopedics", "Dermatology",
            "Obstetrics & Gynecology", "Psychiatry", "Neurology",
            "General Surgery", "Emergency Medicine", "Radiology",
            "Anesthesiology", "Ophthalmology", "Pathology",
            "Oncology", "Endocrinology", "Gastroenterology",
            "Pulmonology", "Nephrology"
        };
        return Ok(specialties);
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
            ProviderId = provider.ProviderId,
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

    // -----------------------------------------------------------------
    // Version-chain endpoints (5.1 — Provider Identity & Versioning)
    //
    // Mounted only under /api/v1/providers — there is no legacy consumer
    // for these routes, so dual-mounting is unnecessary. The {id} token
    // is the chain key (Provider.ProviderId), distinct from per-version
    // document Ids exposed elsewhere on this controller.
    // -----------------------------------------------------------------

    /// <summary>
    /// Paginated, newest-first list of every version for a provider.
    /// </summary>
    [HttpGet("{id}/versions")]
    [ProducesResponseType(typeof(ProviderVersionPage), StatusCodes.Status200OK)]
    public async Task<ActionResult<ProviderVersionPage>> GetVersions(
        string id,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? continuationToken = null)
    {
        if (pageSize <= 0 || pageSize > 200) pageSize = 25;

        var (items, next) = await _versioning.ListVersionsAsync(id, pageSize, continuationToken);
        return Ok(new ProviderVersionPage { Items = items, ContinuationToken = next });
    }

    /// <summary>Get a single version by <c>VersionId</c>.</summary>
    [HttpGet("{id}/versions/{versionId}")]
    [ProducesResponseType(typeof(Provider), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Provider>> GetVersion(string id, string versionId)
    {
        var version = await _versioning.GetVersionAsync(id, versionId);
        if (version == null)
        {
            return NotFound(new { message = $"Version '{versionId}' of provider '{id}' not found" });
        }
        return Ok(version);
    }

    /// <summary>Create a Draft v1 of a brand-new provider.</summary>
    [HttpPost("drafts")]
    [ProducesResponseType(typeof(Provider), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Provider>> CreateDraft([FromBody] Provider provider)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var draft = await _versioning.CreateDraftAsync(provider, ResolveActorId());
        return CreatedAtAction(nameof(GetVersion),
            new { id = draft.ProviderId, versionId = draft.VersionId }, draft);
    }

    /// <summary>
    /// Move a Draft into Active. If a current head exists for the same
    /// provider, atomically supersedes it; if the head was Suspended or
    /// Terminated, this also emits a <c>ProviderVersionReactivated</c>.
    /// </summary>
    [HttpPost("{id}/versions/{versionId}/activate")]
    [ProducesResponseType(typeof(Provider), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<Provider>> Activate(string id, string versionId)
    {
        try
        {
            var activated = await _versioning.ActivateVersionAsync(id, versionId, ResolveActorId());
            return Ok(activated);
        }
        catch (ProviderVersionStateException ex) when (ex.IsNotFound)
        {
            return NotFound(new { message = ex.Message, providerId = ex.ProviderId, versionId = ex.VersionId });
        }
        catch (ProviderVersionStateException ex)
        {
            return Conflict(new { message = ex.Message, providerId = ex.ProviderId, versionId = ex.VersionId, versionState = ex.CurrentState.ToString() });
        }
    }

    /// <summary>
    /// Clone the latest Active version into a new Draft (next
    /// <c>VersionNumber</c>, predecessor link set).
    /// </summary>
    [HttpPost("{id}/amend")]
    [ProducesResponseType(typeof(Provider), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Provider>> Amend(string id)
    {
        try
        {
            var draft = await _versioning.AmendActiveProviderAsync(id, ResolveActorId());
            return CreatedAtAction(nameof(GetVersion),
                new { id = draft.ProviderId, versionId = draft.VersionId }, draft);
        }
        catch (ProviderVersionStateException ex) when (ex.IsNotFound)
        {
            return NotFound(new { message = ex.Message, providerId = ex.ProviderId });
        }
        catch (ProviderVersionStateException ex)
        {
            return Conflict(new { message = ex.Message, providerId = ex.ProviderId, versionId = ex.VersionId, versionState = ex.CurrentState.ToString() });
        }
    }

    /// <summary>Pause an Active version. Same VersionId remains addressable.</summary>
    [HttpPost("{id}/versions/{versionId}/suspend")]
    [ProducesResponseType(typeof(Provider), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<Provider>> Suspend(
        string id, string versionId, [FromBody] SuspendRequest body)
    {
        try
        {
            var suspended = await _versioning.SuspendVersionAsync(
                id, versionId, body?.Reason ?? string.Empty, ResolveActorId());
            return Ok(suspended);
        }
        catch (ProviderVersionStateException ex) when (ex.IsNotFound)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (ProviderVersionStateException ex)
        {
            return Conflict(new { message = ex.Message, providerId = ex.ProviderId, versionId = ex.VersionId, versionState = ex.CurrentState.ToString() });
        }
    }

    /// <summary>Permanently terminate an Active or Suspended version. No successor.</summary>
    [HttpPost("{id}/versions/{versionId}/terminate")]
    [ProducesResponseType(typeof(Provider), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<Provider>> Terminate(
        string id, string versionId, [FromBody] TerminateRequest body)
    {
        try
        {
            var terminated = await _versioning.TerminateVersionAsync(
                id, versionId, body?.Reason ?? string.Empty, ResolveActorId());
            return Ok(terminated);
        }
        catch (ProviderVersionStateException ex) when (ex.IsNotFound)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (ProviderVersionStateException ex)
        {
            return Conflict(new { message = ex.Message, providerId = ex.ProviderId, versionId = ex.VersionId, versionState = ex.CurrentState.ToString() });
        }
    }

    /// <summary>
    /// Lift a Suspended or Terminated head out by creating + activating
    /// a new version that supersedes it.
    /// </summary>
    [HttpPost("{id}/reactivate")]
    [ProducesResponseType(typeof(Provider), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Provider>> Reactivate(string id)
    {
        try
        {
            var reactivated = await _versioning.ReactivateProviderAsync(id, ResolveActorId());
            return Ok(reactivated);
        }
        catch (ProviderVersionStateException ex) when (ex.IsNotFound)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (ProviderVersionStateException ex)
        {
            return Conflict(new { message = ex.Message, providerId = ex.ProviderId, versionId = ex.VersionId, versionState = ex.CurrentState.ToString() });
        }
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
    /// Create new provider (legacy path). Creates and activates v1 in one
    /// shot for clients that don't need an explicit draft → activate flow.
    /// New consumers should use <c>POST /drafts</c> + <c>POST /versions/{v}/activate</c>.
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

        // Preserve the historical "server assigns Id" semantics — legacy
        // clients of POST /api/Providers do not control the document id.
        provider.Id = Guid.NewGuid().ToString();
        provider.ProviderId = provider.Id;
        provider.CreatedDate = DateTime.UtcNow;

        var actor = ResolveActorId();
        var draft = await _versioning.CreateDraftAsync(provider, actor);
        var activated = await _versioning.ActivateVersionAsync(draft.ProviderId, draft.VersionId, actor);
        return CreatedAtAction(nameof(GetById), new { id = activated.ProviderId }, activated);
    }

    /// <summary>
    /// Update provider. Active versions are read-only — the repository
    /// throws <see cref="ProviderVersionStateException"/> which surfaces as 409.
    /// Use <c>POST /amend</c> to create an editable Draft from the current Active version.
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(Provider), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<Provider>> UpdateProvider(string id, [FromBody] Provider provider)
    {
        _logger.LogInformation("Updating provider: {Id}", SanitizeForLog(id));

        var existing = await _providerRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return NotFound($"Provider {id} not found");
        }

        // Existing consumers PUT against `Id` (the chain key for legacy
        // single-row chains); on a multi-version chain the same `Id`
        // resolves to the head, which is read-only. Surface 409 with the
        // amend instructions so callers know the new flow.
        try
        {
            provider.Id = existing.Id;
            provider.ProviderId = existing.ProviderId;
            provider.VersionId = existing.VersionId;
            provider.VersionNumber = existing.VersionNumber;
            provider.VersionState = existing.VersionState;
            provider.CreatedDate = existing.CreatedDate;
            provider.LastUpdatedDate = DateTime.UtcNow;

            var updated = await _providerRepository.UpdateAsync(provider);
            return Ok(updated);
        }
        catch (ProviderVersionStateException ex) when (ex.IsNotFound)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (ProviderVersionStateException ex)
        {
            return Conflict(new
            {
                message = ex.Message,
                providerId = ex.ProviderId,
                versionId = ex.VersionId,
                versionState = ex.CurrentState.ToString()
            });
        }
    }

    /// <summary>
    /// Delete provider (soft delete via Terminate transition on the latest
    /// Active version). The historical chain is preserved.
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteProvider(string id)
    {
        _logger.LogInformation("Deleting provider: {Id}", SanitizeForLog(id));

        var existing = await _providerRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return NotFound($"Provider {id} not found");
        }

        try
        {
            await _versioning.TerminateVersionAsync(
                existing.ProviderId, existing.VersionId, "deleted via legacy DELETE", ResolveActorId());
            return NoContent();
        }
        catch (ProviderVersionStateException ex) when (ex.IsNotFound)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (ProviderVersionStateException ex)
        {
            return Conflict(new { message = ex.Message, providerId = ex.ProviderId, versionId = ex.VersionId, versionState = ex.CurrentState.ToString() });
        }
    }

    /// <summary>
    /// Add network participation to provider
    /// </summary>
    [HttpPost("{id}/network-participations")]
    [ProducesResponseType(typeof(Provider), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
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

        try
        {
            var updated = await _providerRepository.UpdateAsync(provider);
            return Ok(updated);
        }
        catch (ProviderVersionStateException ex)
        {
            return Conflict(new { message = ex.Message, providerId = ex.ProviderId, versionId = ex.VersionId, versionState = ex.CurrentState.ToString() });
        }
    }

    /// <summary>
    /// Update provider credentialing status
    /// </summary>
    [HttpPut("{id}/credentialing")]
    [ProducesResponseType(typeof(Provider), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
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

        try
        {
            var updated = await _providerRepository.UpdateAsync(provider);
            return Ok(updated);
        }
        catch (ProviderVersionStateException ex)
        {
            return Conflict(new { message = ex.Message, providerId = ex.ProviderId, versionId = ex.VersionId, versionState = ex.CurrentState.ToString() });
        }
    }

    /// <summary>
    /// Get provider bank account / EFT disbursement info by NPI.
    /// Returns only masked display fields (last-4 digits) — full account numbers
    /// are never exposed via this endpoint. Used by capitation-service for
    /// disbursement method selection; full credentials are fetched server-side
    /// only during NACHA file generation.
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

        // Return masked copy — strip full account/routing/tax numbers
        var masked = new ProviderBankAccount
        {
            EftEnabled = provider.BankAccount.EftEnabled,
            PreferredDisbursementMethod = provider.BankAccount.PreferredDisbursementMethod,
            AccountType = provider.BankAccount.AccountType,
            AccountHolderName = provider.BankAccount.AccountHolderName,
            StripeConnectedAccountId = provider.BankAccount.StripeConnectedAccountId,
            RoutingNumberLast4 = provider.BankAccount.RoutingNumberLast4,
            AccountNumberLast4 = provider.BankAccount.AccountNumberLast4,
            W9OnFile = provider.BankAccount.W9OnFile,
            TaxIdType = provider.BankAccount.TaxIdType,
            // RoutingNumber, AccountNumber, TaxId intentionally omitted
        };

        return Ok(masked);
    }

    /// <summary>
    /// Upsert provider bank account / EFT disbursement info by NPI.
    /// Updates only the BankAccount sub-document on the existing Provider record.
    /// </summary>
    [HttpPut("npi/{npi}/bank-account")]
    [ProducesResponseType(typeof(ProviderBankAccount), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
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

        try
        {
            await _providerRepository.UpdateAsync(provider);
        }
        catch (ProviderVersionStateException ex)
        {
            return Conflict(new { message = ex.Message, providerId = ex.ProviderId, versionId = ex.VersionId, versionState = ex.CurrentState.ToString() });
        }

        _logger.LogInformation(
            "Bank account updated for provider NPI: {NPI}, method={Method}, eftEnabled={EftEnabled}",
            SanitizeForLog(npi), bankAccount.PreferredDisbursementMethod, bankAccount.EftEnabled);

        return Ok(provider.BankAccount);
    }

    private string ResolveActorId()
    {
        var sub = HttpContext.User?.FindFirst("sub")?.Value;
        if (!string.IsNullOrEmpty(sub)) return sub;
        if (HttpContext.Request.Headers.TryGetValue("X-User-Id", out var header) && !string.IsNullOrEmpty(header.ToString()))
            return header.ToString();
        return "system";
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

/// <summary>Page envelope returned by <c>GET /{id}/versions</c>.</summary>
public sealed class ProviderVersionPage
{
    public IReadOnlyList<Provider> Items { get; set; } = Array.Empty<Provider>();
    public string? ContinuationToken { get; set; }
}

/// <summary>Body for POST <c>/{id}/versions/{versionId}/suspend</c>.</summary>
public sealed class SuspendRequest
{
    public string? Reason { get; set; }
}

/// <summary>Body for POST <c>/{id}/versions/{versionId}/terminate</c>.</summary>
public sealed class TerminateRequest
{
    public string? Reason { get; set; }
}
