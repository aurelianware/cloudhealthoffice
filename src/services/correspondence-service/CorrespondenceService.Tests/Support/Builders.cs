using System.Text.Json.Nodes;
using CorrespondenceService.Models;
using CorrespondenceService.Services;

namespace CorrespondenceService.Tests.Support;

/// <summary>Test data: an appeal acknowledgment letter type and template.</summary>
public static class Builders
{
    public const string Tenant = "tenant-a";
    public const string LetterTypeCode = "APPEAL_ACK";

    public static readonly DateOnly Today = new(2026, 10, 21);

    public static LetterType LetterType(string code = LetterTypeCode, string tenant = Tenant) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        TenantId = tenant,
        Code = code,
        Name = code == LetterTypeCode ? "Appeal acknowledgment" : code,
        Category = LetterCategory.Appeal,
        Audience = LetterAudience.Member,
        CreatedAt = DateTime.UtcNow,
        CreatedBy = "author-1",
    };

    public static LetterTemplate Template(
        string templateId = "TPL-ACK",
        int version = 1,
        TemplateStatus status = TemplateStatus.Approved,
        TemplateScope? scope = null,
        string language = "en",
        DateOnly? from = null,
        DateOnly? to = null,
        string letterType = LetterTypeCode,
        TemplateKind kind = TemplateKind.PlanAuthored)
    {
        var t = new LetterTemplate
        {
            Id = LetterTemplate.MakeId(templateId, version),
            TenantId = Tenant,
            TemplateId = templateId,
            Version = version,
            LetterTypeCode = letterType,
            Name = "Appeal acknowledgment",
            Kind = kind,
            FormNumber = kind is TemplateKind.Standardized or TemplateKind.StatePrescribed ? "CMS-10003" : null,
            Status = TemplateStatus.Draft,
            Scope = scope ?? new TemplateScope(),
            Language = language,
            Translation = language == "en" ? null : new TemplateTranslation { TranslatorId = "translator-1", ReviewedAt = DateTime.UtcNow },
            EffectiveFrom = from ?? new DateOnly(2026, 1, 1),
            EffectiveTo = to,
            Blocks =
            [
                new() { Id = "date", Kind = TemplateBlockKind.Paragraph, Text = "{{ letter.date }}" },
                new() { Id = "address", Kind = TemplateBlockKind.AddressBlock },
                new() { Id = "title", Kind = TemplateBlockKind.Heading, Text = "We received your appeal" },
                new() { Id = "body", Kind = TemplateBlockKind.Paragraph, Text = "Dear {{ member.name }}, we received your appeal {{ appeal.id }} on {{ appeal.receivedDate }}." },
                new() { Id = "rights", Kind = TemplateBlockKind.Paragraph, Locked = true, Text = "You have the right to ask for a State fair hearing." },
                new() { Id = "signature", Kind = TemplateBlockKind.Signature, Text = "Member Services" },
            ],
            RequiredBlocks = ["body", "rights"],
            CreatedBy = "author-1",
            CreatedAt = DateTime.UtcNow,
        };
        if (status == TemplateStatus.Approved)
            TemplateRules.StampApproval(t, "approver-1", DateTime.UtcNow);
        else
            t.Status = status;
        return t;
    }

    public static LetterRecipient Recipient(string? line1 = "100 Main St") => new()
    {
        Party = LetterAudience.Member,
        Name = "Maria Lopez",
        AddressLine1 = line1,
        City = "Dallas",
        State = "TX",
        PostalCode = "75201",
        AddressSource = "MemberRecord",
    };

    public static JsonObject Data() => new()
    {
        ["member"] = new JsonObject { ["name"] = "Maria Lopez" },
        ["appeal"] = new JsonObject { ["id"] = "APL-100", ["receivedDate"] = "October 20, 2026" },
    };

    public static LetterRequest Request(string subjectKind = "Appeal", string subjectId = "APL-100") => new()
    {
        LetterTypeCode = LetterTypeCode,
        Subjects = [new() { Kind = "Member", Id = "M-1" }, new() { Kind = subjectKind, Id = subjectId }],
        Recipient = Recipient(),
        Data = Data(),
        LetterDate = Today,
        State = "TX",
        LineOfBusiness = "Medicaid",
        Reason = "test",
    };
}
