using AuthorizationService.Models;

namespace AuthorizationService.Services;

/// <summary>
/// Receipt-based CHO operational target for medical prior authorizations.
/// These targets do not assert a universal statutory ceiling for every LOB or drug.
/// No pause, restart, or extension is granted by this policy.
/// </summary>
public static class AuthorizationDeadlinePolicy
{
    public const string Version = "cho-receipt-v1";

    public static DateTime DueAt(DateTime receivedAt, string? levelOfService) =>
        Utc(receivedAt).AddHours(IsExpedited(levelOfService) ? 72 : 168);

    public static bool IsExpedited(string? levelOfService) =>
        string.Equals(levelOfService, "U", StringComparison.OrdinalIgnoreCase);

    public static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Local
        ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    public static void Initialize(Authorization authorization)
    {
        authorization.DecisionReceivedAt = Utc(authorization.SubmittedDate);
        authorization.DecisionDueAt = DueAt(authorization.SubmittedDate, authorization.LevelOfService);
        authorization.DecisionDeadlinePolicy = Version;
    }

    public static void InitializeMissing(Authorization authorization)
    {
        authorization.DecisionReceivedAt ??= Utc(authorization.SubmittedDate);
        authorization.DecisionDueAt ??= DueAt(authorization.DecisionReceivedAt.Value, authorization.LevelOfService);
        authorization.DecisionDeadlinePolicy ??= Version;
    }

    public static DateTime Receipt(Authorization authorization) =>
        authorization.DecisionReceivedAt ?? Utc(authorization.SubmittedDate);

    public static DateTime Deadline(Authorization authorization) =>
        authorization.DecisionDueAt ?? DueAt(Receipt(authorization), authorization.LevelOfService);
}
