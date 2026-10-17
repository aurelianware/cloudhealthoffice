namespace ClaimsExaminerService.Services.Examiner;

/// <summary>
/// The dispositions the examiner may recommend (the <c>recommend_disposition</c>
/// tool's enum, and the values claims-service accepts on
/// <c>PUT /api/claims/{id}/ai-examination</c>).
/// </summary>
public static class AiDispositions
{
    public const string Approve = "Approve";
    public const string Deny = "Deny";
    public const string RequestInfo = "RequestInfo";
    public const string EscalateToHuman = "EscalateToHuman";

    public static readonly IReadOnlyList<string> All = [Approve, Deny, RequestInfo, EscalateToHuman];

    /// <summary>The known disposition <paramref name="value"/> names (case-insensitive); anything else escalates to a human.</summary>
    public static string Normalize(string? value) =>
        All.FirstOrDefault(d => string.Equals(d, value, StringComparison.OrdinalIgnoreCase)) ?? EscalateToHuman;
}

/// <summary>Pend codes the examiner acts on.</summary>
public static class ExaminerPendCodes
{
    /// <summary>NCCI bundling pends: the only pends in scope for v1.</summary>
    public const string Ncci = "NCCI";
}

/// <summary>
/// A dependency could not answer: the message must be retried, not skipped.
/// The dependency is claims-service, or Anthropic when it is rate limited,
/// overloaded, behind an open circuit or timed out. The consumer retries the
/// message with backoff and dead-letters it after the last attempt, so an
/// outage never silently drops an AI examination. Nor does it turn one into
/// a permanent EscalateToHuman.
/// </summary>
public sealed class ExaminerTransientException : Exception
{
    public ExaminerTransientException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
