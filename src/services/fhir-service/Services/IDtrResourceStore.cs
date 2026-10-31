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
    /// Stores <paramref name="questionnaire"/> in the default library. When a
    /// questionnaire with its id is already there, replaces it only if the seed
    /// is a newer revision (<see cref="DtrService.IsNewerSeed"/>), so a release
    /// that improves a shipped form reaches existing deployments. Synchronous
    /// because <see cref="DtrService"/> seeds the library from its constructor.
    /// </summary>
    void SeedDefaultQuestionnaire(Questionnaire questionnaire);

    /// <summary>The questionnaire stored under exactly this tenant and id, or null.</summary>
    Task<Questionnaire?> FindQuestionnaireAsync(string tenantId, string id, CancellationToken ct = default);

    /// <summary>Insert only; false if this tenant already has the id. Never overwrites.</summary>
    Task<bool> TryInsertQuestionnaireAsync(Questionnaire questionnaire, string tenantId, CancellationToken ct = default);

    /// <summary>Replace only the expected stored version; false on a concurrent change or missing record.</summary>
    Task<bool> TryReplaceQuestionnaireAsync(Questionnaire questionnaire, string tenantId, string? expectedVersion, CancellationToken ct = default);

    /// <summary>Own tenant plus un-overridden shared defaults, selected by stored tenant keys.</summary>
    Task<IReadOnlyList<Questionnaire>> ListVisibleQuestionnairesAsync(string tenantId, CancellationToken ct = default);

    /// <summary>Responses belonging to exactly this tenant.</summary>
    Task<IReadOnlyList<QuestionnaireResponse>> ListResponsesAsync(string tenantId, CancellationToken ct = default);

    /// <summary>The response stored under exactly this tenant and id, or null.</summary>
    Task<QuestionnaireResponse?> FindResponseAsync(string tenantId, string id, CancellationToken ct = default);

    /// <summary>Stores a newly submitted response under this tenant.</summary>
    Task InsertResponseAsync(QuestionnaireResponse response, string tenantId, CancellationToken ct = default);
}
