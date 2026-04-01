namespace AuthorizationService.Models;

public static class AuthorizationsSummaryCalculator
{
    /// <summary>
    /// Calculates turnaround days for an authorization.
    /// When SlaResumedAt is set (RFAI was issued and docs received),
    /// turnaround is measured from SlaResumedAt instead of SubmittedDate.
    /// </summary>
    public static double CalculateTurnaroundDays(Authorization auth)
    {
        if (auth.ReviewedDate == null) return 0;

        var startDate = auth.SlaResumedAt.HasValue && auth.SlaResumedAt.Value > auth.SubmittedDate
            ? auth.SlaResumedAt.Value
            : auth.SubmittedDate;
        return (auth.ReviewedDate.Value - startDate).TotalDays;
    }
}
