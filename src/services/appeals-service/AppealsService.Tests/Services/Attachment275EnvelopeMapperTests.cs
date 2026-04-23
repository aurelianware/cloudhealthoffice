using AppealsService.Models;
using AppealsService.Services;

namespace AppealsService.Tests.Services;

/// <summary>
/// Pure-mapping unit tests for <see cref="Attachment275EnvelopeMapper"/>.
/// Encryption is the consumer's responsibility — the mapper takes
/// already-encrypted ciphertext via its <c>encryptedDescription</c>
/// argument, so these tests only verify pass-through and structural
/// shape.
/// </summary>
public class Attachment275EnvelopeMapperTests
{
    private static Attachment275EnvelopeDto NewEnvelope(
        string? controlNumber = "BHT-12345",
        string? documentType = "Medical Records",
        string? documentFormat = "PDF",
        string? transmissionCode = null,
        DateTime? submittedDate = null) => new()
    {
        TenantId = "tenant-a",
        Context = "appeal",
        ClaimId = "claim-1",
        DocumentType = documentType,
        DocumentFormat = documentFormat,
        ControlNumber = controlNumber,
        TransmissionCode = transmissionCode,
        SubmittedDate = submittedDate ?? new DateTime(2026, 2, 7, 14, 0, 0, DateTimeKind.Utc),
        Notes = "plain note text" // not used by the mapper directly
    };

    [Fact]
    public void ToAppealAttachment_PopulatesControlNumberFromEnvelope()
    {
        var mapper = new Attachment275EnvelopeMapper();
        var envelope = NewEnvelope(controlNumber: "BHT-XYZ-789");

        var result = mapper.ToAppealAttachment(envelope, encryptedDescription: null);

        result.ControlNumber.Should().Be("BHT-XYZ-789");
    }

    [Fact]
    public void ToAppealAttachment_DefaultsTransmissionCodeToEL_WhenEnvelopeMissing()
    {
        var mapper = new Attachment275EnvelopeMapper();
        var envelope = NewEnvelope(transmissionCode: null);

        var result = mapper.ToAppealAttachment(envelope, encryptedDescription: null);

        result.TransmissionCode.Should().Be("EL");
    }

    [Fact]
    public void ToAppealAttachment_PassesEncryptedDescriptionThrough()
    {
        var mapper = new Attachment275EnvelopeMapper();
        var envelope = NewEnvelope();

        var result = mapper.ToAppealAttachment(envelope, encryptedDescription: "enc::ciphertext::abc");

        result.Description.Should().Be("enc::ciphertext::abc",
            "the mapper must not re-encrypt or transform the ciphertext supplied by the consumer");
    }

    [Fact]
    public void ToAppealAttachment_DerivesDeterministicFileName()
    {
        var mapper = new Attachment275EnvelopeMapper();
        var envelope = NewEnvelope(
            controlNumber: "BHT-12345",
            documentFormat: "PDF",
            submittedDate: new DateTime(2026, 2, 7, 14, 0, 0, DateTimeKind.Utc));

        var result = mapper.ToAppealAttachment(envelope, encryptedDescription: null);

        result.FileName.Should().Be("275-BHT-12345-20260207140000.pdf");
    }

    [Fact]
    public void ToAppealAttachment_FileName_FallsBackForUnknownControlAndFormat()
    {
        var mapper = new Attachment275EnvelopeMapper();
        var envelope = NewEnvelope(
            controlNumber: null,
            documentFormat: null,
            submittedDate: new DateTime(2026, 3, 15, 9, 30, 0, DateTimeKind.Utc));

        var result = mapper.ToAppealAttachment(envelope, encryptedDescription: null);

        result.FileName.Should().Be("275-unknown-20260315093000.bin");
    }

    [Fact]
    public void ToAppealAttachment_AttachmentTypeDescription_PreservesOriginalDocumentType()
    {
        var mapper = new Attachment275EnvelopeMapper();
        var envelope = NewEnvelope(documentType: "Medical Records");

        var result = mapper.ToAppealAttachment(envelope, encryptedDescription: null);

        result.AttachmentTypeDescription.Should().Be("Medical Records");
    }

