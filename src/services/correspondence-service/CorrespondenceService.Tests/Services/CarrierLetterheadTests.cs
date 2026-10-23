using System.Text.Json;
using CloudHealthOffice.PlanConfiguration;
using CorrespondenceService.Models;
using CorrespondenceService.Services;
using CorrespondenceService.Tests.Support;
using UglyToad.PdfPig;

namespace CorrespondenceService.Tests.Services;

/// <summary>
/// Which legal entity a letter goes out from
/// (plan-configuration.md §12.4). Before carriers existed,
/// correspondence-service had no sender at all: a letter carried the
/// recipient's address, the letter's own data and nothing saying who sent it.
///
/// <para>
/// The carrier now supplies it, two ways. A
/// <see cref="TemplateBlockKind.ReturnAddressBlock"/> left blank renders the
/// carrier's name and address, as a blank
/// <see cref="TemplateBlockKind.AddressBlock"/> renders the recipient's; and a
/// template that lays out its own letterhead can reach the same values through
/// <c>{{ carrier.* }}</c>.
/// </para>
///
/// <para>
/// A carrier that states no letterhead name leaves the block empty, which
/// fails that block's required-blocks check and stops the letter. A letter
/// headed by a blank line, or by whatever the deployment happens to be called,
/// is worse than one that is not sent: a notice of appeal rights is a statement
/// about somebody's benefits, and whose statement it is matters.
/// </para>
/// </summary>
public class CarrierLetterheadTests
{
    private static readonly CarrierIdentity Acme = new()
    {
        Id = "tenant-1|HMO",
        Code = "HMO",
        Name = "the HMO",
        LegalName = "ACME HEALTH PLAN OF TEXAS INC",
        CorrespondenceAddress = new CarrierAddress
        {
            Street = "100 Congress Ave Suite 400", City = "Austin", State = "TX", ZipCode = "78701",
        },
        IsDefault = true,
    };

    private static LetterTemplate WithReturnAddress()
    {
        var t = Builders.Template();
        t.Blocks.Insert(0, new TemplateBlock { Id = "from", Kind = TemplateBlockKind.ReturnAddressBlock, Text = "" });
        t.RequiredBlocks = [.. t.RequiredBlocks, "from"];
        return t;
    }

    private static LetterRenderResult Render(LetterTemplate template, CarrierIdentity? carrier)
    {
        var model = MergeFields.FromJson(Builders.Data());
        model["letter"] = new Dictionary<string, object?> { ["date"] = "October 21, 2026", ["id"] = "LTR-TEST" };
        model["carrier"] = carrier is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>
        {
            ["name"] = carrier.LetterheadName,
            ["city"] = carrier.CorrespondenceAddress?.City,
        };
        return LetterRenderer.Render(new LetterRenderInput(template, [], [], model,
            Builders.Recipient(), "LTR-TEST", Builders.Today, DateTime.UtcNow, carrier));
    }

