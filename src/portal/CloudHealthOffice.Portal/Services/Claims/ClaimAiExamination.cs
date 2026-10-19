namespace CloudHealthOffice.Portal.Services;

public class ClaimAiExamination
{
    public string RecommendedDisposition { get; set; } = string.Empty;
    public double ConfidenceScore { get; set; }
    public string? Rationale { get; set; }
    public List<string> PolicyCitations { get; set; } = new();
    public string? ModelId { get; set; }
    public string? PromptVersion { get; set; }
    public DateTime GeneratedAt { get; set; }
    public string? ExaminerAgreement { get; set; }
    public DateTime? ExaminerActedAt { get; set; }
    public string? ExaminerUserId { get; set; }
}
