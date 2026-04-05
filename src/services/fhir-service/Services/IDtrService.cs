using FhirService.Models;
using Hl7.Fhir.Model;

namespace FhirService.Services;

public interface IDtrService
{
    // Questionnaire CRUD
    Task<Questionnaire?> GetQuestionnaireAsync(string id, string tenantId, CancellationToken ct = default);
    Task<(IReadOnlyList<Questionnaire> Items, int Total)> SearchQuestionnairesAsync(
        QuestionnaireSearchParams search, string tenantId, CancellationToken ct = default);
    Task<Questionnaire> CreateQuestionnaireAsync(Questionnaire questionnaire, string tenantId, CancellationToken ct = default);
    Task<Questionnaire?> UpdateQuestionnaireAsync(string id, Questionnaire questionnaire, string tenantId, CancellationToken ct = default);

    // QuestionnaireResponse
    Task<QuestionnaireResponse?> GetResponseAsync(string id, string tenantId, CancellationToken ct = default);
    Task<(IReadOnlyList<QuestionnaireResponse> Items, int Total)> SearchResponsesAsync(
        QuestionnaireResponseSearchParams search, string tenantId, CancellationToken ct = default);
    Task<QuestionnaireResponse> SubmitResponseAsync(QuestionnaireResponse response, string tenantId, CancellationToken ct = default);

    // Validation
    bool QuestionnaireExists(string questionnaireRef, string tenantId);

    // $questionnaire-package
    Task<Bundle?> GetQuestionnairePackageAsync(string questionnaireId, string? patientId, string tenantId, CancellationToken ct = default);
}
