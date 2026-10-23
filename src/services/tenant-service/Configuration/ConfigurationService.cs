using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.PlanConfiguration;

namespace TenantService.Configuration;

public enum ConfigurationOutcome
{
    Applied,
    Pending,
    Invalid,
    Forbidden,
    NotFound,
    Conflict,
}

public sealed record ConfigurationResult(ConfigurationOutcome Outcome, IReadOnlyList<string> Errors, SettingValue? Value = null, PendingConfigurationChange? Pending = null)
{
    /// <summary>How a sponsor-level write was checked against line-of-business floors.</summary>
    public FloorCheck FloorCheck { get; init; } = FloorCheck.NotApplicable;

    public static ConfigurationResult Fail(ConfigurationOutcome outcome, params string[] errors) => new(outcome, errors);
}

/// <summary>A value to set (or clear) at one node.</summary>
public sealed record SettingWrite
{
    /// <summary>JSON: <c>7</c>, <c>true</c>, <c>"Weekly"</c>. Ignored when clearing.</summary>
    public string? Value { get; init; }
    /// <summary>First day it applies; today when omitted. Ignored for settings that are not effective-dated.</summary>
    public DateTime? EffectiveFrom { get; init; }
    public DateTime? EffectiveTo { get; init; }
    public bool Locked { get; init; }
    public string? Reason { get; init; }
}

/// <summary>An override somewhere in the tenant: the node, its value in force, and any later change already scheduled.</summary>
public sealed record SettingOverride(SettingLevel Level, string ScopeId, string? Value, bool Locked, DateTime? EffectiveFrom, int? Version,
    DateTime? NextChangeOn, string? NextValue);

