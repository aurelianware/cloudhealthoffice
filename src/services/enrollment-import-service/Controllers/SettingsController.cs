using CloudHealthOffice.Infrastructure.Security;
using EnrollmentImportService.Services.Settings;
using Microsoft.AspNetCore.Mvc;

namespace EnrollmentImportService.Controllers;

/// <summary>
/// Enrollment settings by level (docs/architecture/enrollment-operations.md
/// §6): built-in defaults per line of business, overridden per line of
/// business, benefit plan and sponsor; the most specific level wins. Reading
/// needs enrollment:read; changing needs enrollment:configure (TenantAdmin
/// holds it through *:*). Tenant and actor come from the token.
/// </summary>
[ApiController]
[Route("api/v1/enrollment/settings")]
public sealed class SettingsController(
    IEnrollmentSettingsStore store,
    EnrollmentSettingsResolver resolver,
    ICurrentActor actor,
    TimeProvider clock) : ControllerBase
{
    public const string ConfigurePermission = "enrollment:configure";

    /// <summary>The tenant's overrides, and the built-in defaults they override.</summary>
    [HttpGet]
    [RequirePermission("enrollment:read")]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(new
        {
            Defaults = EnrollmentSettingKeys.Ranges.Keys.ToDictionary(k => k, EnrollmentSettingDefaults.ForSetting),
            Overrides = (await store.ListAsync(actor.TenantId, ct)).Select(View),
        });

    /// <summary>What a setting resolves to for a line of business, plan and sponsor, and which level it came from.</summary>
    [HttpGet("resolve")]
    [RequirePermission("enrollment:read")]
    public async Task<IActionResult> Resolve([FromQuery] string setting, [FromQuery] string? lineOfBusiness,
        [FromQuery] string? benefitPlanId, [FromQuery] string? groupNumber, CancellationToken ct)
    {
        if (!EnrollmentSettingKeys.IsKnown(setting)) return UnknownSetting(setting);
        var line = lineOfBusiness is null ? null : LinesOfBusiness.Normalize(lineOfBusiness);
        if (lineOfBusiness is not null && line is null) return UnknownLine(lineOfBusiness);
        return Ok(await resolver.ResolveAsync(actor.TenantId, setting, line, benefitPlanId, groupNumber, ct));
    }

    /// <summary>
    /// Sets values at one level: <c>lineOfBusiness/Commercial</c>,
    /// <c>benefitPlan/{planId}</c> or <c>sponsor/{groupNumber}</c>. A value of
    /// null removes it from that level, so it falls through to the next.
    /// </summary>
    [HttpPut("{level}/{key}")]
    [RequirePermission(ConfigurePermission)]
    public async Task<IActionResult> Put([FromRoute] string level, [FromRoute] string key,
        [FromBody] Dictionary<string, int?> values, CancellationToken ct)
    {
        if (ParseLevel(level) is not { } parsed) return UnknownLevel(level);
        if (Normalize(parsed, key) is not { } normalized) return UnknownLine(key);
        if (values is null || values.Count == 0) return UnprocessableEntity(new { error = "Send at least one setting." });
        foreach (var (name, value) in values)
        {
            if (!EnrollmentSettingKeys.IsKnown(name)) return UnknownSetting(name);
            var (min, max) = EnrollmentSettingKeys.Ranges[name];
            if (value is { } v && (v < min || v > max))
                return UnprocessableEntity(new { error = $"{name} must be between {min} and {max}." });
        }

        var tenantId = actor.TenantId;
        var existing = (await store.GetAsync(tenantId, [new EnrollmentSettingScope(parsed, normalized)], ct)).FirstOrDefault();
        var current = existing ?? new EnrollmentSettingOverride
        {
            Id = EnrollmentSettingOverride.IdFor(tenantId, parsed, normalized),
            TenantId = tenantId,
            Level = parsed,
            Key = normalized,
        };
        foreach (var (name, value) in values)
        {
            if (value is { } v) current.Values[name] = v;
            else current.Values.Remove(name);
        }
        current.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        current.UpdatedBy = actor.UserId;

        if (current.Values.Count == 0)
        {
            await store.DeleteAsync(tenantId, parsed, normalized, ct);
            return NoContent();
        }
        await store.UpsertAsync(current, ct);
        return Ok(View(current));
    }

    [HttpDelete("{level}/{key}")]
    [RequirePermission(ConfigurePermission)]
    public async Task<IActionResult> Delete([FromRoute] string level, [FromRoute] string key, CancellationToken ct)
    {
        if (ParseLevel(level) is not { } parsed) return UnknownLevel(level);
        if (Normalize(parsed, key) is not { } normalized) return UnknownLine(key);
        return await store.DeleteAsync(actor.TenantId, parsed, normalized, ct) ? NoContent() : NotFound();
    }

    private static object View(EnrollmentSettingOverride o) =>
        new { Level = o.Level.ToString(), o.Key, o.Values, o.UpdatedAt, o.UpdatedBy };

    private static EnrollmentSettingLevel? ParseLevel(string level) => level.Trim().ToLowerInvariant() switch
    {
        "lineofbusiness" or "line-of-business" or "lob" => EnrollmentSettingLevel.LineOfBusiness,
        "benefitplan" or "benefit-plan" or "plan" => EnrollmentSettingLevel.BenefitPlan,
        "sponsor" => EnrollmentSettingLevel.Sponsor,
        _ => null,
    };

    /// <summary>Line-of-business keys use sponsor-service's names; plan and sponsor keys are taken as given.</summary>
    private static string? Normalize(EnrollmentSettingLevel level, string key) =>
        level == EnrollmentSettingLevel.LineOfBusiness ? LinesOfBusiness.Normalize(key)
        : string.IsNullOrWhiteSpace(key) ? null : key.Trim();

    private UnprocessableEntityObjectResult UnknownSetting(string name) =>
        UnprocessableEntity(new { error = $"Unknown setting '{name}'. Known: {string.Join(", ", EnrollmentSettingKeys.Ranges.Keys)}." });

    private UnprocessableEntityObjectResult UnknownLevel(string level) =>
        UnprocessableEntity(new { error = $"Unknown level '{level}'. Use lineOfBusiness, benefitPlan or sponsor." });

    private UnprocessableEntityObjectResult UnknownLine(string line) =>
        UnprocessableEntity(new { error = $"Unknown line of business '{line}'. Use Commercial, Medicare, Medicaid, Exchange, TRICARE or VA." });
}
