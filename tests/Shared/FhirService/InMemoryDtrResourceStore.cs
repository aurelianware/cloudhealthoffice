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
/// an upsert replaces, a seed never overwrites, and each read returns a fresh
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

    public System.Threading.Tasks.Task UpsertQuestionnaireAsync(Questionnaire questionnaire, string tenantId, CancellationToken ct = default)
    {
        _questionnaires[(tenantId, questionnaire.Id)] = Serializer.SerializeToString(questionnaire);
        return System.Threading.Tasks.Task.CompletedTask;
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
