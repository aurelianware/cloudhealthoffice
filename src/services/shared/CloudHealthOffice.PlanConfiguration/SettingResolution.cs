namespace CloudHealthOffice.PlanConfiguration;

/// <summary>
/// One version of a value set at one level (plan-configuration.md §6).
/// Values are never edited in place: a change, a correction or a removal
/// adds a version. At a date, the level's value is the version in force then
/// with the latest <see cref="EffectiveFrom"/>, the highest
/// <see cref="Version"/> breaking ties; a <see cref="Cleared"/> version means
/// the level sets nothing from that date.
/// </summary>
public sealed class SettingValue
{
    /// <summary><c>{tenant}|{key}|{level}|{scope}|{version}</c>.</summary>
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public SettingLevel Level { get; set; }
    /// <summary>The node at that level: the line-of-business name, the sponsor's group number; empty for the tenant.</summary>
    public string ScopeId { get; set; } = string.Empty;
    /// <summary>JSON; null when <see cref="Cleared"/>.</summary>
    public string? Value { get; set; }
    public bool Cleared { get; set; }
    /// <summary>Lower (more specific) levels cannot override a locked value.</summary>
    public bool Locked { get; set; }
    /// <summary>UTC midnight of the first day the version applies.</summary>
    public DateTime EffectiveFrom { get; set; }
    /// <summary>UTC midnight of the first day it no longer applies; null while open-ended.</summary>
    public DateTime? EffectiveTo { get; set; }
    public string? Reason { get; set; }
    public string SetBy { get; set; } = string.Empty;
    public DateTime SetAt { get; set; }
    /// <summary>1, 2, 3 ... per tenant, key, level and scope.</summary>
    public int Version { get; set; }
    /// <summary>The pending change this version was approved from, when two-person approval applied.</summary>
    public string? ApprovedFromPendingId { get; set; }
    public string? ApprovedBy { get; set; }

    public static string IdFor(string tenantId, string key, SettingLevel level, string scopeId, int version) =>
        $"{tenantId}|{key}|{(int)level}|{scopeId}|{version}";

    public bool InForceOn(DateTime asOf) => EffectiveFrom <= asOf && (EffectiveTo is null || asOf < EffectiveTo);
}

/// <summary>
/// Where a setting is being resolved: the node at each level that is known.
/// Unknown levels are skipped. The tenant comes from the caller's token.
/// </summary>
public sealed record SettingScope
{
    public static readonly SettingScope Empty = new();

    public string? LineOfBusiness { get; init; }
    /// <summary>The sponsor's group number.</summary>
    public string? Sponsor { get; init; }

    /// <summary>The node id at each level, most specific first. Tenant is always present (empty id).</summary>
    public IReadOnlyList<(SettingLevel Level, string ScopeId)> Chain()
    {
        var chain = new List<(SettingLevel, string)>();
        if (!string.IsNullOrWhiteSpace(Sponsor)) chain.Add((SettingLevel.Sponsor, Sponsor.Trim()));
        if (LinesOfBusiness.Normalize(LineOfBusiness) is { } lob) chain.Add((SettingLevel.LineOfBusiness, lob));
        chain.Add((SettingLevel.Tenant, string.Empty));
        return chain;
    }

    public string CacheKey => $"{LinesOfBusiness.Normalize(LineOfBusiness)}|{Sponsor?.Trim()}";
}

public sealed record SettingTraceEntry(SettingLevel Level, string ScopeId, string? Value, bool Locked, int? Version, string Note);

/// <summary>A resolved value, where it came from, and every level checked (plan-configuration.md §7).</summary>
public sealed record ResolvedSetting
{
    public required string Key { get; init; }
    public required string Value { get; init; }
    public required SettingLevel SourceLevel { get; init; }
    public string? SourceId { get; init; }
    public bool Locked { get; init; }
    /// <summary>The floor or range the value was clamped to, when it was.</summary>
    public string? ClampedBy { get; init; }
    /// <summary>True when tenant-service could not be reached and this is the definition's default (logged by the client).</summary>
    public bool IsFallback { get; init; }
    public IReadOnlyList<SettingTraceEntry> Trace { get; init; } = [];