/// <summary>
/// The configuration module's rules (plan-configuration.md §§4–7, 9a):
/// resolve, explain, write with validation, per-level permission and change
/// control, two-person approval, and the change log. The caller's tenant
/// comes from its token; nothing here crosses tenants.
/// </summary>
public sealed class ConfigurationService(
    ISettingCatalog catalog,
    IConfigurationStore store,
    IConfigurationChangePublisher publisher,
    TimeProvider clock,
    ILogger<ConfigurationService> logger,
    ISponsorLineOfBusinessLookup? sponsors = null)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private DateTime Today => DateTime.SpecifyKind(Now.Date, DateTimeKind.Utc);

    public async Task<ResolvedSetting?> ResolveAsync(string tenantId, string key, SettingScope scope, DateTime? asOf, CancellationToken ct = default)
    {
        if (catalog.Find(key) is not { } definition) return null;
        var date = (asOf ?? Today).Date;
        var nodes = scope.Chain().Where(n => definition.AllowedLevels.Contains(n.Level)).ToList();
        var versions = await store.GetVersionsAsync(tenantId, key, nodes, ct);
        return SettingResolver.Resolve(definition, scope, date, versions);
    }

    /// <summary>Every override of a key at the node's level and all more specific levels, with what is in force on <paramref name="asOf"/>.</summary>
    public async Task<IReadOnlyList<SettingOverride>?> OverridesAsync(string tenantId, string key, SettingLevel level, string? scopeId, DateTime? asOf, CancellationToken ct = default)
    {
        if (catalog.Find(key) is null) return null;
        var date = (asOf ?? Today).Date;
        var node = Normalize(level, scopeId);
        var versions = await store.GetVersionsAtOrBelowAsync(tenantId, key, level, ct);
        return versions
            // At the node's own level, only that node; below it, every node
            // (the hierarchy links that would narrow this arrive with programs,
            // plans and sponsor policies).
            .Where(v => v.Level > level || string.IsNullOrEmpty(node) || v.ScopeId == node)
            .GroupBy(v => (v.Level, v.ScopeId))
            .Select(g =>
            {
                var current = SettingResolver.InForce(g, date);
                var next = g.Where(v => v.EffectiveFrom > date).OrderBy(v => v.EffectiveFrom).ThenByDescending(v => v.Version).FirstOrDefault();
                return new SettingOverride(g.Key.Level, g.Key.ScopeId, current?.Value, current?.Locked ?? false, current?.EffectiveFrom,
                    current?.Version, next?.EffectiveFrom, next is null ? null : next.Cleared ? null : next.Value);
            })
            .Where(o => o.Value is not null || o.NextChangeOn is not null)
            .OrderBy(o => o.Level).ThenBy(o => o.ScopeId)
            .ToList();
    }

    public async Task<IReadOnlyList<SettingValue>?> HistoryAsync(string tenantId, string key, SettingLevel level, string? scopeId, CancellationToken ct = default)
    {
        if (catalog.Find(key) is null) return null;
        var versions = await store.GetVersionsAsync(tenantId, key, [(level, Normalize(level, scopeId))], ct);
        return versions.OrderBy(v => v.Version).ToList();
    }

    /// <summary>Sets a value at a node, or (with <paramref name="clear"/>) stops the node setting one from the effective date.</summary>
    public async Task<ConfigurationResult> WriteAsync(ICurrentActor actor, string key, SettingLevel level, string? scopeId, SettingWrite write, bool clear,
        CancellationToken ct = default)
    {
        if (catalog.Find(key) is not { } definition)
            return ConfigurationResult.Fail(ConfigurationOutcome.NotFound, $"Setting '{key}' is not defined.");
        var permission = SettingValidator.WritePermissionFor(level);
        if (!actor.HasPermission(permission))
            return ConfigurationResult.Fail(ConfigurationOutcome.Forbidden, $"Changing {key} at {level} needs {permission}.");

        var node = Normalize(level, scopeId);
        var (floorCheck, lineOfBusiness) = await FloorChecks.ForWriteAsync(sponsors, definition, level, node, clear, actor.TenantId, logger, ct);
        var errors = SettingValidator.Validate(definition, level, node, write.Value, clear, write.Reason, lineOfBusiness).ToList();
        var (from, to) = Dates(definition, write, errors);
        if (errors.Count > 0) return new ConfigurationResult(ConfigurationOutcome.Invalid, errors) { FloorCheck = floorCheck };

        var value = clear ? null : Canonical(definition, write.Value!);
        if (definition.ChangeControl == ChangeControl.TwoPersonApproval)
        {
            var pending = new PendingConfigurationChange
            {
                TenantId = actor.TenantId, Key = key, Level = level, ScopeId = node, Value = value, Cleared = clear,
                Locked = write.Locked, EffectiveFrom = from, EffectiveTo = to, Reason = write.Reason?.Trim(),
                RequestedBy = actor.UserId, RequestedAt = Now,
            };
            await store.AddPendingAsync(pending, ct);
            await store.AddChangeAsync(Record(pending, "Requested", actor.UserId, await BeforeAsync(actor.TenantId, key, level, node, from, ct), null), ct);
            return new ConfigurationResult(ConfigurationOutcome.Pending, [], Pending: pending) { FloorCheck = floorCheck };
        }

        var applied = await ApplyAsync(actor.TenantId, key, level, node, value, clear, write.Locked, from, to, write.Reason?.Trim(), actor.UserId, null, null, ct);
        return new ConfigurationResult(ConfigurationOutcome.Applied, [], applied) { FloorCheck = floorCheck };
    }

    public Task<IReadOnlyList<PendingConfigurationChange>> PendingAsync(string tenantId, CancellationToken ct = default) =>
        store.ListPendingAsync(tenantId, ct);

    /// <summary>
    /// Approves a pending change: <c>config:approve</c>, never one's own, and the
    /// change must still be valid. The approval claims the change (Pending →
    /// Approving), writes its version (at most once per change), then marks it
    /// Approved with that version. If it stops part-way, approving again
    /// finishes it rather than writing a second version.
    /// </summary>
    public async Task<ConfigurationResult> ApproveAsync(ICurrentActor actor, string pendingId, string? note, CancellationToken ct = default)
    {
        if (!actor.HasPermission(PlanConfigurationPermissions.Approve))
            return ConfigurationResult.Fail(ConfigurationOutcome.Forbidden, $"Approving needs {PlanConfigurationPermissions.Approve}.");
        if (await store.GetPendingAsync(actor.TenantId, pendingId, ct) is not { } pending)
            return ConfigurationResult.Fail(ConfigurationOutcome.NotFound, "No such pending change.");
        if (pending.Status is not (PendingChangeStatus.Pending or PendingChangeStatus.Approving))
            return ConfigurationResult.Fail(ConfigurationOutcome.Conflict, $"The change was already {pending.Status.ToString().ToLowerInvariant()}.");
        if (string.Equals(pending.RequestedBy, actor.UserId, StringComparison.Ordinal))
            return ConfigurationResult.Fail(ConfigurationOutcome.Forbidden, "A change must be approved by someone other than the person who requested it.");

        var floorCheck = FloorCheck.NotApplicable;
        if (pending.Status == PendingChangeStatus.Pending)
        {
            if (catalog.Find(pending.Key) is not { } definition)
                return ConfigurationResult.Fail(ConfigurationOutcome.Invalid, $"Setting '{pending.Key}' is no longer defined.");
            (floorCheck, var lineOfBusiness) = await FloorChecks.ForWriteAsync(sponsors, definition, pending.Level, pending.ScopeId, pending.Cleared, actor.TenantId, logger, ct);
            var errors = SettingValidator.Validate(definition, pending.Level, pending.ScopeId, pending.Value, pending.Cleared, pending.Reason, lineOfBusiness);
            if (errors.Count > 0) return new ConfigurationResult(ConfigurationOutcome.Invalid, errors) { FloorCheck = floorCheck };

            // Claim the decision first, so two approvers cannot both apply it.
            if (!await store.TryClaimApprovalAsync(actor.TenantId, pendingId, actor.UserId, Now, note, ct))
            {
                // Someone else claimed or decided it; only an unfinished claim may be finished here.
                pending = await store.GetPendingAsync(actor.TenantId, pendingId, ct);
                if (pending?.Status != PendingChangeStatus.Approving)
                    return ConfigurationResult.Fail(ConfigurationOutcome.Conflict, "Someone decided this change first.");
            }
        }
        else
        {
            logger.LogInformation("Finishing an approval of change {PendingId} for tenant {TenantId} that stopped part-way",
                LogSanitizer.Sanitize(pendingId), LogSanitizer.Sanitize(actor.TenantId));
        }

        // The approver of record is whoever claimed it; a finisher only completes it.
        var approver = pending!.DecidedBy ?? actor.UserId;
        var applied = await store.GetVersionForPendingAsync(actor.TenantId, pendingId, ct)
                      ?? await ApplyAsync(actor.TenantId, pending.Key, pending.Level, pending.ScopeId, pending.Value, pending.Cleared, pending.Locked,
                          pending.EffectiveFrom, pending.EffectiveTo, pending.Reason, pending.RequestedBy, pending.Id, approver, ct);

        if (!await store.TryCompleteApprovalAsync(actor.TenantId, pendingId, applied, ct)
            && (await store.GetPendingAsync(actor.TenantId, pendingId, ct))?.Status != PendingChangeStatus.Approved)
            return ConfigurationResult.Fail(ConfigurationOutcome.Conflict, "The change could not be marked approved; approve it again to finish.");

        pending.Status = PendingChangeStatus.Approved;
        pending.DecidedBy = approver;
        pending.AppliedVersion = applied.Version;
        pending.AppliedVersionId = applied.Id;
        return new ConfigurationResult(ConfigurationOutcome.Applied, [], applied, pending) { FloorCheck = floorCheck };
    }

    /// <summary>Rejects a change still waiting; a change whose approval has started can only be finished.</summary>
    public async Task<ConfigurationResult> RejectAsync(ICurrentActor actor, string pendingId, string? note, CancellationToken ct = default)
    {
        if (!actor.HasPermission(PlanConfigurationPermissions.Approve))
            return ConfigurationResult.Fail(ConfigurationOutcome.Forbidden, $"Rejecting needs {PlanConfigurationPermissions.Approve}.");
        if (await store.GetPendingAsync(actor.TenantId, pendingId, ct) is not { } pending)
            return ConfigurationResult.Fail(ConfigurationOutcome.NotFound, "No such pending change.");
        if (!await store.TryRejectAsync(actor.TenantId, pendingId, actor.UserId, Now, note, ct))
            return ConfigurationResult.Fail(ConfigurationOutcome.Conflict, "The change was already decided, or its approval has started.");
        pending.Status = PendingChangeStatus.Rejected;
        pending.DecidedBy = actor.UserId;
        pending.DecisionNote = note;
        var record = Record(pending, "Rejected", actor.UserId, null, null);
        record.Reason = note;
        await store.AddChangeAsync(record, ct);
        return new ConfigurationResult(ConfigurationOutcome.Applied, [], Pending: pending);
    }

    public Task<IReadOnlyList<ConfigurationChangeRecord>> ChangesAsync(string tenantId, string? key, int limit, CancellationToken ct = default) =>
        store.ListChangesAsync(tenantId, key, limit, ct);

    private async Task<SettingValue> ApplyAsync(string tenantId, string key, SettingLevel level, string node, string? value, bool clear, bool locked,
        DateTime from, DateTime? to, string? reason, string setBy, string? pendingId, string? approvedBy, CancellationToken ct)
    {
        var before = await BeforeAsync(tenantId, key, level, node, from, ct);
        var version = await store.AppendAsync(new SettingValue
        {
            TenantId = tenantId, Key = key, Level = level, ScopeId = node, Value = value, Cleared = clear,
            Locked = !clear && locked, EffectiveFrom = from, EffectiveTo = to, Reason = reason,
            SetBy = setBy, SetAt = Now, ApprovedFromPendingId = pendingId, ApprovedBy = approvedBy,
        }, ct);

        await store.AddChangeAsync(new ConfigurationChangeRecord
        {
            // An approval's record has a fixed id, so finishing a stopped approval does not log it twice.
            Id = pendingId is null ? Guid.NewGuid().ToString("N") : $"approved-{pendingId}",
            TenantId = tenantId, Key = key, Level = level, ScopeId = node,
            Action = pendingId is null ? (clear ? "Cleared" : "Set") : "Approved",
            Before = before, After = value, Locked = version.Locked, EffectiveFrom = from, EffectiveTo = to,
            Reason = reason, Actor = approvedBy ?? setBy, At = Now, Version = version.Version, PendingId = pendingId,
        }, ct);

        try
        {
            await publisher.PublishAsync(new ConfigurationChangedEvent
            {
                TenantId = tenantId, Key = key, Level = level, ScopeId = node, Version = version.Version, ChangedAt = Now,
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The change is stored; subscribers' caches expire on their own.
            logger.LogWarning(ex, "Could not publish the change to {Key} for tenant {TenantId}", LogSanitizer.Sanitize(key), LogSanitizer.Sanitize(tenantId));
        }
        return version;
    }

    private async Task<string?> BeforeAsync(string tenantId, string key, SettingLevel level, string node, DateTime on, CancellationToken ct) =>
        SettingResolver.InForce(await store.GetVersionsAsync(tenantId, key, [(level, node)], ct), on)?.Value;

    private (DateTime From, DateTime? To) Dates(SettingDefinition definition, SettingWrite write, List<string> errors)
    {
        if (!definition.EffectiveDated)
        {
            if (write.EffectiveFrom is not null || write.EffectiveTo is not null)
                errors.Add($"{definition.Key} is not effective-dated; it applies to every date from now on.");
            return (DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc), null);
        }
        var from = DateTime.SpecifyKind((write.EffectiveFrom ?? Today).Date, DateTimeKind.Utc);
        DateTime? to = write.EffectiveTo is { } t ? DateTime.SpecifyKind(t.Date, DateTimeKind.Utc) : null;
        if (to is not null && to <= from) errors.Add("The end date must be after the start date.");
        return (from, to);
    }

    private static string Canonical(SettingDefinition definition, string value) =>
        definition.Type is SettingType.Integer or SettingType.Decimal && SettingJson.AsNumber(value) is { } n
            ? SettingJson.Number(n)
            : value.Trim();

    /// <summary>The node id as stored: the line of business's canonical name; nothing for the tenant.</summary>
    public static string Normalize(SettingLevel level, string? scopeId) => level switch
    {
        SettingLevel.Tenant or SettingLevel.Platform => string.Empty,
        SettingLevel.LineOfBusiness => LinesOfBusiness.Normalize(scopeId) ?? scopeId?.Trim() ?? string.Empty,
        _ => scopeId?.Trim() ?? string.Empty,
    };

    private ConfigurationChangeRecord Record(PendingConfigurationChange p, string action, string actor, string? before, int? version) => new()
    {
        TenantId = p.TenantId, Key = p.Key, Level = p.Level, ScopeId = p.ScopeId, Action = action,
        Before = before, After = p.Value, Locked = p.Locked, EffectiveFrom = p.EffectiveFrom, EffectiveTo = p.EffectiveTo,
        Reason = p.Reason, Actor = actor, At = Now, Version = version, PendingId = p.Id,
    };
}