    [Fact]
    public void ToAppealAttachment_StatusIsSent_WhenIngressedFrom275()
    {
        // Difference vs. the controller's POST /attachments path, which
        // creates Pending: a 275 received over Kafka has, by definition,
        // already been transmitted over the X12 network.
        var mapper = new Attachment275EnvelopeMapper();

        var result = mapper.ToAppealAttachment(NewEnvelope(), encryptedDescription: null);

        result.Status.Should().Be(AttachmentStatus.Sent);
    }

    [Fact]
    public void ToAppealAttachment_UploadedAt_FallsBackToUtcNow_WhenEnvelopeDateMissing()
    {
        var mapper = new Attachment275EnvelopeMapper();
        var envelope = NewEnvelope(submittedDate: default(DateTime));

        var before = DateTime.UtcNow.AddSeconds(-1);
        var result = mapper.ToAppealAttachment(envelope, encryptedDescription: null);
        var after = DateTime.UtcNow.AddSeconds(1);

        result.UploadedAt.Should().BeAfter(before).And.BeBefore(after);
    }

    [Fact]
    public void ToAppealAttachment_FileNameStamp_AgreesWithUploadedAt_WhenSubmittedDateMissing()
    {
        // Regression: when envelope.SubmittedDate is default, both
        // UploadedAt and the filename's timestamp segment used to call
        // DateTime.UtcNow independently and could drift by milliseconds —
        // producing a record where the filename stamp does not match the
        // persisted UploadedAt. Mapper must compute the effective date
        // once and reuse it.
        var mapper = new Attachment275EnvelopeMapper();
        var envelope = NewEnvelope(controlNumber: "BHT-CONSISTENCY", submittedDate: default(DateTime));

        var result = mapper.ToAppealAttachment(envelope, encryptedDescription: null);

        var expectedStamp = result.UploadedAt.ToString("yyyyMMddHHmmss");
        result.FileName.Should().Be($"275-BHT-CONSISTENCY-{expectedStamp}.{envelope.DocumentFormat!.ToLowerInvariant()}");
    }

    [Theory]
    [InlineData("Medical Records", "OZ")]
    [InlineData("Lab Results", "LA")]
    [InlineData("Discharge Summary", "DS")]
    [InlineData("Operative Report", "OB")]
    [InlineData("Radiology Report", "RR")]
    public void MapDocumentTypeToAttachmentTypeCode_ReturnsExpected_ForKnownTypes(string documentType, string expected)
    {
        var mapper = new Attachment275EnvelopeMapper();

        mapper.MapDocumentTypeToAttachmentTypeCode(documentType).Should().Be(expected);
    }

    [Fact]
    public void MapDocumentTypeToAttachmentTypeCode_ReturnsOZ_ForUnknownType()
    {
        var mapper = new Attachment275EnvelopeMapper();

        mapper.MapDocumentTypeToAttachmentTypeCode("Some Unmapped Type").Should().Be("OZ");
    }

    [Fact]
    public void MapDocumentTypeToAttachmentTypeCode_ReturnsOZ_ForNullOrEmpty()
    {
        var mapper = new Attachment275EnvelopeMapper();

        mapper.MapDocumentTypeToAttachmentTypeCode(null).Should().Be("OZ");
        mapper.MapDocumentTypeToAttachmentTypeCode(string.Empty).Should().Be("OZ");
    }

    [Theory]
    [InlineData("AA")]
    [InlineData("BM")]
    [InlineData("EL")]
    [InlineData("FT")]
    [InlineData("FX")]
    [InlineData("IL")]
    [InlineData("OZ")]
    public void ResolveTransmissionCode_AcceptsAllSevenCodeSystemEntries(string code)
    {
        var mapper = new Attachment275EnvelopeMapper();

        mapper.ResolveTransmissionCode(code).Should().Be(code);
    }

    [Fact]
    public void ResolveTransmissionCode_DefaultsToEL_ForUnrecognized()
    {
        var mapper = new Attachment275EnvelopeMapper();

        mapper.ResolveTransmissionCode("ZZ").Should().Be("EL");
        mapper.ResolveTransmissionCode("not-a-code").Should().Be("EL");
        mapper.ResolveTransmissionCode(null).Should().Be("EL");
        mapper.ResolveTransmissionCode(string.Empty).Should().Be("EL");
    }
}
