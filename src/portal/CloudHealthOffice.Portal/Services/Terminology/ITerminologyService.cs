namespace CloudHealthOffice.Portal.Services;

// ── Terminology Service ─────────────────────────────────────────────────────

public interface ITerminologyService
{
    Task<TermTranslateResult> TranslateAsync(string system, string code, string targetSystem,
        string? tenantId = null, int? age = null, string? gender = null, string? state = null);
    Task<List<TermMapVersionSummary>> GetMapVersionsAsync();
    Task<TermHealthStatus> GetHealthAsync();
}
