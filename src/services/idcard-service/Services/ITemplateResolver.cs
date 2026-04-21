using IdCardService.Models;
using IdCardService.Repositories;

namespace IdCardService.Services;

public interface ITemplateResolver
{
    /// <summary>
    /// Resolves the best template for <paramref name="sponsorId"/> +
    /// <paramref name="planId"/>. Falls through sponsor-default → global
    /// default. Returns <c>null</c> only if no global default exists.
    /// </summary>
    Task<IdCardTemplate?> ResolveAsync(
        string tenantId, string? sponsorId, string? planId, string? languageCode, CancellationToken ct = default);

    /// <summary>Returns the global default template for a tenant, or null.</summary>
    Task<IdCardTemplate?> GetGlobalDefaultAsync(string tenantId, CancellationToken ct = default);
}

public class TemplateResolver : ITemplateResolver
{
    private readonly IIdCardTemplateRepository _repository;
    private readonly ILogger<TemplateResolver> _logger;

    public TemplateResolver(IIdCardTemplateRepository repository, ILogger<TemplateResolver> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<IdCardTemplate?> ResolveAsync(
        string tenantId, string? sponsorId, string? planId, string? languageCode, CancellationToken ct = default)
    {
        // 1. Specific (sponsor + plan)
        if (!string.IsNullOrEmpty(sponsorId) && !string.IsNullOrEmpty(planId))
        {
            var specific = await _repository.FindBySponsorAndPlanAsync(tenantId, sponsorId, planId, ct);
            if (SupportsLanguage(specific, languageCode)) return specific;
        }

        // 2. Sponsor-default
        if (!string.IsNullOrEmpty(sponsorId))
        {
            var sponsorDefault = await _repository.FindSponsorDefaultAsync(tenantId, sponsorId, ct);
            if (SupportsLanguage(sponsorDefault, languageCode)) return sponsorDefault;
        }

        // 3. Global default
        var global = await _repository.FindGlobalDefaultAsync(tenantId, ct);
        if (global == null)
        {
            _logger.LogError(
                "No global default ID card template exists for tenant {TenantId}. Seed a global template.",
                Sanitize(tenantId));
        }
        return global;
    }

    public Task<IdCardTemplate?> GetGlobalDefaultAsync(string tenantId, CancellationToken ct = default) =>
        _repository.FindGlobalDefaultAsync(tenantId, ct);

    private static bool SupportsLanguage(IdCardTemplate? template, string? languageCode)
    {
        if (template == null) return false;
        if (string.IsNullOrEmpty(languageCode)) return true;
        // An empty SupportedLanguages list is treated as "no language
        // restriction" — the template's copy is language-neutral (logos,
        // numeric plan codes, etc.). Templates that only target a specific
        // locale must populate SupportedLanguages explicitly.
        if (template.SupportedLanguages.Count == 0) return true;
        return template.SupportedLanguages.Contains(languageCode, StringComparer.OrdinalIgnoreCase);
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}
