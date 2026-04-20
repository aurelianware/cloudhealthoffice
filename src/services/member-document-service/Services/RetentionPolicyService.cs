namespace MemberDocumentService.Services;

public interface IRetentionPolicyService
{
    RetentionPolicyResult ResolvePolicy(string? stateCode, DateTime? coverageTerminationDate, string? requestedPolicyId = null);
}

public sealed class RetentionPolicyResult
{
    public string PolicyId { get; set; } = "DEFAULT-10Y";
    public int YearsToRetain { get; set; } = 10;
    public DateTime RetentionUntilDate { get; set; }
}

public class RetentionPolicyService : IRetentionPolicyService
{
    private const int HipaaMinimumYears = 6;

    private static readonly Dictionary<string, int> StateYears = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TX"] = 10,
        ["CA"] = 10,
        ["NY"] = 10
    };

    public RetentionPolicyResult ResolvePolicy(string? stateCode, DateTime? coverageTerminationDate, string? requestedPolicyId = null)
    {
        var baseDate = coverageTerminationDate?.Date ?? DateTime.UtcNow.Date;

        // Product default is 10 years post coverage termination.
        var years = 10;
        var normalizedState = (stateCode ?? string.Empty).Trim().ToUpperInvariant();
        if (StateYears.TryGetValue(normalizedState, out var stateYears))
        {
            years = stateYears;
        }

        years = Math.Max(years, HipaaMinimumYears);
        var policyId = string.IsNullOrWhiteSpace(requestedPolicyId)
            ? (string.IsNullOrWhiteSpace(normalizedState) ? "DEFAULT-10Y" : $"{normalizedState}-{years}Y")
            : requestedPolicyId.Trim();

        return new RetentionPolicyResult
        {
            PolicyId = policyId,
            YearsToRetain = years,
            RetentionUntilDate = baseDate.AddYears(years)
        };
    }
}
