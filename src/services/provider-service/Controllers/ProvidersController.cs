using Microsoft.AspNetCore.Mvc;
using ProviderService.Adapters;
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
    private readonly ProviderAdapterFactory _adapterFactory;
    private readonly IProviderIntegrityProjectionService _integrityProjection;
    private readonly IPanelGatingValidator _panelGatingValidator;
    private readonly ICredentialingService _credentialing;
    private readonly ILogger<ProvidersController> _logger;

    public ProvidersController(
        IProviderRepository providerRepository,
        IProviderVersioningService versioning,
        ProviderAdapterFactory adapterFactory,
        IProviderIntegrityProjectionService integrityProjection,
        IPanelGatingValidator panelGatingValidator,
        ICredentialingService credentialing,
        ILogger<ProvidersController> logger)
    {
        _providerRepository = providerRepository;
        _versioning = versioning;
        _adapterFactory = adapterFactory;
        _integrityProjection = integrityProjection;
        _panelGatingValidator = panelGatingValidator;
        _credentialing = credentialing;
        _logger = logger;
    }

    /// <summary>
    /// Tenant id resolved by <see cref="ProviderService.Middleware.TenantMiddleware"/>.
    /// Throws when the middleware did not set it (defensive — the middleware always
    /// populates the value, defaulting to <c>"default-tenant"</c> in dev).
    /// </summary>
    private string TenantId =>
        HttpContext.Items["TenantId"]?.ToString()
            ?? throw new InvalidOperationException("TenantId not found in request context");

    /// <summary>
    /// Get provider by NPI
    /// </summary>
    [HttpGet("npi/{npi}")]
    [ProducesResponseType(typeof(Provider), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Provider>> GetByNPI(string npi)
    {
        _logger.LogInformation("Fetching provider by NPI: {NPI}", SanitizeForLog(npi));

        var (adapter, settings) = await _adapterFactory.GetAdapterWithSettingsAsync(TenantId);
        var response = await adapter.GetProviderByNpiAsync(new ProviderAdapterRequest
        {
            TenantId = TenantId,
            Npi = npi,
            PlatformSettings = settings,
        });

        if (response.Provider == null)
        {
            return NotFound($"Provider with NPI {npi} not found");
        }

        return Ok(response.Provider.ToProvider());
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

        var (adapter, settings) = await _adapterFactory.GetAdapterWithSettingsAsync(TenantId);
        var response = await adapter.SearchProvidersAsync(new ProviderAdapterRequest
        {
            TenantId = TenantId,
            Name = searchName,
            Specialty = specialty,
            ZipCode = zipCode,
            State = state,
            PlanId = planId,
            LineOfBusiness = lineOfBusiness,
            ProviderType = providerType,
            AcceptingNewPatients = acceptingNewPatients,
            Page = page,
            PageSize = pageSize,
            PlatformSettings = settings,
        });

        return Ok(response.Providers.Select(p => p.ToProvider()));
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
        var (adapter, settings) = await _adapterFactory.GetAdapterWithSettingsAsync(TenantId);
        var response = await adapter.SearchProvidersAsync(new ProviderAdapterRequest
        {
            TenantId = TenantId,
            Specialty = specialty,
            State = state,
            ProviderType = providerType,
            AcceptingNewPatients = acceptingNewPatients,
            Page = page,
            PageSize = pageSize,
            PlatformSettings = settings,
        });

        return Ok(response.Providers.Select(p => p.ToProvider()));
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

        var (adapter, settings) = await _adapterFactory.GetAdapterWithSettingsAsync(TenantId);
        var adapterResponse = await adapter.GetProviderAsync(new ProviderAdapterRequest
        {
            TenantId = TenantId,
            ProviderId = id,
            PlanId = planId,
            LineOfBusiness = lineOfBusiness,
            ServiceDate = checkDate,
            PlatformSettings = settings,
        });

        if (adapterResponse.Provider == null)
        {
            return NotFound($"Provider {id} not found");
        }

        var provider = adapterResponse.Provider.ToProvider();

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

        var (adapter, settings) = await _adapterFactory.GetAdapterWithSettingsAsync(TenantId);
        var adapterResponse = await adapter.GetProviderAsync(new ProviderAdapterRequest
        {
            TenantId = TenantId,
            ProviderId = id,
            PlanId = planId,
            LineOfBusiness = lineOfBusiness,
            PlatformSettings = settings,
        });

        if (adapterResponse.Provider == null)
        {
            return NotFound($"Provider {id} not found");
        }

        var provider = adapterResponse.Provider.ToProvider();

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
    // These actions are relative-route actions on a controller that is
    // dual-mounted at both /api/v1/providers and /api/Providers, so they
    // are reachable through both prefixes. The {id} token is the chain key
    // (Provider.ProviderId), distinct from per-version document Ids exposed
    // elsewhere on this controller. If these routes must be v1-only, they
    // need absolute /api/v1/providers/... route templates or a separate
    // v1-only controller.
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
    /// On-demand integrity-projection refresh (capability 5.4.5). Forces
    /// a verification round-trip outside the hosted-worker schedule and
    /// patches the head Active version's
    /// <see cref="Provider.IntegrityScore"/> /
    /// <see cref="Provider.IntegrityRating"/> /
    /// <see cref="Provider.LastVerifiedAt"/> /
    /// <see cref="Provider.NextVerificationDue"/> fields. Used by
    /// credentialing workflows and credential-event-driven re-verification.
    ///
    /// <para>
    /// Route is <c>/{id}/verification/refresh</c> (not <c>/verify</c>) to
    /// avoid namespace collision with verification-service's
    /// <c>GET /api/v1/providers/{npi}/verify</c>.
    /// </para>
    /// </summary>
    [HttpPost("{id}/verification/refresh")]
    [ProducesResponseType(typeof(IntegrityProjectionRefreshResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IntegrityProjectionRefreshResult>> RefreshVerification(
        string id,
        [FromQuery] bool force = true,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "on-demand verification refresh for provider {Id} force={Force}",
            SanitizeForLog(id), force);

        var result = await _integrityProjection.RefreshProviderAsync(
            TenantId, id, forceRefresh: force,
            actorId: ResolveActorId(),
            correlationId: HttpContext.TraceIdentifier,
            ct);
        if (result == null)
        {
            return NotFound(new { message = $"Provider {id} not found or has no Active version" });
        }
        return Ok(result);
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

        var (adapter, settings) = await _adapterFactory.GetAdapterWithSettingsAsync(TenantId);
        var response = await adapter.GetProviderAsync(new ProviderAdapterRequest
        {
            TenantId = TenantId,
            ProviderId = id,
            PlatformSettings = settings,
        });

        if (response.Provider == null)
        {
            return NotFound($"Provider {id} not found");
        }

        return Ok(response.Provider.ToProvider());
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

        // Soft validation (5.5): warn + count when the caller supplied
        // participations without panel-gating fields. The write proceeds
        // unchanged; the metric drives the eventual hard-validation
        // cutover decision.
        _panelGatingValidator.Inspect("CreateProvider", TenantId, provider);

        var actor = ResolveActorId();
        var draft = await _versioning.CreateDraftAsync(provider, actor);
        var activated = await _versioning.ActivateVersionAsync(draft.ProviderId, draft.VersionId, actor);
        return CreatedAtAction(nameof(GetById), new { id = activated.ProviderId }, activated);
    }

    /// <summary>
    /// Update provider. Self-healing: an Active (read-only) provider is
    /// auto-amended into a Draft, updated with the caller's field values,
    /// and activated within the same call — see the identical rationale
    /// on <see cref="AddNetworkParticipation"/>. Existing consumers PUT
    /// against `Id` (the chain key for legacy single-row chains); on a
    /// multi-version chain the same `Id` resolves to the head.
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

        try
        {
            var actor = ResolveActorId();
            var target = existing;
            var needsActivation = false;
            if (existing.VersionState != ProviderVersionState.Draft)
            {
                target = await _versioning.AmendActiveProviderAsync(existing.ProviderId, actor);
                needsActivation = true;
            }

            provider.Id = target.Id;
            provider.ProviderId = target.ProviderId;
            provider.VersionId = target.VersionId;
            provider.VersionNumber = target.VersionNumber;
            provider.VersionState = target.VersionState;
            provider.PredecessorVersionId = target.PredecessorVersionId;
            provider.CreatedDate = target.CreatedDate;
            provider.LastUpdatedDate = DateTime.UtcNow;

            // Soft validation (5.5): warn + count any participation that
            // arrives without panel-gating fields.
            _panelGatingValidator.Inspect("UpdateProvider", TenantId, provider);

            var updated = await _providerRepository.UpdateAsync(provider);
            if (needsActivation)
            {
                updated = await _versioning.ActivateVersionAsync(updated.ProviderId, updated.VersionId, actor);
            }

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
    /// Add network participation to provider. Self-healing: an Active
    /// (read-only) provider is auto-amended into a Draft, edited, and
    /// activated in the same call — the amend → activate cycle has no
    /// other endpoint that can populate a Draft's contents, so without
    /// this, adding a participation to any already-Active provider was
    /// silently impossible (the write always 409'd, and callers that
    /// treat 409 as "already present" masked the gap indefinitely).
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

        var actor = ResolveActorId();
        var needsActivation = false;
        if (provider.VersionState != ProviderVersionState.Draft)
        {
            try
            {
                provider = await _versioning.AmendActiveProviderAsync(provider.ProviderId, actor);
                needsActivation = true;
            }
            catch (ProviderVersionStateException ex) when (ex.IsNotFound)
            {
                return NotFound(new { message = ex.Message, providerId = ex.ProviderId });
            }
        }

        provider.NetworkParticipations.Add(participation);
        provider.LastUpdatedDate = DateTime.UtcNow;

        // Soft validation (5.5): inspect the appended participation
        // specifically — pre-existing participations on the row aren't
        // this caller's responsibility, so the bulk Inspect overload
        // would produce false-positive telemetry for legacy rows.
        _panelGatingValidator.Inspect("AddNetworkParticipation", TenantId, provider, participation);

        try
        {
            var updated = await _providerRepository.UpdateAsync(provider);
            if (needsActivation)
            {
                updated = await _versioning.ActivateVersionAsync(updated.ProviderId, updated.VersionId, actor);
            }

            return Ok(updated);
        }
        catch (ProviderVersionStateException ex)
        {
            return Conflict(new { message = ex.Message, providerId = ex.ProviderId, versionId = ex.VersionId, versionState = ex.CurrentState.ToString() });
        }
    }

    /// <summary>
    /// Update provider credentialing status. Legacy endpoint preserved for
    /// existing callers. Internally rewired through the event-sourced
    /// credentialing workflow (capability 5.6) — emits a
    /// <see cref="CredentialingEventType.DecisionRecorded"/> event with
    /// <see cref="DecisionAuthorityType.DelegatedAuthority"/> and patches
    /// the flat-field projection on <see cref="Provider"/>. Works on
    /// Active providers (the previous implementation called
    /// <see cref="IProviderRepository.UpdateAsync"/> and 409'd on every
    /// non-Draft row).
    /// </summary>
    [HttpPut("{id}/credentialing")]
    [ProducesResponseType(typeof(Provider), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<Provider>> UpdateCredentialing(
        string id,
        [FromBody] CredentialingUpdateRequest request,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Updating credentialing for provider {Id}, status={Status}",
            SanitizeForLog(id), request.Status);

        var provider = await _providerRepository.GetByIdAsync(id);
        if (provider == null)
        {
            return NotFound($"Provider {id} not found");
        }

        // Translate legacy status-only payload to a delegated-authority
        // decision. Approved and Denied map directly. Pending/Expired/
        // Suspended legacy values have no event-chain analogue — return
        // 400 with a hint that those transitions require the explicit
        // /credentialing/* sub-routes (or, for Suspended, await the
        // Phase 2 SuspensionRecorded event type).
        CredentialingDecision decision;
        switch (request.Status)
        {
            case CredentialingStatus.Approved:
                decision = CredentialingDecision.Approved;
                break;
            case CredentialingStatus.Denied:
                decision = CredentialingDecision.Denied;
                break;
            default:
                return BadRequest(new
                {
                    error = "unsupported_legacy_status",
                    message = $"Status {request.Status} cannot be set via PUT /credentialing. " +
                              "Use POST /credentialing/applications, /credentialing/recredential, " +
                              "or /credentialing/decisions for workflow-driven transitions.",
                });
        }

        var actorId = ResolveActorId();
        // Inbound JSON DateTime values typically deserialize with
        // Kind=Unspecified. Explicitly treat them as UTC before lifting
        // to DateTimeOffset so the implicit DateTime→DateTimeOffset
        // conversion can't pick up a local-time offset.
        var rawCredentialingDate = request.CredentialingDate ?? DateTime.UtcNow;
        var credentialingDateUtc = DateTime.SpecifyKind(rawCredentialingDate, DateTimeKind.Utc);
        var decisionRequest = new RecordDecisionRequest
        {
            Decision = decision,
            DecidedAt = new DateTimeOffset(credentialingDateUtc, TimeSpan.Zero),
            CredentialingDate = credentialingDateUtc,
            RecredentialingDueDate = request.RecredentialingDueDate.HasValue
                ? DateTime.SpecifyKind(request.RecredentialingDueDate.Value, DateTimeKind.Utc)
                : null,
            DecisionAuthorityType = DecisionAuthorityType.DelegatedAuthority,
            DecisionAuthorityId = actorId,
            CommitteeMembers = null,
            DecisionMinuteReference = null,
            DenialReason = null,
        };

        try
        {
            await _credentialing.RecordDecisionAsync(
                TenantId, id, decisionRequest, actorId, HttpContext.TraceIdentifier, ct);
        }
        catch (CredentialingNotFoundException)
        {
            return NotFound($"Provider {id} not found");
        }
        catch (CredentialingValidationException ex)
        {
            return BadRequest(new { error = "credentialing_validation_failed", message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "credentialing_publish_failed",
                message = ex.Message,
            });
        }

        // Re-read so the response reflects the patched projection. The
        // projection patch is best-effort — if the row hasn't been
        // updated (no Active head) the response still surfaces the row
        // as-stored.
        var updated = await _providerRepository.GetByIdAsync(id);
        return updated == null ? NotFound($"Provider {id} not found") : Ok(updated);
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
