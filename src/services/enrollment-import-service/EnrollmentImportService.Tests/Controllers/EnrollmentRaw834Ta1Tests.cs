using System.Text;
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

/// <summary>The raw 834 upload runs the interchange envelope gate before the 834 parser.</summary>
public class EnrollmentRaw834Ta1Tests
{
    private const string Body =
        "GS*BE*SPONSOR01*CHO*20260115*1200*7*X*005010X220A1~ST*834*0001*005010X220A1~BGN*00*REF1*20260115*1200****2~SE*3*0001~GE*1*7~";

    private static string Edi834(char isa14 = '0', string iea02 = "000000701") =>
        $"ISA*00*          *00*          *ZZ*SPONSOR01      *ZZ*CHO            *260115*1200*^*00501*000000701*{isa14}*T*:~" +
        Body + $"IEA*1*{iea02}~";

    private readonly Mock<IEnrollmentImportService> _import = new();
    private readonly Mock<IEnrollment834EdiParser> _parser = new();
    private readonly InMemoryX12InterchangeStore _store = new();

    private EnrollmentController Controller()
    {
        _parser.Setup(p => p.Parse(It.IsAny<string>(), It.IsAny<string>())).Returns(new Enrollment834 { FileName = "f.834" });
        _import.Setup(s => s.ImportEnrollmentAsync(It.IsAny<Enrollment834>(), "t1")).ReturnsAsync(new ImportResult());
        var intake = new X12InterchangeIntake(_store, Options.Create(new X12InterchangeOptions()), NullLogger<X12InterchangeIntake>.Instance);
        return new EnrollmentController(
            _import.Object, _parser.Object, Mock.Of<IPlanCodeGapReportService>(), Mock.Of<IEnrollmentImportRunRepository>(),
            new TestActor(tenantId: "t1"), NullLogger<EnrollmentController>.Instance, intake);
    }

    private static IFormFile File(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "f.834");
    }

    [Fact]
    public async Task RejectedEnvelope_ReturnsTa1_AndNeverParsesOrImports()
    {
        var result = await Controller().ImportRaw834(File(Edi834(iea02: "000000999")));

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("TA1*000000701*260115*1200*R*001~", bad.Value!.ToString());
        _parser.Verify(p => p.Parse(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _import.Verify(s => s.ImportEnrollmentAsync(It.IsAny<Enrollment834>(), It.IsAny<string>()), Times.Never);
        var stored = Assert.Single(await _store.ListAcknowledgmentsAsync("t1", new InterchangeAcknowledgmentQuery()));
        Assert.Equal("834", stored.TransactionType);
    }

    [Fact]
    public async Task AckRequested_ImportsAndReturnsAcceptedTa1()
    {
        var result = await Controller().ImportRaw834(File(Edi834(isa14: '1')));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var import = Assert.IsType<ImportResult>(ok.Value);
        Assert.Equal("A", import.InterchangeAcknowledgmentCode);
        Assert.Contains("*A*000~", import.AcknowledgmentTa1);
        Assert.Single(import.Ta1AcknowledgmentIds);
    }

    [Fact]
    public async Task NoAckRequested_ImportsWithoutTa1_AndASecondUploadIsADuplicate()
    {
        var ctl = Controller();
        var first = Assert.IsType<OkObjectResult>((await ctl.ImportRaw834(File(Edi834()))).Result);
        Assert.Null(((ImportResult)first.Value!).AcknowledgmentTa1);

        var second = await ctl.ImportRaw834(File(Edi834()));
        var bad = Assert.IsType<BadRequestObjectResult>(second.Result);
        Assert.Contains("*R*025~", bad.Value!.ToString());
    }
}
