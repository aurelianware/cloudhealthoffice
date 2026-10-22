using System.Text.Json.Nodes;

namespace CorrespondenceService.Models;

public sealed class CreateLetterTypeRequest
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public LetterCategory Category { get; set; }
    public LetterAudience Audience { get; set; }
    public bool RequiresApproval { get; set; }
    public string? DocumentType { get; set; }
    public bool IsSignificantCommunication { get; set; }
    public bool IsRequiredMaterial { get; set; }
}

/// <summary>A template version's content: used to create a template, a new version, or edit a draft.</summary>
public sealed class TemplateContentRequest
{
    public string LetterTypeCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public TemplateKind Kind { get; set; } = TemplateKind.PlanAuthored;
    public string? FormNumber { get; set; }
    public string? OmbNumber { get; set; }
    public DateOnly? FormExpiresOn { get; set; }
    public TemplateScope Scope { get; set; } = new();
    public string Language { get; set; } = "en";
    public TemplateFormat Format { get; set; } = TemplateFormat.Standard;
    public TemplateTranslation? Translation { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public List<TemplateBlock> Blocks { get; set; } = new();
    public List<string> RequiredBlocks { get; set; } = new();
    public List<string> Attachments { get; set; } = new();
    public float BodyFontPt { get; set; } = 12f;
    public float MinFontPt { get; set; } = 12f;
    public double? ReadingLevelMax { get; set; }
}

public sealed class RequestLetterSubject
{
    public string Kind { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
}

/// <summary>A request for one letter to one recipient (§6). In step 1 every letter is requested by a user.</summary>
public sealed class LetterRequest
{
    public string LetterTypeCode { get; set; } = string.Empty;
    public List<RequestLetterSubject> Subjects { get; set; } = new();
    public LetterRecipient Recipient { get; set; } = new();

    /// <summary>Merge data the template's Liquid reads. <c>letter</c> and <c>recipient</c> are filled by the service.</summary>
    public JsonObject? Data { get; set; }

    public string Language { get; set; } = "en";
    public TemplateFormat Format { get; set; } = TemplateFormat.Standard;
    public string? LineOfBusiness { get; set; }
    public string? State { get; set; }
    public string? Product { get; set; }
    public string? PlanId { get; set; }

    /// <summary>The date printed on the letter; today (UTC) when omitted.</summary>
    public DateOnly? LetterDate { get; set; }
    public string? RuleRef { get; set; }
    public DateTime? DueBy { get; set; }
    public string? Reason { get; set; }
}

/// <summary>Renders an approved or draft version against sample data; nothing is stored.</summary>
public sealed class PreviewRequest
{
    public LetterRecipient Recipient { get; set; } = new();
    public JsonObject? Data { get; set; }
    public DateOnly? LetterDate { get; set; }
}

public sealed class RetireRequest
{
    public string? Reason { get; set; }
}
