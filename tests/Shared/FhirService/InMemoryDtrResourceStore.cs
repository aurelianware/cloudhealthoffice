// Test double for fhir-service's IDtrResourceStore. Linked into each test
// project that builds a DtrService without a database: FhirService.Tests and
// SmartAuth.Tests through the tests/Shared/FhirService/*.cs glob,
// Cms0057Acceptance.Tests by name. Never registered by fhir-service itself.
using System.Collections.Concurrent;
using FhirService.Services;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace CloudHealthOffice.Testing.FhirService;

/// <summary>
/// In-process <see cref="IDtrResourceStore"/> with the semantics of
/// <see cref="MongoDtrResourceStore"/>: one entry per (tenant id, resource id),
/// conditional writes never overwrite a newer version, seeds never overwrite, and each read returns a fresh
/// copy parsed from the stored FHIR JSON rather than the object that was written.
/// </summary>
public sealed class InMemoryDtrResourceStore : IDtrResourceStore
{
    private static readonly FhirJsonSerializer Serializer = new(new SerializerSettings { Pretty = false });
    private static readonly FhirJsonParser Parser = new(new ParserSettings { PermissiveParsing = true });

    private readonly ConcurrentDictionary<(string TenantId, string Id), string> _questionnaires = new();
    private readonly ConcurrentDictionary<(string TenantId, string Id), string> _responses = new();

    public void SeedDefaultQuestionnaire(Questionnaire questionnaire)
        => _questionnaires.TryAdd(
            (DtrService.DefaultLibraryTenant, questionnaire.Id), Serializer.SerializeToString(questionnaire));

    public System.Threading.Tasks.Task<Questionnaire?> FindQuestionnaireAsync(string tenantId, string id, CancellationToken ct = default)
        => System.Threading.Tasks.Task.FromResult(_questionnaires.TryGetValue((tenantId, id), out var json)
            ? Parser.Parse<Questionnaire>(json)
            : null);

    public System.Threading.Tasks.Task<bool> TryInsertQuestionnaireAsync(Questionnaire questionnaire, string tenantId, CancellationToken ct = default)
        => System.Threading.Tasks.Task.FromResult(_questionnaires.TryAdd(
            (tenantId, questionnaire.Id), Serializer.SerializeToString(questionnaire)));

    public System.Threading.Tasks.Task<bool> TryReplaceQuestionnaireAsync(
        Questionnaire questionnaire, string tenantId, string? expectedVersion, CancellationToken ct = default)
    {
        var key = (tenantId, questionnaire.Id);
        var saved = _questionnaires.TryGetValue(key, out var json)
            && Parser.Parse<Questionnaire>(json).Meta?.VersionId == expectedVersion
            && _questionnaires.TryUpdate(key, Serializer.SerializeToString(questionnaire), json);
        return System.Threading.Tasks.Task.FromResult(saved);
    }

    public System.Threading.Tasks.Task<IReadOnlyList<Questionnaire>> ListVisibleQuestionnairesAsync(string tenantId, CancellationToken ct = default)
    {
        IReadOnlyList<Questionnaire> items = _questionnaires
            .Where(kv => kv.Key.TenantId == tenantId || kv.Key.TenantId == DtrService.DefaultLibraryTenant)
            .GroupBy(kv => kv.Key.Id, StringComparer.Ordinal)
            .Select(g => g.FirstOrDefault(kv => kv.Key.TenantId == tenantId).Value ?? g.First().Value)
            .Select(json => Parser.Parse<Questionnaire>(json)).ToList();
        return System.Threading.Tasks.Task.FromResult(items);
    }

    public System.Threading.Tasks.Task<IReadOnlyList<QuestionnaireResponse>> ListResponsesAsync(string tenantId, CancellationToken ct = default)
    {
        IReadOnlyList<QuestionnaireResponse> items = _responses.Where(kv => kv.Key.TenantId == tenantId)
            .Select(kv => Parser.Parse<QuestionnaireResponse>(kv.Value)).ToList();
        return System.Threading.Tasks.Task.FromResult(items);
    }

    public System.Threading.Tasks.Task<QuestionnaireResponse?> FindResponseAsync(string tenantId, string id, CancellationToken ct = default)
        => System.Threading.Tasks.Task.FromResult(_responses.TryGetValue((tenantId, id), out var json)
            ? Parser.Parse<QuestionnaireResponse>(json)
            : null);

    public System.Threading.Tasks.Task InsertResponseAsync(QuestionnaireResponse response, string tenantId, CancellationToken ct = default)
    {
        _responses[(tenantId, response.Id)] = Serializer.SerializeToString(response);
        return System.Threading.Tasks.Task.CompletedTask;
    }
}
