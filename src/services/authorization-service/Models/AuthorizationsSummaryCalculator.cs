namespace AuthorizationService.Models;

public static class AuthorizationsSummaryCalculator
{
    /// <summary>
    /// Calculates turnaround days for an authorization.
    /// Turnaround is always measured from receipt, including time waiting for documents.
    /// </summary>
    public static double CalculateTurnaroundDays(Authorization auth)
    {
        if (auth.ReviewedDate == null) return 0;

        var startDate = Services.AuthorizationDeadlinePolicy.Receipt(auth);
        return (auth.ReviewedDate.Value - startDate).TotalDays;
    }
}
