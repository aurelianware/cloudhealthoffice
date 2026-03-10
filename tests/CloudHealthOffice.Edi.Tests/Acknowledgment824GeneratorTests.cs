using AttachmentService.Models;
using AttachmentService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.Edi.Tests;

public class Acknowledgment824GeneratorTests
{
    private static AcknowledgmentGeneratorService MakeGenerator() =>
        new(NullLogger<AcknowledgmentGeneratorService>.Instance);

    private static TradingPartner DefaultTp() => new()
    {
        InterchangeSenderId  = "SENDERID",
        InterchangeReceiverId = "RECEIVERID",
        ApplicationSenderId  = "APPSEND",
        ApplicationReceiverId = "APPRECV"
    };

    // ── OTI acceptance code mapping ──────────────────────────────────────

    [Theory]
    [InlineData("Linked",    "TA")]
    [InlineData("Validated", "TA")]
    [InlineData("Failed",    "TR")]
    [InlineData("Received",  "TP")]
    [InlineData("Unknown",   "TP")]
    public void Generate824_OtiAcceptanceCode_MapsCorrectly(string status, string expectedCode)
    {
        var svc = MakeGenerator();
        var attachment = new Attachment { Id = "ATT-001", Status = status };
        var edi = svc.Generate824(attachment, DefaultTp());

        var segs = EdiTestHelper.ParseSegments(edi);
        var oti  = EdiTestHelper.Segment(segs, "OTI");

        Assert.Equal(expectedCode, oti[1]);
    }

    // ── REF enrichment ───────────────────────────────────────────────────

    [Fact]
    public void Generate824_WithClaimId_IncludesRefD9()
    {
        var svc = MakeGenerator();
        var attachment = new Attachment { Id = "ATT-002", Status = "Linked", ClaimId = "CLM-9999" };
        var edi = svc.Generate824(attachment, DefaultTp());

        Assert.Contains("REF*D9*CLM-9999~", edi);
    }

    [Fact]
    public void Generate824_WithoutClaimId_NoRefD9()
    {
        var svc = MakeGenerator();
        var attachment = new Attachment { Id = "ATT-003", Status = "Validated", ClaimId = null };
        var edi = svc.Generate824(attachment, DefaultTp());

        Assert.DoesNotContain("REF*D9*", edi);
    }

    [Fact]
    public void Generate824_WithRfaiReference_IncludesRefEj()
    {
        var svc = MakeGenerator();
        var attachment = new Attachment
        {
            Id             = "ATT-004",
            Status         = "Linked",
            ClaimId        = "CLM-1000",
            RFAIReference  = "RFAI-XYZ"
        };
        var edi = svc.Generate824(attachment, DefaultTp());

        Assert.Contains("REF*EJ*RFAI-XYZ~", edi);
    }

    [Fact]
    public void Generate824_WithoutRfaiReference_NoRefEj()
    {
        var svc = MakeGenerator();
        var attachment = new Attachment { Id = "ATT-005", Status = "Received", RFAIReference = null };
        var edi = svc.Generate824(attachment, DefaultTp());

        Assert.DoesNotContain("REF*EJ*", edi);
    }

    // ── MSG text ─────────────────────────────────────────────────────────

    [Fact]
    public void Generate824_FailedStatus_MsgContainsNotes()
    {
        var svc = MakeGenerator();
        var attachment = new Attachment
        {
            Id     = "ATT-006",
            Status = "Failed",
            Notes  = "Invalid document format"
        };
        var edi = svc.Generate824(attachment, DefaultTp());

        Assert.Contains("MSG*Attachment rejected: Invalid document format~", edi);
    }

    // ── Envelope / SE count ──────────────────────────────────────────────

    [Fact]
    public void Generate824_CoreSegmentsPresent()
    {
        var svc = MakeGenerator();
        var attachment = new Attachment { Id = "ATT-007", Status = "Received" };
        var edi = svc.Generate824(attachment, DefaultTp());

        Assert.Contains("ST*824*0001*005010~", edi);
        Assert.Contains("BGN*11*ATT-007*", edi);
        Assert.Contains("GS*AG*APPSEND*APPRECV*", edi);
    }

    [Fact]
    public void Generate824_SeCountCorrect_NoOptionalRefs()
    {
        // No ClaimId, no RFAIReference → ST BGN OTI MSG SE = 5 segments
        var svc = MakeGenerator();
        var attachment = new Attachment { Id = "ATT-008", Status = "Received" };
        var edi = svc.Generate824(attachment, DefaultTp());

        EdiTestHelper.AssertSeCountCorrect(EdiTestHelper.ParseSegments(edi));
    }

    [Fact]
    public void Generate824_SeCountCorrect_BothOptionalRefs()
    {
        // ClaimId + RFAIReference → ST BGN OTI REF*D9 REF*EJ MSG SE = 7 segments
        var svc = MakeGenerator();
        var attachment = new Attachment
        {
            Id            = "ATT-009",
            Status        = "Linked",
            ClaimId       = "CLM-2000",
            RFAIReference = "RFAI-ABC"
        };
        var edi = svc.Generate824(attachment, DefaultTp());

        EdiTestHelper.AssertSeCountCorrect(EdiTestHelper.ParseSegments(edi));
    }
}
