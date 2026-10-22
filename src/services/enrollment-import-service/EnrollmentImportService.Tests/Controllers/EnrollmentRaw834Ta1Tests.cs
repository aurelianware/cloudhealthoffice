using System.Text;
using CloudHealthOffice.Infrastructure.Edi.Interchange;
using EnrollmentImportService.Models;
using EnrollmentImportService.Services;
using EnrollmentImportService.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace EnrollmentImportService.Tests.Controllers;

/// <summary>The raw 834 upload runs the interchange envelope gate before the 834 is parsed or imported.</summary>
public class EnrollmentRaw834Ta1Tests
{
    private const string Body =
        "GS*BE*SPONSOR01*CHO*20260115*1200*7*X*005010X220A1~ST*834*0001*005010X220A1~BGN*00*REF1*20260115*1200****2~" +
        "INS*Y*18*021*28*A~REF*0F*SUB1~NM1*IL*1*DOE*JANE~SE*6*0001~GE*1*7~";

    private static string Edi834(char isa14 = '0', string iea02 = "000000701") =>
        $"ISA*00*          *00*          *ZZ*SPONSOR01      *ZZ*CHO            *260115*1200*^*00501*000000701*{isa14}*T*:~" +
        Body + $"IEA*1*{iea02}~";

    private readonly Mock<IEnrollmentImportService> _import = new();
    private readonly FlakyStreamingParser _parser = new();
    private readonly RunTestKit _kit;

    public EnrollmentRaw834Ta1Tests()
    {
        _import.Setup(s => s.ImportEnrollmentAsync(It.IsAny<Enrollment834>(), "t1")).ReturnsAsync(new ImportResult { SuccessCount = 1 });
        _kit = new RunTestKit(_import.Object, _parser);
    }

    private static IFormFile File(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "f.834");
    }

    [Fact]
    public async Task RejectedEnvelope_ReturnsTa1_AndNeverParsesOrImports()
    {
        var result = await _kit.Controller().ImportRaw834(File(Edi834(iea02: "000000999")));

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("TA1*000000701*260115*1200*R*001~", bad.Value!.ToString());
        _kit.Runs.All.Should().BeEmpty("a rejected envelope queues no import");
        _import.Verify(s => s.ImportEnrollmentAsync(It.IsAny<Enrollment834>(), It.IsAny<string>()), Times.Never);
        var stored = Assert.Single(await _kit.Interchanges.ListAcknowledgmentsAsync("t1", new InterchangeAcknowledgmentQuery()));
        Assert.Equal("834", stored.TransactionType);
    }

    [Fact]
    public async Task AckRequested_ImportsAndReturnsAcceptedTa1()
    {
        var result = await _kit.Controller().ImportRaw834(File(Edi834(isa14: '1')));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var import = Assert.IsType<ImportResult>(ok.Value);
        Assert.Equal("A", import.InterchangeAcknowledgmentCode);
        Assert.Contains("*A*000~", import.AcknowledgmentTa1);
        Assert.Single(import.Ta1AcknowledgmentIds);
        Assert.Equal(1, import.SuccessCount);
    }

    [Fact]
    public async Task ParseFailureAfterTheEnvelope_ReleasesTheControlNumber()
    {
        _parser.Fail = "bad INS loop";
        var failed = await _kit.Controller().ImportRaw834(File(Edi834()));
        Assert.Contains("bad INS loop", Assert.IsType<BadRequestObjectResult>(failed.Result).Value!.ToString());
        _import.Verify(s => s.ImportEnrollmentAsync(It.IsAny<Enrollment834>(), It.IsAny<string>()), Times.Never);

        _parser.Fail = null;
        Assert.IsType<OkObjectResult>((await _kit.Controller().ImportRaw834(File(Edi834()))).Result);
    }

    [Fact]
    public async Task ImportFailingAfterItStarted_KeepsTheControlNumber()
    {
        _import.Setup(s => s.ImportEnrollmentAsync(It.IsAny<Enrollment834>(), "t1")).ThrowsAsync(new InvalidOperationException("member-service down"));
        var first = await _kit.Controller().ImportRaw834(File(Edi834()));

        // The unit failed; the run finished with it recorded rather than the request failing.
        var result = Assert.IsType<ImportResult>(Assert.IsType<OkObjectResult>(first.Result).Value);
        result.Errors.Should().ContainSingle(e => e.StartsWith("INS 1:"));
        _kit.Runs.All.Single().FailedUnits.Should().Be(1);

        // Members may have been written: the resend is refused as a duplicate, not imported again.
        var resent = await _kit.Controller().ImportRaw834(File(Edi834()));
        Assert.Contains("*R*025~", Assert.IsType<BadRequestObjectResult>(resent.Result).Value!.ToString());
    }

    [Fact]
    public async Task NoAckRequested_ImportsWithoutTa1_AndASecondUploadIsADuplicate()
    {
        var first = Assert.IsType<OkObjectResult>((await _kit.Controller().ImportRaw834(File(Edi834()))).Result);
        Assert.Null(((ImportResult)first.Value!).AcknowledgmentTa1);

        var second = await _kit.Controller().ImportRaw834(File(Edi834()));
        var bad = Assert.IsType<BadRequestObjectResult>(second.Result);
        Assert.Contains("*R*025~", bad.Value!.ToString());
    }
}
