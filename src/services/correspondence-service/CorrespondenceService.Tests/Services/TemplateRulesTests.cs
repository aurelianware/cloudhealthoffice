using CorrespondenceService.Models;
using CorrespondenceService.Services;
using CorrespondenceService.Tests.Support;

namespace CorrespondenceService.Tests.Services;

public class TemplateRulesTests
{
    private static readonly string[] Known = [Builders.LetterTypeCode, "NOTICE_OF_AVAILABILITY"];

    private static LetterTemplate Draft(TemplateKind kind = TemplateKind.PlanAuthored) =>
        Builders.Template(status: TemplateStatus.Draft, kind: kind);

    [Fact]
    public void A_valid_draft_passes() =>
        TemplateRules.ValidateDraft(Draft(), Known, null).Should().BeEmpty();

    [Fact]
    public void Font_floor_is_12_pt_and_18_for_large_print()
    {
        var t = Draft();
        t.MinFontPt = 10;
        TemplateRules.ValidateDraft(t, Known, null).Should().Contain(e => e.Contains("below 12 pt"));

        var large = Draft();
        large.Format = TemplateFormat.LargePrint;
        TemplateRules.ValidateDraft(large, Known, null).Should().Contain(e => e.Contains("below 18 pt"));

        var block = Draft();
        block.Blocks[0].FontSize = 9;
        TemplateRules.ValidateDraft(block, Known, null).Should().Contain(e => e.Contains("Block 'date' is 9 pt"));
    }

    [Fact]
    public void Rejects_bad_ids_bad_liquid_missing_required_blocks_and_unknown_attachments()
    {
        var t = Draft();
        t.Blocks.Add(new TemplateBlock { Id = "bad id", Text = "x" });
        t.Blocks.Add(new TemplateBlock { Id = "broken", Text = "{% if x %}" });
        t.Blocks.Add(new TemplateBlock { Id = "body", Text = "dup" });
        t.RequiredBlocks.Add("taglines");
        t.Attachments.AddRange(["NOPE", Builders.LetterTypeCode]);

        var errors = TemplateRules.ValidateDraft(t, Known, null);
        errors.Should().Contain(e => e.Contains("'bad id'"));
        errors.Should().Contain(e => e.Contains("'broken' is not valid Liquid"));
        errors.Should().Contain(e => e.Contains("'body' is used twice"));
        errors.Should().Contain(e => e.Contains("'taglines' is not in the template"));
        errors.Should().Contain(e => e.Contains("'NOPE' does not exist"));
        errors.Should().Contain(e => e.Contains("cannot attach its own letter type"));
    }

    [Fact]
    public void Standardized_templates_need_a_form_number_and_keep_locked_text()
    {
        var previous = Builders.Template(kind: TemplateKind.Standardized);
        var next = Builders.Template(version: 2, status: TemplateStatus.Draft, kind: TemplateKind.Standardized);
        TemplateRules.ValidateDraft(next, Known, previous).Should().BeEmpty();

        next.Blocks.Single(b => b.Id == "rights").Text = "You may ask for a hearing.";
        TemplateRules.ValidateDraft(next, Known, previous).Should().ContainSingle(e => e.Contains("Locked block 'rights'"));

        next.FormNumber = null;
        TemplateRules.ValidateDraft(next, Known, previous).Should().Contain(e => e.Contains("form number"));
    }

    [Fact]
    public void Plan_authored_templates_may_reword_locked_blocks_between_versions()
    {
        var previous = Builders.Template();
        var next = Builders.Template(version: 2, status: TemplateStatus.Draft);
        next.Blocks.Single(b => b.Id == "rights").Text = "Reworded.";
        TemplateRules.ValidateDraft(next, Known, previous).Should().BeEmpty();
    }

    [Fact]
    public void Approval_needs_a_draft_a_second_person_and_a_translator_for_other_languages()
    {
        var t = Draft();
        TemplateRules.ValidateApproval(t, "author-1", requireSeparateApprover: true).Should().ContainSingle(e => e.Contains("someone other than its author"));
        TemplateRules.ValidateApproval(t, "author-1", requireSeparateApprover: false).Should().BeEmpty();
        TemplateRules.ValidateApproval(t, "approver-1", true).Should().BeEmpty();

        var spanish = Builders.Template(status: TemplateStatus.Draft, language: "es");
        spanish.Translation = null;
        TemplateRules.ValidateApproval(spanish, "approver-1", true).Should().ContainSingle(e => e.Contains("translator"));

        TemplateRules.ValidateApproval(Builders.Template(), "approver-1", true).Should().ContainSingle(e => e.Contains("Only a draft"));
    }

    [Fact]
    public void Approval_stamps_locked_block_hashes()
    {
        var t = Draft();
        TemplateRules.StampApproval(t, "approver-1", DateTime.UtcNow);
        t.Status.Should().Be(TemplateStatus.Approved);
        t.Blocks.Single(b => b.Id == "rights").ApprovedTextHash.Should().Be(LetterRenderer.Sha256("You have the right to ask for a State fair hearing."));
        t.Blocks.Where(b => !b.Locked).Should().OnlyContain(b => b.ApprovedTextHash == null);
    }
}
