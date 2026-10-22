using CorrespondenceService.Models;
using CorrespondenceService.Repositories;

namespace CorrespondenceService.Tests.Support;

/// <summary>A tenant-scoped in-memory repository for pipeline and composer tests.</summary>
public sealed class InMemoryCorrespondenceRepository : ICorrespondenceRepository
{
    private readonly object _gate = new();
    public List<LetterType> LetterTypes { get; } = new();
    public List<LetterTemplate> Templates { get; } = new();
    public List<Letter> Letters { get; } = new();

    public void Reset()
    {
        lock (_gate) { LetterTypes.Clear(); Templates.Clear(); Letters.Clear(); }
    }

    public Task<LetterType?> GetLetterTypeAsync(string tenantId, string code, CancellationToken ct = default)
    { lock (_gate) return Task.FromResult(LetterTypes.FirstOrDefault(x => x.TenantId == tenantId && x.Code == code)); }

    public Task<IReadOnlyList<LetterType>> ListLetterTypesAsync(string tenantId, CancellationToken ct = default)
    { lock (_gate) return Task.FromResult<IReadOnlyList<LetterType>>(LetterTypes.Where(x => x.TenantId == tenantId).ToList()); }

    public Task<bool> TryCreateLetterTypeAsync(LetterType letterType, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (LetterTypes.Any(x => x.TenantId == letterType.TenantId && x.Code == letterType.Code)) return Task.FromResult(false);
            LetterTypes.Add(letterType);
            return Task.FromResult(true);
        }
    }

    public Task<LetterTemplate?> GetTemplateVersionAsync(string tenantId, string templateId, int version, CancellationToken ct = default)
    { lock (_gate) return Task.FromResult(Copy(Templates.FirstOrDefault(x => x.TenantId == tenantId && x.TemplateId == templateId && x.Version == version))); }

    public Task<IReadOnlyList<LetterTemplate>> ListTemplateVersionsAsync(string tenantId, string templateId, CancellationToken ct = default)
    { lock (_gate) return Task.FromResult<IReadOnlyList<LetterTemplate>>(Templates.Where(x => x.TenantId == tenantId && x.TemplateId == templateId).OrderBy(x => x.Version).Select(x => Copy(x)!).ToList()); }

    public Task<IReadOnlyList<LetterTemplate>> ListTemplatesAsync(string tenantId, string? letterTypeCode, CancellationToken ct = default)
    { lock (_gate) return Task.FromResult<IReadOnlyList<LetterTemplate>>(Templates.Where(x => x.TenantId == tenantId && (letterTypeCode == null || x.LetterTypeCode == letterTypeCode)).Select(x => Copy(x)!).ToList()); }

    public Task<IReadOnlyList<LetterTemplate>> ListApprovedAsync(string tenantId, string letterTypeCode, string language, TemplateFormat format, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<LetterTemplate>>(Templates.Where(x => x.TenantId == tenantId && x.LetterTypeCode == letterTypeCode
                && x.Status == TemplateStatus.Approved && x.Language == language && x.Format == format).Select(x => Copy(x)!).ToList());
    }

    public Task<bool> TryCreateTemplateVersionAsync(LetterTemplate template, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (Templates.Any(x => x.TenantId == template.TenantId && x.TemplateId == template.TemplateId && x.Version == template.Version)) return Task.FromResult(false);
            Templates.Add(Copy(template)!);
            return Task.FromResult(true);
        }
    }

    public Task SaveTemplateVersionAsync(LetterTemplate template, long expectedRevision, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var i = Templates.FindIndex(x => x.TenantId == template.TenantId && x.Id == template.Id && x.Revision == expectedRevision);
            if (i < 0) throw new CorrespondenceConcurrencyException("changed");
            template.Revision = expectedRevision + 1;
            Templates[i] = Copy(template)!;
            return Task.CompletedTask;
        }
    }

    public Task CreateLetterAsync(Letter letter, CancellationToken ct = default)
    { lock (_gate) Letters.Add(letter); return Task.CompletedTask; }

    public Task<Letter?> GetLetterAsync(string tenantId, string letterId, CancellationToken ct = default)
    { lock (_gate) return Task.FromResult(Letters.FirstOrDefault(x => x.TenantId == tenantId && x.Id == letterId)); }

    public Task<IReadOnlyList<Letter>> ListLettersForSubjectAsync(string tenantId, string subjectKind, string subjectId, int limit, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<Letter>>(Letters.Where(x => x.TenantId == tenantId && x.Subjects.Any(s => s.Kind == subjectKind && s.Id == subjectId))
                .OrderByDescending(x => x.CreatedAt).Take(limit).ToList());
    }

    public Task AppendEventAsync(string tenantId, string letterId, LetterEvent letterEvent, CancellationToken ct = default)
    {
        lock (_gate) Letters.FirstOrDefault(x => x.TenantId == tenantId && x.Id == letterId)?.Events.Add(letterEvent);
        return Task.CompletedTask;
    }

    private static LetterTemplate? Copy(LetterTemplate? t) => t is null ? null
        : System.Text.Json.JsonSerializer.Deserialize<LetterTemplate>(System.Text.Json.JsonSerializer.Serialize(t));
}
