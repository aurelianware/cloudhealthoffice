namespace CloudHealthOffice.Portal.Services;

// Shapes returned by correspondence-service (api/v1/correspondence). Enums
// arrive as strings and are kept as strings here.

public class CorrespondenceSummary
{
    public long RenderedToday { get; set; }
    public long RenderedLast7Days { get; set; }
    public long CheckFailedLast7Days { get; set; }
    public DateTime AsOf { get; set; }
}

public class CorrespondenceLetter
{
    public string Id { get; set; } = string.Empty;
    public string LetterTypeCode { get; set; } = string.Empty;
    public string TemplateId { get; set; } = string.Empty;
    public int TemplateVersion { get; set; }
    public string Language { get; set; } = string.Empty;
    public List<CorrespondenceSubject> Subjects { get; set; } = new();
    public CorrespondenceRecipient Recipient { get; set; } = new();
    public DateOnly LetterDate { get; set; }
    public string? DocumentId { get; set; }
    public int PageCount { get; set; }
    public string Status { get; set; } = string.Empty;
    public List<CorrespondenceCheck> Checks { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}

public class CorrespondenceSubject
{
    public string Kind { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
}

public class CorrespondenceRecipient
{
    public string Party { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public class CorrespondenceCheck
{
    public string Name { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string? Detail { get; set; }
}

public class CorrespondenceLetterType
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
}

public class CorrespondenceTemplate
{
    public string TemplateId { get; set; } = string.Empty;
    public int Version { get; set; }
    public string LetterTypeCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? FormNumber { get; set; }
    public string Language { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public DateTime? ApprovedAt { get; set; }
}