    private static string PdfText(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return string.Join("\n", doc.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text))));
    }

    [Fact]
    public void A_blank_return_address_block_renders_the_carriers_name_and_address()
    {
        var result = Render(WithReturnAddress(), Acme);

        result.Passed.Should().BeTrue(string.Join("; ", result.Checks.Where(c => !c.Passed).Select(c => c.Detail)));
        var text = PdfText(result.Pdf);
        text.Should().Contain("ACME HEALTH PLAN OF TEXAS INC")
            .And.Contain("100 Congress Ave Suite 400")
            .And.Contain("Austin, TX 78701");
        // Still the recipient's letter: the sender does not displace them.
        text.Should().Contain("Maria Lopez").And.Contain("Dallas, TX 75201");
    }

    [Fact]
    public void A_correspondence_name_heads_the_letter_in_preference_to_the_legal_name()
    {
        var result = Render(WithReturnAddress(), Acme with { CorrespondenceName = "Acme Health Plan" });

        PdfText(result.Pdf).Should().Contain("Acme Health Plan").And.NotContain("OF TEXAS INC");
    }

    [Fact]
    public void A_carrier_that_states_no_letterhead_stops_the_letter_rather_than_sending_a_blank_one()
    {
        var result = Render(WithReturnAddress(), Acme with { LegalName = null, CorrespondenceName = null });

        result.Passed.Should().BeFalse();
        result.Checks.Single(c => c.Name == LetterRenderer.RequiredBlocksPresent).Passed.Should().BeFalse();
        PdfText(result.Pdf).Should().NotContain("ACME");
    }

    [Fact]
    public void A_host_with_no_carrier_at_all_behaves_the_same_way()
    {
        // Not a carrier that says nothing but no carrier record reaching the
        // renderer at all — a host running without a directory. Same outcome,
        // because the letter is equally unsendable.
        Render(WithReturnAddress(), carrier: null).Passed.Should().BeFalse();
    }

    [Fact]
    public void A_template_with_no_return_address_block_is_unaffected_by_the_carrier()
    {
        // Every template that exists today. Adding a sender to the model must
        // not change a letter that does not ask for one.
        var withCarrier = Render(Builders.Template(), Acme);
        var without = Render(Builders.Template(), carrier: null);

        withCarrier.Passed.Should().BeTrue();
        without.Passed.Should().BeTrue();
        PdfText(withCarrier.Pdf).Should().Be(PdfText(without.Pdf));
    }

    [Fact]
    public void A_block_that_writes_its_own_letterhead_reads_the_carrier_through_merge_fields()
    {
        var t = Builders.Template();
        t.Blocks.Insert(0, new TemplateBlock { Id = "from", Text = "From {{ carrier.name }} in {{ carrier.city }}" });
        t.RequiredBlocks = [.. t.RequiredBlocks, "from"];

        var result = Render(t, Acme);

        result.Passed.Should().BeTrue();
        PdfText(result.Pdf).Should().Contain("From ACME HEALTH PLAN OF TEXAS INC in Austin");
    }

    [Fact]
    public void A_letterhead_merge_field_the_carrier_cannot_fill_stops_the_letter()
    {
        var t = Builders.Template();
        t.Blocks.Insert(0, new TemplateBlock { Id = "from", Text = "From {{ carrier.name }}" });
        t.RequiredBlocks = [.. t.RequiredBlocks, "from"];

        var result = Render(t, Acme with { LegalName = null, CorrespondenceName = null });

        result.Passed.Should().BeFalse();
        result.Checks.Single(c => c.Name == LetterRenderer.MergeFieldsResolved).Passed.Should().BeFalse();
    }

    [Fact]
    public void An_address_the_carrier_states_only_in_part_still_renders_what_it_has()
    {
        var result = Render(WithReturnAddress(), Acme with
        {
            CorrespondenceAddress = new CarrierAddress { City = "Austin", State = "TX" },
        });

        result.Passed.Should().BeTrue();
        var text = PdfText(result.Pdf);
        text.Should().Contain("ACME HEALTH PLAN OF TEXAS INC").And.Contain("Austin, TX");
    }

    [Fact]
    public void The_written_record_says_which_entity_the_letter_went_out_under()
    {
        // The letter is stored write-once and its snapshot is the audit trail.
        // It has always recorded who the letter went to; it must now also say
        // whose name it went out in.
        var snapshot = LetterComposer.Snapshot(Builders.Template(), [], Builders.Request(), Builders.Today, Acme);

        using var json = JsonDocument.Parse(snapshot);
        var carrier = json.RootElement.GetProperty("carrier");
        carrier.GetProperty("id").GetString().Should().Be("tenant-1|HMO");
        carrier.GetProperty("letterheadName").GetString().Should().Be("ACME HEALTH PLAN OF TEXAS INC");
    }

    [Fact]
    public void The_record_of_a_letter_sent_with_no_carrier_says_so_rather_than_inventing_one()
    {
        var snapshot = LetterComposer.Snapshot(Builders.Template(), [], Builders.Request(), Builders.Today, carrier: null);

        using var json = JsonDocument.Parse(snapshot);
        json.RootElement.GetProperty("carrier").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
