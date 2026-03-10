using AttachmentService.Models;
using AttachmentService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.Edi.Tests;

/// <summary>
/// Tests for the 824 Application Advice rejection catalog:
/// - Each standard rejection code maps to the correct X12 TED01 error type code
/// - TED segment is present in the EDI when a rejection code is set
/// - TED segment is absent for accepted transactions
/// - Notes override the default TED02 description
/// - Unknown codes do not emit a TED segment
/// - SE count remains correct in all combinations
/// </summary>
public class AttachmentRejectionCatalogTests
{
    private static AcknowledgmentGeneratorService MakeGenerator() =>
        new(NullLogger<AcknowledgmentGeneratorService>.Instance);

    private static TradingPartner DefaultTp() => new()
    {
        InterchangeSenderId   = "SENDERID",
        InterchangeReceiverId = "RECEIVERID",
        ApplicationSenderId   = "APPSEND",
        ApplicationReceiverId = "APPRECV"
    };

    // ── TED01 error type code mapping ────────────────────────────────────

    [Theory]
    [InlineData(AttachmentRejectionCode.Duplicate,            "7")]
    [InlineData(AttachmentRejectionCode.InvalidFormat,        "3")]
    [InlineData(AttachmentRejectionCode.MissingData,          "4")]
    [InlineData(AttachmentRejectionCode.InvalidProvider,      "1")]
    [InlineData(AttachmentRejectionCode.InvalidRfai,          "2")]
    [InlineData(AttachmentRejectionCode.InvalidData,          "5")]
    [InlineData(AttachmentRejectionCode.SizeExceeded,         "5")]
    [InlineData(AttachmentRejectionCode.DocumentTypeMismatch, "5")]
    public void ToTed01ErrorTypeCode_ReturnsCorrectCode(string rejectionCode, string expectedTed01)
    {
        Assert.Equal(expectedTed01, AttachmentRejectionCode.ToTed01ErrorTypeCode(rejectionCode));
    }

    [Fact]
    public void ToTed01ErrorTypeCode_UnknownCode_ReturnsNull()
    {
        Assert.Null(AttachmentRejectionCode.ToTed01ErrorTypeCode("UNKNOWN_CODE"));
    }

    [Fact]
    public void ToTed01ErrorTypeCode_Null_ReturnsNull()
    {
        Assert.Null(AttachmentRejectionCode.ToTed01ErrorTypeCode(null));
    }

    // ── TED segment in generated 824 ─────────────────────────────────────

    [Theory]
    [InlineData(AttachmentRejectionCode.Duplicate,            "7")]
    [InlineData(AttachmentRejectionCode.InvalidFormat,        "3")]
    [InlineData(AttachmentRejectionCode.MissingData,          "4")]
    [InlineData(AttachmentRejectionCode.InvalidProvider,      "1")]
    [InlineData(AttachmentRejectionCode.InvalidRfai,          "2")]
    [InlineData(AttachmentRejectionCode.InvalidData,          "5")]
    [InlineData(AttachmentRejectionCode.SizeExceeded,         "5")]
    [InlineData(AttachmentRejectionCode.DocumentTypeMismatch, "5")]
    public void Generate824_FailedWithRejectionCode_EmitsTedSegment(string rejectionCode, string expectedTed01)
    {
        var svc = MakeGenerator();
        var attachment = new Attachment
        {
            Id            = "ATT-REJ-001",
            Status        = "Failed",
            RejectionCode = rejectionCode
        };
        var edi  = svc.Generate824(attachment, DefaultTp());
        var segs = EdiTestHelper.ParseSegments(edi);

        Assert.True(EdiTestHelper.HasSegment(segs, "TED"), $"TED segment missing for code {rejectionCode}");
        var ted = EdiTestHelper.Segment(segs, "TED");
        Assert.Equal(expectedTed01, ted[1]); // TED01 = error type code
    }

