using RiskAdjustmentService.Models;

namespace RiskAdjustmentService.Repositories;

public interface IRiskScoreRepository
{
    Task<MemberRiskScore?> GetByIdAsync(string id);
    Task<MemberRiskScore?> GetByMemberAndYearAsync(string memberId, int measurementYear);
    Task<IEnumerable<MemberRiskScore>> GetByMemberAsync(string memberId);
    Task<IEnumerable<MemberRiskScore>> SearchAsync(
        int? measurementYear,
        string? memberId,
        LineOfBusiness? lineOfBusiness,
        ScoreStatus? status,
        decimal? minScore,
        decimal? maxScore,
        int page,
        int pageSize);
    Task<IEnumerable<MemberRiskScore>> GetByMeasurementYearAsync(
        int measurementYear,
        LineOfBusiness? lineOfBusiness,
        int page,
        int pageSize);
    Task<MeasurementYearSummary> GetMeasurementYearSummaryAsync(
        int measurementYear,
        LineOfBusiness? lineOfBusiness);
    Task<MemberRiskScore> CreateAsync(MemberRiskScore score);
    Task<MemberRiskScore> UpdateAsync(MemberRiskScore score);
    Task DeleteAsync(string id);
}
