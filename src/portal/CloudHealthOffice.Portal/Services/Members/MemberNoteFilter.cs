namespace CloudHealthOffice.Portal.Services;

public sealed record MemberNoteFilter(string? Category, int Limit, string? ContinuationToken);
