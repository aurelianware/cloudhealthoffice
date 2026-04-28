using System.Text.Json;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Fakes;

/// <summary>
/// In-memory fake of <see cref="IProviderRepository"/> with full
/// version-chain semantics. Mirrors the in-memory benefit-plan
/// repository used by service- and controller-level tests.
/// </summary>
/// <remarks>
/// All store and fetch operations deep-clone via JSON round-trip so that
/// external mutations of a returned object cannot corrupt the stored
/// document — matching the behaviour of real Cosmos / Mongo round-trips
/// and preventing tests from becoming order-dependent due to shared
/// object references.
/// </remarks>
public sealed class InMemoryProviderRepository : IProviderRepository
{
    private static readonly JsonSerializerOptions _jsonOpts = new(JsonSerializerDefaults.Web);
    private readonly List<Provider> _docs = new();
    public IReadOnlyList<Provider> Docs => _docs;

    /// <summary>Tenant context defaults to "tenant-a" for the service tests.</summary>
    public string TenantId { get; set; } = "tenant-a";

    /// <summary>Set true to simulate a transactional batch failure.</summary>
    public bool FailNextActivate { get; set; }

    private static Provider Clone(Provider provider)
        => JsonSerializer.Deserialize<Provider>(JsonSerializer.Serialize(provider, _jsonOpts), _jsonOpts)!;

    private static Provider Hydrate(Provider provider)
    {
        if (string.IsNullOrEmpty(provider.ProviderId))
        {
            provider.ProviderId = provider.Id;
        }
        if (string.IsNullOrEmpty(provider.VersionId))
        {
            provider.VersionId = provider.Id;
            provider.VersionNumber = provider.VersionNumber <= 0 ? 1 : provider.VersionNumber;
            provider.VersionState = provider.Status switch
            {
                ProviderStatus.Terminated => ProviderVersionState.Terminated,
                ProviderStatus.Inactive => ProviderVersionState.Suspended,
                ProviderStatus.Pending => ProviderVersionState.Draft,
                _ => ProviderVersionState.Active
            };
        }
        provider.Status = provider.VersionState switch
        {
            ProviderVersionState.Active => ProviderStatus.Active,
            ProviderVersionState.Suspended => ProviderStatus.Inactive,
            ProviderVersionState.Terminated => ProviderStatus.Terminated,
            ProviderVersionState.Superseded => ProviderStatus.Inactive,
            ProviderVersionState.Draft => ProviderStatus.Pending,
            _ => provider.Status
        };
        return provider;
    }

    /// <summary>
    /// Returns hydrated clones of each row so legacy rows (empty
    /// VersionId / unset VersionState) are normalized before any state
    /// filter is applied — matching the real Mongo / Cosmos behavior
    /// where missing BSON fields are treated as "not set" rather than
    /// the C# enum default.
    /// </summary>
    private IEnumerable<Provider> HydratedView()
        => _docs.Select(d => Hydrate(Clone(d)));

    public Task<Provider?> GetByIdAsync(string id)
    {
        var match = HydratedView()
            .Where(d => (d.ProviderId == id || d.Id == id)
                && d.TenantId == TenantId
                && d.VersionState != ProviderVersionState.Draft)
            .OrderByDescending(d => d.VersionNumber)
            .FirstOrDefault();
        return Task.FromResult<Provider?>(match);
    }

    public Task<Provider?> GetByNPIAsync(string npi)
    {
        var match = HydratedView()
            .Where(d => d.NPI == npi && d.TenantId == TenantId
                && d.VersionState != ProviderVersionState.Draft)
            .OrderByDescending(d => d.VersionNumber)
            .FirstOrDefault();
        return Task.FromResult<Provider?>(match);
    }

