using Hl7.Fhir.Model;
using Task = System.Threading.Tasks.Task;

namespace FhirService.Services;

/// <summary>
/// Durable storage behind <see cref="DtrService"/>: DTR Questionnaires and the
/// QuestionnaireResponses submitted against them, each keyed by
/// (tenant id, resource id). The default questionnaire library every tenant sees
/// is stored under tenant id <see cref="DtrService.DefaultLibraryTenant"/>.
///
/// fhir-service registers <see cref="MongoDtrResourceStore"/> (ADR 017: MongoDB
/// in every environment). There is no in-process implementation in the service.
/// </summary>
public interface IDtrResourceStore
{
    /// <summary>
    /// Stores <paramref name="questionnaire"/> in the default library unless a
    /// questionnaire with its id is already there. Synchronous because
    /// <see cref="DtrService"/> seeds the library from its constructor.
    /// </summary>
    void SeedDefaultQuestionnaire(Questionnaire questionnaire);

    /// <summary>The questionnaire stored under exactly this tenant and id, or null.</summary>
    Task<Questionnaire?> FindQuestionnaireAsync(string tenantId, string id, CancellationToken ct = default);

    /// <summary>Inserts or replaces the questionnaire stored under this tenant and its id.</summary>
    Task UpsertQuestionnaireAsync(Questionnaire questionnaire, string tenantId, CancellationToken ct = default);

    /// <summary>The response stored under exactly this tenant and id, or null.</summary>
    Task<QuestionnaireResponse?> FindResponseAsync(string tenantId, string id, CancellationToken ct = default);

    /// <summary>Stores a newly submitted response under this tenant.</summary>
    Task InsertResponseAsync(QuestionnaireResponse response, string tenantId, CancellationToken ct = default);
}
