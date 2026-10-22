using System.Text;
using CloudHealthOffice.Infrastructure.Edi.Files;
using CloudHealthOffice.Infrastructure.Edi.Interchange;
using EnrollmentImportService.Controllers;
using EnrollmentImportService.Models;
using EnrollmentImportService.Services;
using EnrollmentImportService.Services.Edi;
using EnrollmentImportService.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace EnrollmentImportService.Tests.Controllers;

/// <summary>Every raw 834 (upload or SFTP) gets an EDI file record (edi-operations.md §3–4).</summary>
public class EnrollmentRaw834EdiFileTests
{
    private const string Body =
        "GS*BE*SPONSOR01*CHO*20260115*1200*7*X*005010X220A1~ST*834*0001*005010X220A1~BGN*00*REF1*20260115*1200****2~" +
        "INS*Y*18*021*28*A~REF*0F*SUB1~NM1*IL*1*DOE*JANE~SE*6*0001~GE*1*7~";

    private static string Edi834(string iea02 = "000000701") =>
        "ISA*00*          *00*          *ZZ*SPONSOR01      *ZZ*CHO            *260115*1200*^*00501*000000701*0*T*:~" +
        Body + $"IEA*1*{iea02}~";

    private readonly Mock<IEnrollmentImportService> _import = new();
    private readonly FlakyStreamingParser _parser = new();
    private RunTestKit? _kit;
    private Enrollment834? _imported;

    private InMemoryEdiFileStore _files => Kit().Files;

    private RunTestKit Kit(ImportResult? result = null)
    {
        if (_kit is not null) return _kit;
        _import.Setup(s => s.ImportEnrollmentAsync(It.IsAny<Enrollment834>(), "t1"))
            .Callback<Enrollment834, string>((e, _) => _imported = e)
            .ReturnsAsync(result ?? new ImportResult { SuccessCount = 3, FailedCount = 1 });
        return _kit = new RunTestKit(_import.Object, _parser);
    }

    private EnrollmentController Controller(ImportResult? result = null) => Kit(result).Controller();

    private static IFormFile File(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "f.834");
    }

    private async Task<EdiFile> OnlyFileAsync() => (await _files.ListAsync("t1", new EdiFileQuery())).Single();

    [Fact]
    public async Task AcceptedFile_IsRecordedAndLinkedToTheImport()
    {
        var response = await Controller().ImportRaw834(File(Edi834()), source: "Sftp", sourcePath: "/outbound/834/f.834");

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var file = await OnlyFileAsync();
        file.TransactionType.Should().Be("834");
        file.Source.Should().Be(EdiFileSource.Sftp);
        file.SourcePath.Should().Be("/outbound/834/f.834");
        file.InterchangeControlNumbers.Should().Equal("000000701");
        file.GroupControlNumbers.Should().Equal("7");
        file.Status.Should().Be(EdiFileStatus.Applied);
        file.AcceptedRecords.Should().Be(3);
        file.RejectedRecords.Should().Be(1);
        ((ImportResult)ok.Value!).EdiFileId.Should().Be(file.Id);
        _imported!.EdiFileId.Should().Be(file.Id, "the import run records the file it came from");
        file.RawDocumentId.Should().StartWith("tenants/t1/edi/inbound/834/", "the raw file is kept");
        file.SizeBytes.Should().Be(Encoding.UTF8.GetByteCount(Edi834()));
    }

    [Fact]
    public async Task RejectedEnvelope_IsRecordedAsRejected()
    {
        var response = await Controller().ImportRaw834(File(Edi834(iea02: "000000999")));

        Assert.IsType<BadRequestObjectResult>(response.Result);
        var file = await OnlyFileAsync();
        file.Status.Should().Be(EdiFileStatus.Rejected);
        file.Ta1AckCode.Should().Be(Ta1AckCodes.Rejected);
        file.Source.Should().Be(EdiFileSource.Upload);
    }

    [Fact]
    public async Task Duplicate_IsRecordedAsDuplicate()
    {
        await Controller().ImportRaw834(File(Edi834()));
        var second = await Controller().ImportRaw834(File(Edi834()));

        Assert.IsType<BadRequestObjectResult>(second.Result);
        var files = await _files.ListAsync("t1", new EdiFileQuery());
        files.Should().HaveCount(2);
        files.Select(f => f.Status).Should().BeEquivalentTo([EdiFileStatus.Applied, EdiFileStatus.Duplicate]);
    }

    [Fact]
    public async Task UnparseableContents_AreRecordedWithTheError()
    {
        var controller = Controller();
        _parser.Fail = "no INS loop";

        Assert.IsType<BadRequestObjectResult>((await controller.ImportRaw834(File(Edi834()))).Result);
        var file = await OnlyFileAsync();
        file.Status.Should().Be(EdiFileStatus.Rejected);
        file.Errors.Should().Contain(e => e.Contains("no INS loop"));
    }
}