    public int AsInt() => (int)(SettingJson.AsNumber(Value) ?? 0);
    public decimal AsDecimal() => SettingJson.AsNumber(Value) ?? 0;
    public bool AsBool() => string.Equals(Value, "true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The resolution rules, shared by tenant-service (the API) and the
/// in-process client used in tests, so both answer the same way.
/// </summary>
public static class SettingResolver
{
    /// <summary>The version that holds a level on a date, or null when the level sets nothing then.</summary>
    public static SettingValue? InForce(IEnumerable<SettingValue> versions, DateTime asOf) =>
        versions.Where(v => v.InForceOn(asOf))
            .OrderByDescending(v => v.EffectiveFrom).ThenByDescending(v => v.Version)
            .FirstOrDefault() is { Cleared: false } v ? v : null;

    /// <summary>
    /// Resolves <paramref name="definition"/> for <paramref name="scope"/> on
    /// <paramref name="asOf"/> from the versions stored at the scope's levels:
    /// a locked value at a general level wins over everything more specific;
    /// otherwise the most specific level that sets a value wins; otherwise the
    /// platform default (per line of business). Constraints and floors clamp
    /// the result.
    /// </summary>
    public static ResolvedSetting Resolve(SettingDefinition definition, SettingScope scope, DateTime asOf,
        IEnumerable<SettingValue> versions, bool isFallback = false)
    {
        var date = asOf.Date;
        var byNode = versions.Where(v => v.Key == definition.Key)
            .GroupBy(v => (v.Level, v.ScopeId))
            .ToDictionary(g => g.Key, g => InForce(g, date));

        // General to specific, so a lock can stop the walk.
        var chain = scope.Chain().Where(c => definition.AllowedLevels.Contains(c.Level)).Reverse().ToList();
        var lob = LinesOfBusiness.Normalize(scope.LineOfBusiness);
        var trace = new List<SettingTraceEntry>
        {
            new(SettingLevel.Platform, lob ?? string.Empty, definition.DefaultFor(lob), false, null,
                lob is not null && definition.DefaultsByLineOfBusiness.ContainsKey(lob) ? $"Default for {lob}" : "Default"),
        };

        SettingValue? winner = null;
        string? lockedNote = null;
        foreach (var (level, scopeId) in chain)
        {
            var value = byNode.GetValueOrDefault((level, scopeId));
            if (lockedNote is not null)
            {
                trace.Add(new(level, scopeId, value?.Value, value?.Locked ?? false, value?.Version,
                    value is null ? "Not set" : $"Ignored: {lockedNote}"));
                continue;
            }
            if (value is null)
            {
                trace.Add(new(level, scopeId, null, false, null, "Not set"));
                continue;
            }
            winner = value;
            trace.Add(new(level, scopeId, value.Value, value.Locked, value.Version, value.Locked ? "Set and locked" : "Set"));
            if (value.Locked) lockedNote = $"locked at {level}{(scopeId.Length > 0 ? " " + scopeId : string.Empty)}";
        }

        var raw = winner?.Value ?? definition.DefaultFor(lob);
        var (clamped, clampedBy) = Clamp(definition, raw, lob);
        return new ResolvedSetting
        {
            Key = definition.Key,
            Value = clamped,
            SourceLevel = winner?.Level ?? SettingLevel.Platform,
            SourceId = winner is null ? lob : winner.ScopeId,
            Locked = winner?.Locked ?? false,
            ClampedBy = clampedBy,
            IsFallback = isFallback,
            Trace = trace,
        };
    }

    /// <summary>Applies the range and the floors for the line of business to a numeric value.</summary>
    public static (string Value, string? ClampedBy) Clamp(SettingDefinition definition, string value, string? lineOfBusiness)
    {
        if (SettingJson.AsNumber(value) is not { } n) return (value, null);
        string? by = null;
        var c = definition.Constraints;
        if (c.Min is { } min && n < min) { n = min; by = $"Minimum {SettingJson.Number(min)}"; }
        if (c.Max is { } max && n > max) { n = max; by = $"Maximum {SettingJson.Number(max)}"; }
        foreach (var floor in c.Floors.Where(f => f.LineOfBusiness is null
                                                  || (lineOfBusiness is not null && string.Equals(f.LineOfBusiness, lineOfBusiness, StringComparison.OrdinalIgnoreCase))))
        {
            if (floor.Min is { } fmin && n < fmin) { n = fmin; by = floor.Citation; }
            if (floor.Max is { } fmax && n > fmax) { n = fmax; by = floor.Citation; }
        }
        return by is null ? (value, null) : (SettingJson.Number(n), by);
    }
}

/// <summary>Checks a value before it is stored (plan-configuration.md §4–5).</summary>
public static class SettingValidator
{
    public static IReadOnlyList<string> Validate(SettingDefinition definition, SettingLevel level, string scopeId,
        string? value, bool cleared, string? reason)
    {
        var errors = new List<string>();
        if (level == SettingLevel.Platform || !definition.AllowedLevels.Contains(level))
            errors.Add($"{definition.Key} cannot be set at {level}. Allowed: {string.Join(", ", definition.AllowedLevels.OrderBy(l => l))}.");
        if (level == SettingLevel.LineOfBusiness && LinesOfBusiness.Normalize(scopeId) is null)
            errors.Add($"'{scopeId}' is not a line of business. Use one of {string.Join(", ", LinesOfBusiness.All)}.");
        if (level is not (SettingLevel.Tenant or SettingLevel.Platform) && string.IsNullOrWhiteSpace(scopeId))
            errors.Add($"A {level} value needs the node it applies to.");
        if (definition.ChangeControl != ChangeControl.None && string.IsNullOrWhiteSpace(reason))
            errors.Add($"A reason is required to change {definition.Key}.");
        if (cleared) return errors;

        if (!SettingJson.TryParse(definition.Type, value, out var element, out var typeError))
        {
            errors.Add(typeError!);
            return errors;
        }

        var c = definition.Constraints;
        if (definition.Type == SettingType.Enum && c.AllowedValues is { Count: > 0 } allowed
            && !allowed.Contains(element.GetString() ?? string.Empty, StringComparer.Ordinal))
            errors.Add($"The value must be one of {string.Join(", ", allowed)}.");

        if (SettingJson.AsNumber(value) is { } n)
        {
            if (c.Min is { } min && n < min) errors.Add($"The value must be at least {SettingJson.Number(min)}.");
            if (c.Max is { } max && n > max) errors.Add($"The value must be at most {SettingJson.Number(max)}.");
            // A line-of-business value is checked against that line's floors here;
            // values at other levels are clamped to the member's line at resolution.
            var lob = level == SettingLevel.LineOfBusiness ? LinesOfBusiness.Normalize(scopeId) : null;
            foreach (var floor in c.Floors.Where(f => f.LineOfBusiness is null
                                                      || (lob is not null && string.Equals(f.LineOfBusiness, lob, StringComparison.OrdinalIgnoreCase))))
            {
                if (floor.Min is { } fmin && n < fmin) errors.Add($"The value must be at least {SettingJson.Number(fmin)} ({floor.Citation}).");
                if (floor.Max is { } fmax && n > fmax) errors.Add($"The value must be at most {SettingJson.Number(fmax)} ({floor.Citation}).");
            }
        }
        return errors;
    }

    /// <summary>The permission a write at <paramref name="level"/> needs (plan-configuration.md §9a).</summary>
    public static string WritePermissionFor(SettingLevel level) => level switch
    {
        SettingLevel.Sponsor or SettingLevel.SponsorPolicy or SettingLevel.Class => PlanConfigurationPermissions.Sponsor,
        SettingLevel.Program or SettingLevel.BenefitPlan => PlanConfigurationPermissions.Product,
        _ => PlanConfigurationPermissions.Enterprise,
    };
}

/// <summary>The permission names (also on ChoRolePermissions).</summary>
public static class PlanConfigurationPermissions
{
    public const string Read = "config:read";
    public const string Sponsor = "config:sponsor";
    public const string Product = "config:product";
    public const string Enterprise = "config:enterprise";
    public const string Approve = "config:approve";
}