    [Fact]
    public void Generate824_FailedWithRejectionCode_Ted02UsesDefaultDescription_WhenNoNotes()
    {
        var svc = MakeGenerator();
        var attachment = new Attachment
        {
            Id            = "ATT-REJ-002",
            Status        = "Failed",
            RejectionCode = AttachmentRejectionCode.Duplicate,
            Notes         = null
        };
        var edi  = svc.Generate824(attachment, DefaultTp());
        var segs = EdiTestHelper.ParseSegments(edi);
        var ted  = EdiTestHelper.Segment(segs, "TED");

        Assert.Equal("Duplicate attachment submission", ted[2]); // TED02 = description
    }

    [Fact]
    public void Generate824_FailedWithRejectionCode_Ted02UsesNotesOverDefaultDescription()
    {
        var svc = MakeGenerator();
        var attachment = new Attachment
        {
            Id            = "ATT-REJ-003",
            Status        = "Failed",
            RejectionCode = AttachmentRejectionCode.InvalidFormat,
            Notes         = "TIFF files are not accepted by this payer"
        };
        var edi  = svc.Generate824(attachment, DefaultTp());
        var segs = EdiTestHelper.ParseSegments(edi);
        var ted  = EdiTestHelper.Segment(segs, "TED");

        Assert.Equal("TIFF files are not accepted by this payer", ted[2]);
    }

    [Fact]
    public void Generate824_FailedWithUnknownRejectionCode_NoTedSegment()
    {
        var svc = MakeGenerator();
        var attachment = new Attachment
        {
            Id            = "ATT-REJ-004",
            Status        = "Failed",
            RejectionCode = "SOMETHING_CUSTOM",
            Notes         = "Custom rejection"
        };
        var edi  = svc.Generate824(attachment, DefaultTp());
        var segs = EdiTestHelper.ParseSegments(edi);

        Assert.False(EdiTestHelper.HasSegment(segs, "TED"));
    }

    [Fact]
    public void Generate824_FailedWithNoRejectionCode_NoTedSegment()
    {
        var svc = MakeGenerator();
        var attachment = new Attachment
        {
            Id            = "ATT-REJ-005",
            Status        = "Failed",
            RejectionCode = null,
            Notes         = "Manually rejected by staff"
        };
        var edi  = svc.Generate824(attachment, DefaultTp());
        var segs = EdiTestHelper.ParseSegments(edi);

        Assert.False(EdiTestHelper.HasSegment(segs, "TED"));
    }

    [Fact]
    public void Generate824_AcceptedStatus_NoTedSegment()
    {
        var svc = MakeGenerator();
        var attachment = new Attachment { Id = "ATT-REJ-006", Status = "Linked" };
        var edi  = svc.Generate824(attachment, DefaultTp());
        var segs = EdiTestHelper.ParseSegments(edi);

        Assert.False(EdiTestHelper.HasSegment(segs, "TED"));
    }

    // ── SE count remains correct with TED ────────────────────────────────

    [Fact]
    public void Generate824_WithTedSegment_SeCountCorrect()
    {
        var svc = MakeGenerator();
        var attachment = new Attachment
        {
            Id            = "ATT-REJ-007",
            Status        = "Failed",
            ClaimId       = "CLM-5000",
            RFAIReference = "RFAI-001",
            RejectionCode = AttachmentRejectionCode.InvalidRfai
        };
        var edi  = svc.Generate824(attachment, DefaultTp());
        var segs = EdiTestHelper.ParseSegments(edi);

        // ST BGN OTI REF*D9 REF*EJ TED MSG SE = 8 segments
        EdiTestHelper.AssertSeCountCorrect(segs);
    }

    [Fact]
    public void Generate824_WithoutTedSegment_SeCountCorrect()
    {
        var svc = MakeGenerator();
        var attachment = new Attachment
        {
            Id     = "ATT-REJ-008",
            Status = "Failed",
            Notes  = "Unknown issue"
            // No RejectionCode → no TED
        };
        var edi  = svc.Generate824(attachment, DefaultTp());
        EdiTestHelper.AssertSeCountCorrect(EdiTestHelper.ParseSegments(edi));
    }
}
