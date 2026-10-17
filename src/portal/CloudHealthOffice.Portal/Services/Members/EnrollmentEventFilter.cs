namespace CloudHealthOffice.Portal.Services;

public sealed record EnrollmentEventFilter(
    string? Type = null,
    DateTime? From = null,
    DateTime? To = null,
    int Limit = 50,
    string? ContinuationToken = null);
