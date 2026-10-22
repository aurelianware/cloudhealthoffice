using CorrespondenceService.Models;
using CorrespondenceService.Services;
using CorrespondenceService.Tests.Support;
using UglyToad.PdfPig;

namespace CorrespondenceService.Tests.Services;

public class LetterRendererTests
{
    private static LetterRenderResult Render(LetterTemplate template, LetterRecipient? recipient = null,
        IReadOnlyList<LetterTemplate>? attachments = null, IReadOnlyList<string>? missingAttachments = null,
        System.Text.Json.Nodes.JsonObject? data = null, DateOnly? date = null)
    {
        var model = MergeFields.FromJson(data ?? Builders.Data());
        model["letter"] = new Dictionary<string, object?> { ["date"] = "October 21, 2026", ["id"] = "LTR-TEST" };
        return LetterRenderer.Render(new LetterRenderInput(template, attachments ?? [], missingAttachments ?? [], model,
            recipient ?? Builders.Recipient(), "LTR-TEST", date ?? Builders.Today, DateTime.UtcNow));
    }

    private static string PdfText(byte[] pdf, out int pages)
    {
        using var doc = PdfDocument.Open(pdf);
        pages = doc.NumberOfPages;
        return string.Join("\n", doc.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text))));
    }

    [Fact]
    public void Renders_a_letter_that_passes_every_check()
    {
        var result = Render(Builders.Template());

        result.Checks.Should().OnlyContain(c => c.Passed, "{0}", string.Join("; ", result.Checks.Where(c => !c.Passed).Select(c => c.Detail)));
        result.Checks.Select(c => c.Name).Should().Contain([LetterRenderer.LockedTextUnchanged, LetterRenderer.RequiredBlocksPresent,
            LetterRenderer.AttachmentsPresent, LetterRenderer.MinimumFontSize, LetterRenderer.MergeFieldsResolved,
            LetterRenderer.RecipientAddressComplete, LetterRenderer.FormNotExpired]);

        var text = PdfText(result.Pdf, out var pages);
        pages.Should().Be(1);
        result.PageCount.Should().Be(1);
        text.Should().Contain("Maria Lopez").And.Contain("Dallas, TX 75201").And.Contain("APL-100")
            .And.Contain("State fair hearing").And.Contain("Letter LTR-TEST").And.Contain("Page 1 of 1");
    }

    [Fact]
    public void Attachments_start_on_a_new_page()
    {
        var noa = Builders.Template("TPL-NOA", letterType: "NOTICE_OF_AVAILABILITY");
        noa.Blocks = [new() { Id = "noa", Text = "Free language assistance is available." }];
        noa.RequiredBlocks = ["noa"];

        var result = Render(Builders.Template(), attachments: [noa]);
        result.Passed.Should().BeTrue();
        result.PageCount.Should().Be(2);
        using var doc = PdfDocument.Open(result.Pdf);
        string.Join(" ", doc.GetPage(2).GetWords().Select(w => w.Text)).Should().Contain("language assistance").And.Contain("Page 2 of 2");
    }

    [Fact]
    public void A_long_letter_flows_onto_more_pages()
    {
        var t = Builders.Template();
        t.Blocks.InsertRange(4, Enumerable.Range(0, 40).Select(i => new TemplateBlock { Id = $"p{i}", Text = $"Paragraph {i}. " + string.Concat(Enumerable.Repeat("More words here. ", 12)) }));
        var result = Render(t);
        result.Passed.Should().BeTrue();
        result.PageCount.Should().BeGreaterThan(2);
        PdfText(result.Pdf, out var pages).Should().Contain($"Page {pages} of {pages}");
    }

    [Fact]
    public void Tampered_locked_text_fails()
    {
        var t = Builders.Template();
        t.Blocks.Single(b => b.Id == "rights").Text = "You may not ask for anything.";
        Failed(Render(t)).Should().ContainKey(LetterRenderer.LockedTextUnchanged).WhoseValue.Should().Contain("TPL-ACK/rights");
    }

    [Fact]
    public void Empty_required_block_fails()
    {
        var t = Builders.Template();
        t.Blocks.Single(b => b.Id == "body").Text = "{% if false %}x{% endif %}";
        Failed(Render(t)).Should().ContainKey(LetterRenderer.RequiredBlocksPresent).WhoseValue.Should().Contain("TPL-ACK/body");
    }

    [Fact]
    public void Missing_merge_data_fails()
    {
        var data = Builders.Data();
        ((System.Text.Json.Nodes.JsonObject)data["appeal"]!).Remove("id");
        Failed(Render(Builders.Template(), data: data)).Should().ContainKey(LetterRenderer.MergeFieldsResolved).WhoseValue.Should().Contain("appeal.id");
    }

    [Fact]
    public void Incomplete_address_fails()
    {
        var r = Builders.Recipient(line1: null);
        r.PostalCode = "7520";
        Failed(Render(Builders.Template(), recipient: r)).Should().ContainKey(LetterRenderer.RecipientAddressComplete)
            .WhoseValue.Should().Contain("address line 1").And.Contain("ZIP code");
    }

    [Fact]
    public void Missing_attachment_and_expired_form_fail()
    {
        var t = Builders.Template(kind: TemplateKind.Standardized);
        t.FormExpiresOn = new DateOnly(2026, 8, 31);
        var failed = Failed(Render(t, missingAttachments: ["NOTICE_OF_AVAILABILITY"]));
        failed.Should().ContainKey(LetterRenderer.AttachmentsPresent).WhoseValue.Should().Contain("NOTICE_OF_AVAILABILITY");
        failed.Should().ContainKey(LetterRenderer.FormNotExpired).WhoseValue.Should().Contain("2026-08-31");
    }

    [Fact]
    public void Text_below_the_minimum_font_fails()
    {
        var t = Builders.Template();
        t.Blocks.Single(b => b.Id == "signature").FontSize = 9;
        Failed(Render(t)).Should().ContainKey(LetterRenderer.MinimumFontSize).WhoseValue.Should().Contain("9 pt");
    }

    [Fact]
    public void Reading_level_is_checked_for_english_plan_text_only()
    {
        var t = Builders.Template();
        t.ReadingLevelMax = 6;
        t.Blocks.Single(b => b.Id == "body").Text =
            "Notwithstanding the aforementioned determination, comprehensive reconsideration necessitates substantial documentation substantiating medical appropriateness.";
        Failed(Render(t)).Should().ContainKey(LetterRenderer.ReadingLevel);

        t.Blocks.Single(b => b.Id == "body").Text = "We got your appeal. We will call you soon. You can ask us for help.";
        Render(t).Checks.Single(c => c.Name == LetterRenderer.ReadingLevel).Passed.Should().BeTrue();
    }

    private static Dictionary<string, string> Failed(LetterRenderResult result) =>
        result.Checks.Where(c => !c.Passed).ToDictionary(c => c.Name, c => c.Detail ?? "");
}