    public Task<IEnumerable<Provider>> SearchAsync(
        string? name, string? specialty, string? zipCode, string? state,
        string? planId, LineOfBusiness? lineOfBusiness, ProviderType? providerType,
        bool? acceptingNewPatients, int page, int pageSize,
        string? firstName = null, string? lastName = null, string? city = null)
    {
        IEnumerable<Provider> q = _docs.Where(d => d.TenantId == TenantId && d.Status == ProviderStatus.Active);
        if (!string.IsNullOrEmpty(name))
        {
            q = q.Where(d =>
                (d.FirstName ?? string.Empty).Contains(name, StringComparison.OrdinalIgnoreCase) ||
                (d.LastName ?? string.Empty).Contains(name, StringComparison.OrdinalIgnoreCase) ||
                (d.OrganizationName ?? string.Empty).Contains(name, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrEmpty(firstName))
            q = q.Where(d => (d.FirstName ?? string.Empty).Contains(firstName, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(lastName))
            q = q.Where(d => (d.LastName ?? string.Empty).Contains(lastName, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(specialty))
            q = q.Where(d => d.PrimarySpecialty.Contains(specialty, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(zipCode))
            q = q.Where(d => d.ZipCode == zipCode);
        if (!string.IsNullOrEmpty(state))
            q = q.Where(d => d.State == state);
        if (!string.IsNullOrEmpty(city))
            q = q.Where(d => (d.City ?? string.Empty).Contains(city, StringComparison.OrdinalIgnoreCase));
        if (providerType.HasValue)
            q = q.Where(d => d.ProviderType == providerType.Value);
        if (acceptingNewPatients.HasValue)
            q = q.Where(d => d.AcceptingNewPatients == acceptingNewPatients.Value);
        return Task.FromResult(q.Skip((page - 1) * pageSize).Take(pageSize).Select(d => Hydrate(Clone(d))));
    }

    public Task<Provider> CreateAsync(Provider provider)
    {
        if (string.IsNullOrEmpty(provider.Id)) provider.Id = Guid.NewGuid().ToString();
        if (string.IsNullOrEmpty(provider.TenantId)) provider.TenantId = TenantId;
        _docs.Add(Clone(provider));
        return Task.FromResult(Clone(provider));
    }

    public Task<Provider> UpdateAsync(Provider provider)
    {
        var existing = _docs.FirstOrDefault(d => d.Id == provider.Id && d.TenantId == provider.TenantId);
        if (existing != null)
        {
            var hydrated = Hydrate(Clone(existing));
            if (hydrated.VersionState != ProviderVersionState.Draft)
            {
                throw new ProviderVersionStateException(
                    hydrated.ProviderId, hydrated.VersionId, hydrated.VersionState,
                    $"Provider version {hydrated.VersionId} is {hydrated.VersionState} and cannot be updated. Create an amendment via POST /amend.");
            }
        }
        if (existing != null) _docs.Remove(existing);
        _docs.Add(Clone(provider));
        return Task.FromResult(Clone(provider));
    }

    public Task DeleteAsync(string id)
    {
        _docs.RemoveAll(d => d.Id == id && d.TenantId == TenantId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Provider>> ListNetworkRosterAsync(
        NetworkRosterQuery query,
        NetworkRosterSort sort,
        int skip,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(query.TenantId))
            throw new ArgumentException("NetworkRosterQuery.TenantId is required.", nameof(query));
        if (string.IsNullOrEmpty(query.NetworkId))
            throw new ArgumentException("NetworkRosterQuery.NetworkId is required.", nameof(query));

        var pageSize = Math.Clamp(query.PageSize, 1, NetworkRosterDefaults.MaxPageSize);
        var safeSkip = Math.Max(skip, 0);
        var asOf = (query.AsOfDate ?? DateTime.UtcNow).ToUniversalTime();

        bool ParticipationMatches(NetworkParticipation n)
        {
            if (n.NetworkId != query.NetworkId) return false;
            if (n.EffectiveDate > asOf) return false;
            if (n.TerminationDate.HasValue && n.TerminationDate.Value < asOf) return false;
            if (query.LineOfBusiness.HasValue && n.LineOfBusiness != query.LineOfBusiness.Value) return false;
            if (!string.IsNullOrEmpty(query.Tier) && !string.Equals(n.NetworkTier, query.Tier, StringComparison.Ordinal)) return false;
            if (query.AcceptingNewPatients.HasValue && n.AcceptingNewPatients != query.AcceptingNewPatients.Value) return false;
            return true;
        }

        bool ProviderMatches(Provider p)
        {
            if (p.TenantId != query.TenantId) return false;
            if (p.VersionState != ProviderVersionState.Active) return false;
            if (p.TerminationDate.HasValue && p.TerminationDate.Value < asOf) return false;
            if (!p.NetworkParticipations.Any(ParticipationMatches)) return false;
            if (!string.IsNullOrEmpty(query.Specialty))
            {
                var s = query.Specialty;
                if (!(p.PrimarySpecialty?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false)
                    && !(p.TaxonomyCode?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false))
                    return false;
            }
            if (query.AcceptingNewPatients.HasValue && p.AcceptingNewPatients != query.AcceptingNewPatients.Value) return false;
            return true;
        }

        var hydrated = HydratedView().Where(ProviderMatches);

        IEnumerable<Provider> ordered = sort switch
        {
            NetworkRosterSort.NameDesc => hydrated
                .OrderByDescending(p => p.LastName ?? string.Empty, StringComparer.Ordinal)
                .ThenByDescending(p => p.OrganizationName ?? string.Empty, StringComparer.Ordinal)
                .ThenByDescending(p => p.Id, StringComparer.Ordinal),
            NetworkRosterSort.IntegrityScoreDesc => hydrated
                // Service layer normalizes nulls-last; the fake returns the
                // raw score-desc order to mirror real-repo semantics.
                .OrderByDescending(p => p.IntegrityScore ?? int.MinValue)
                .ThenBy(p => p.Id, StringComparer.Ordinal),
            _ => hydrated
                .OrderBy(p => p.LastName ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(p => p.OrganizationName ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(p => p.Id, StringComparer.Ordinal),
        };

        var slice = ordered.Skip(safeSkip).Take(pageSize).ToList();
        return Task.FromResult<IReadOnlyList<Provider>>(slice);
    }

    public Task<Provider?> GetLatestActiveAsync(string providerId, DateTime asOf)
    {
        var match = HydratedView()
            .Where(d => (d.ProviderId == providerId || d.Id == providerId)
                && d.TenantId == TenantId
                && d.VersionState == ProviderVersionState.Active
                && (d.TerminationDate == null || d.TerminationDate >= asOf))
            .OrderByDescending(d => d.VersionNumber)
            .FirstOrDefault();
        return Task.FromResult<Provider?>(match);
    }

    public Task<Provider?> GetVersionAsync(string providerId, string versionId)
    {
        var match = HydratedView().FirstOrDefault(d =>
            (d.ProviderId == providerId || d.Id == providerId)
            && d.TenantId == TenantId
            && d.VersionId == versionId);
        return Task.FromResult<Provider?>(match);
    }

    public Task<(IReadOnlyList<Provider> Items, string? ContinuationToken)> ListVersionsAsync(
        string providerId, int pageSize, string? continuationToken)
    {
        var skip = 0;
        if (!string.IsNullOrEmpty(continuationToken) && int.TryParse(continuationToken, out var parsed))
            skip = parsed;

        var ordered = HydratedView()
            .Where(d => (d.ProviderId == providerId || d.Id == providerId)
                && d.TenantId == TenantId)
            .OrderByDescending(d => d.VersionNumber)
            .Skip(skip)
            .ToList();

        var slice = ordered.Take(pageSize).ToList();
        var next = ordered.Count > pageSize ? (skip + pageSize).ToString() : null;
        return Task.FromResult<(IReadOnlyList<Provider>, string?)>((slice, next));
    }

    public Task<Provider> CreateDraftAsync(Provider draft)
    {
        if (string.IsNullOrEmpty(draft.Id)) draft.Id = Guid.NewGuid().ToString();
        if (string.IsNullOrEmpty(draft.ProviderId)) draft.ProviderId = draft.Id;
        if (string.IsNullOrEmpty(draft.TenantId)) draft.TenantId = TenantId;
        draft.VersionState = ProviderVersionState.Draft;
        _docs.Add(Clone(draft));
        return Task.FromResult(Clone(draft));
    }

    public Task<Provider> UpdateDraftAsync(Provider draft)
    {
        var existing = _docs.FirstOrDefault(d => d.Id == draft.Id && d.TenantId == draft.TenantId)
            ?? throw new ProviderVersionStateException(draft.ProviderId, draft.VersionId, ProviderVersionState.Draft,
                $"Draft {draft.VersionId} not found") { IsNotFound = true };
        if (existing.VersionState != ProviderVersionState.Draft)
            throw new ProviderVersionStateException(existing.ProviderId, existing.VersionId, existing.VersionState,
                $"Provider version {existing.VersionId} is {existing.VersionState} and cannot be edited.");
        _docs.Remove(existing);
        _docs.Add(Clone(draft));
        return Task.FromResult(Clone(draft));
    }

    public Task<Provider> ActivateAndSupersedeAsync(Provider draftToActivate, Provider? predecessor)
    {
        if (FailNextActivate)
        {
            FailNextActivate = false;
            throw new ProviderVersionStateException(
                draftToActivate.ProviderId, draftToActivate.VersionId, draftToActivate.VersionState,
                "Simulated transactional batch failure");
        }

        var snapshot = _docs.ToList();
        try
        {
            var existingDraft = _docs.FirstOrDefault(d => d.Id == draftToActivate.Id);
            if (existingDraft != null) _docs.Remove(existingDraft);
            _docs.Add(Clone(draftToActivate));

            if (predecessor != null)
            {
                var existingPred = _docs.FirstOrDefault(d => d.Id == predecessor.Id);
                if (existingPred != null) _docs.Remove(existingPred);
                _docs.Add(Clone(predecessor));
            }
            return Task.FromResult(Clone(draftToActivate));
        }
        catch
        {
            _docs.Clear();
            _docs.AddRange(snapshot);
            throw;
        }
    }

    public Task<Provider> ReplaceVersionRowAsync(Provider version)
    {
        var existing = _docs.FirstOrDefault(d => d.Id == version.Id && d.TenantId == version.TenantId);
        if (existing != null) _docs.Remove(existing);
        _docs.Add(Clone(version));
        return Task.FromResult(Clone(version));
    }

    public Task<bool> UpdateIntegrityProjectionAsync(
        string tenantId,
        string providerId,
        int? integrityScore,
        string? integrityRating,
        DateTimeOffset? lastVerifiedAt,
        DateTimeOffset? nextVerificationDue,
        CancellationToken ct = default)
    {
        // Find the head Active row for the chain. Hydration normalises
        // legacy rows (missing VersionState) to Active so they're patched
        // too — matching the real-repo behaviour.
        var head = _docs
            .Select(d => Hydrate(Clone(d)))
            .Where(d => d.TenantId == tenantId
                && (d.ProviderId == providerId || d.Id == providerId)
                && d.VersionState == ProviderVersionState.Active)
            .OrderByDescending(d => d.VersionNumber)
            .FirstOrDefault();
        if (head == null) return Task.FromResult(false);

        // Patch the underlying stored row (not the hydrated clone).
        var stored = _docs.First(d => d.Id == head.Id && d.TenantId == head.TenantId);
        stored.IntegrityScore = integrityScore;
        stored.IntegrityRating = integrityRating;
        stored.LastVerifiedAt = lastVerifiedAt;
        stored.NextVerificationDue = nextVerificationDue;
        stored.LastUpdatedDate = DateTime.UtcNow;
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<Provider>> ListProvidersForIntegrityRefreshAsync(
        string tenantId,
        DateTimeOffset dueBefore,
        bool includeNeverVerified,
        int skip,
        int pageSize,
        CancellationToken ct = default)
    {
        var safeSkip = Math.Max(skip, 0);
        var safePageSize = Math.Clamp(pageSize, 1, 1000);

        bool DueMatches(Provider p)
        {
            if (p.NextVerificationDue == null) return includeNeverVerified;
            return p.NextVerificationDue <= dueBefore;
        }

        var slice = HydratedView()
            .Where(d => d.TenantId == tenantId
                && d.VersionState == ProviderVersionState.Active
                && DueMatches(d))
            .OrderBy(d => d.ProviderId, StringComparer.Ordinal)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .Skip(safeSkip)
            .Take(safePageSize)
            .ToList();
        return Task.FromResult<IReadOnlyList<Provider>>(slice);
    }

    public Task<IReadOnlyList<string>> ListProviderTenantIdsAsync(CancellationToken ct = default)
    {
        var distinct = _docs
            .Select(d => d.TenantId)
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(distinct);
    }

    public Task<long> CountStaleProvidersAsync(
        string tenantId,
        DateTimeOffset staleBefore,
        CancellationToken ct = default)
    {
        // Apply the same hydration rule the other read methods use so
        // legacy rows where VersionState is absent are still treated as
        // Active when Status == Active (matches the Cosmos / Mongo
        // hydration rule documented in
        // docs/architecture/provider-versioning.md "Legacy hydration
        // query pattern"). Counting against raw _docs would miss those
        // rows and diverge from the real repositories' behavior.
        var count = HydratedView()
            .Count(d => d.TenantId == tenantId
                && d.VersionState == ProviderVersionState.Active
                && (d.LastVerifiedAt == null || d.LastVerifiedAt < staleBefore));
        return Task.FromResult((long)count);
    }

    /// <summary>
    /// Set true on the next call to simulate a Cosmos PreconditionFailed
    /// (etag conflict) so backfill-service tests can exercise the
    /// skip-and-count branch.
    /// </summary>
    public bool FailNextPanelGatingPatchAsConflict { get; set; }

    public Task<bool> UpdatePanelGatingDefaultsAsync(
        string tenantId,
        string providerId,
        int participationIndex,
        PanelGatingFields fields,
        CancellationToken ct = default)
    {
        if (FailNextPanelGatingPatchAsConflict)
        {
            FailNextPanelGatingPatchAsConflict = false;
            return Task.FromResult(false);
        }

        var head = _docs
            .Select(d => Hydrate(Clone(d)))
            .Where(d => d.TenantId == tenantId
                && (d.ProviderId == providerId || d.Id == providerId)
                && d.VersionState == ProviderVersionState.Active)
            .OrderByDescending(d => d.VersionNumber)
            .FirstOrDefault();
        if (head == null) return Task.FromResult(false);

        var stored = _docs.First(d => d.Id == head.Id && d.TenantId == head.TenantId);
        if (stored.NetworkParticipations == null
            || participationIndex < 0
            || participationIndex >= stored.NetworkParticipations.Count)
        {
            return Task.FromResult(false);
        }

        var slot = stored.NetworkParticipations[participationIndex];
        slot.PanelLimit = fields.PanelLimit;
        slot.PanelAccepted = fields.PanelAccepted;
        slot.AcceptedLobs = fields.AcceptedLobs.ToList();
        slot.MinAcceptedAgeYears = fields.MinAcceptedAgeYears;
        slot.MaxAcceptedAgeYears = fields.MaxAcceptedAgeYears;
        stored.LastUpdatedDate = DateTime.UtcNow;
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<Provider>> ListProvidersForPanelGatingBackfillAsync(
        string tenantId,
        int skip,
        int pageSize,
        CancellationToken ct = default)
    {
        var safeSkip = Math.Max(skip, 0);
        var safePageSize = Math.Clamp(pageSize, 1, 1000);

        bool HasUntouchedParticipation(Provider p) =>
            p.NetworkParticipations != null
            && p.NetworkParticipations.Any(PanelGatingFields.IsAtTypeDefaults);

        var slice = HydratedView()
            .Where(d => d.TenantId == tenantId
                && d.VersionState == ProviderVersionState.Active
                && HasUntouchedParticipation(d))
            .OrderBy(d => d.ProviderId, StringComparer.Ordinal)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .Skip(safeSkip)
            .Take(safePageSize)
            .ToList();
        return Task.FromResult<IReadOnlyList<Provider>>(slice);
    }

    /// <summary>
    /// Calls captured by <see cref="UpdateCredentialingProjectionAsync"/>
    /// — service-level tests inspect this list to assert the projection
    /// patch was invoked with the expected arguments.
    /// </summary>
    public List<(string TenantId, string ProviderId, CredentialingStatus Status, DateTime? CredentialingDate, DateTime? RecredentialingDueDate)> CredentialingProjectionPatches { get; } = new();

    public Task<bool> UpdateCredentialingProjectionAsync(
        string tenantId,
        string providerId,
        CredentialingStatus status,
        DateTime? credentialingDate,
        DateTime? recredentialingDueDate,
        CancellationToken ct = default)
    {
        CredentialingProjectionPatches.Add((tenantId, providerId, status, credentialingDate, recredentialingDueDate));

        var head = _docs
            .Select(d => Hydrate(Clone(d)))
            .Where(d => d.TenantId == tenantId
                && (d.ProviderId == providerId || d.Id == providerId)
                && d.VersionState == ProviderVersionState.Active)
            .OrderByDescending(d => d.VersionNumber)
            .FirstOrDefault();
        if (head == null) return Task.FromResult(false);

        var stored = _docs.First(d => d.Id == head.Id && d.TenantId == head.TenantId);
        stored.CredentialingStatus = status;
        stored.CredentialingDate = credentialingDate;
        stored.RecredentialingDueDate = recredentialingDueDate;
        stored.LastUpdatedDate = DateTime.UtcNow;
        return Task.FromResult(true);
    }
}

public sealed class InMemoryProviderTransitionRepository : IProviderTransitionRepository
{
    public List<ProviderTransition> Items { get; } = new();

    public Task<ProviderTransition> AppendAsync(ProviderTransition transition, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(transition.Id)) transition.Id = Guid.NewGuid().ToString();
        Items.Add(transition);
        return Task.FromResult(transition);
    }

    public Task<IReadOnlyList<ProviderTransition>> ListAsync(string providerId, string tenantId, CancellationToken ct = default)
    {
        var matches = Items
            .Where(x => x.ProviderId == providerId && x.TenantId == tenantId)
            .OrderByDescending(x => x.OccurredAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<ProviderTransition>>(matches);
    }
}

public sealed class FakeProviderVersionEventPublisher : IProviderVersionEventPublisher
{
    public List<ProviderVersionEvent> Events { get; } = new();

    public Task<ProviderVersionEvent> PublishVersionActivatedAsync(Provider version, string? actorId, string? correlationId, CancellationToken ct = default)
        => Append(new ProviderVersionEvent
        {
            EventId = $"activated:{version.VersionId}",
            EventType = ProviderVersionEventType.ProviderVersionActivated,
            TenantId = version.TenantId,
            ProviderId = version.ProviderId,
            VersionId = version.VersionId,
            ActorId = actorId,
            CorrelationId = correlationId
        });

    public Task<ProviderVersionEvent> PublishVersionSupersededAsync(Provider from, Provider to, string? reason, string? actorId, string? correlationId, CancellationToken ct = default)
        => Append(new ProviderVersionEvent
        {
            EventId = $"superseded:{from.VersionId}->{to.VersionId}",
            EventType = ProviderVersionEventType.ProviderVersionSuperseded,
            TenantId = from.TenantId,
            ProviderId = from.ProviderId,
            VersionId = from.VersionId,
            ActorId = actorId,
            CorrelationId = correlationId
        });

    public Task<ProviderVersionEvent> PublishVersionSuspendedAsync(Provider version, string? reason, string? actorId, string? correlationId, CancellationToken ct = default)
        => Append(new ProviderVersionEvent
        {
            EventId = $"suspended:{version.VersionId}",
            EventType = ProviderVersionEventType.ProviderVersionSuspended,
            TenantId = version.TenantId,
            ProviderId = version.ProviderId,
            VersionId = version.VersionId,
            ActorId = actorId,
            CorrelationId = correlationId
        });

    public Task<ProviderVersionEvent> PublishVersionReactivatedAsync(Provider version, Provider? predecessor, string? actorId, string? correlationId, CancellationToken ct = default)
        => Append(new ProviderVersionEvent
        {
            EventId = $"reactivated:{version.VersionId}",
            EventType = ProviderVersionEventType.ProviderVersionReactivated,
            TenantId = version.TenantId,
            ProviderId = version.ProviderId,
            VersionId = version.VersionId,
            ActorId = actorId,
            CorrelationId = correlationId
        });

    public Task<ProviderVersionEvent> PublishVersionTerminatedAsync(Provider version, string? reason, string? actorId, string? correlationId, CancellationToken ct = default)
        => Append(new ProviderVersionEvent
        {
            EventId = $"terminated:{version.VersionId}",
            EventType = ProviderVersionEventType.ProviderVersionTerminated,
            TenantId = version.TenantId,
            ProviderId = version.ProviderId,
            VersionId = version.VersionId,
            ActorId = actorId,
            CorrelationId = correlationId
        });

    private Task<ProviderVersionEvent> Append(ProviderVersionEvent e)
    {
        // Idempotent — re-emit returns the existing row.
        var existing = Events.FirstOrDefault(x =>
            x.TenantId == e.TenantId && x.ProviderId == e.ProviderId && x.EventId == e.EventId);
        if (existing != null) return Task.FromResult(existing);

        e.Version = Events.Count(x => x.TenantId == e.TenantId && x.ProviderId == e.ProviderId) + 1;
        e.OccurredAt = DateTime.UtcNow;
        Events.Add(e);
        return Task.FromResult(e);
    }
}
