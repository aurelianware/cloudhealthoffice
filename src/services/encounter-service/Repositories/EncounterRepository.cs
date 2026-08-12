using EncounterService.Models;

namespace EncounterService.Repositories;

public interface IEncounterRepository
{
    Task<Encounter?> GetByIdAsync(string id);
    Task<Encounter?> GetByControlNumberAsync(string controlNumber);
    Task<IEnumerable<Encounter>> SearchAsync(
        string? memberId,
        string? payerId,
        string? batchId,
        DateTime? serviceDateFrom,
        DateTime? serviceDateTo,
        EncounterStatus? status,
        SubmissionType? submissionType,
        LineOfBusiness? lineOfBusiness,
        int page,
        int pageSize);
    Task<IEnumerable<Encounter>> GetPendingByPayerAsync(
        string payerId,
        LineOfBusiness? lineOfBusiness,
        EncounterType? encounterType,
        int maxCount);
    Task<EncounterSummary> GetSummaryAsync(DateTime from, DateTime to, string? payerId);
    Task<Encounter> CreateAsync(Encounter encounter);
    Task<Encounter> UpdateAsync(Encounter encounter);
    Task DeleteAsync(string id);
}
