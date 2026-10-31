using FhirService.Models;
using FhirService.Services.Dtr;
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

    // Canonical resolution: url[|version], Questionnaire/{id}, a bare id, or {fhirBaseUrl}/Questionnaire/{id}
    Task<Questionnaire?> ResolveQuestionnaireAsync(string reference, string tenantId, string? fhirBaseUrl = null, CancellationToken ct = default);
    Task<bool> QuestionnaireExistsAsync(string questionnaireRef, string tenantId, string? fhirBaseUrl = null, CancellationToken ct = default);

    // $questionnaire-package (DTR 2.2.0)
    Task<QuestionnairePackageResult> GetQuestionnairePackageAsync(
        QuestionnairePackageRequest request, string tenantId, string fhirBaseUrl, CancellationToken ct = default);
}
