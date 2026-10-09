using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using PremiumBillingService.Controllers;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;
using PremiumBillingService.Services;

namespace PremiumBillingService.Tests.Remittance;

public class RemittancesControllerTests
{
    [Fact]
    public async Task OnePaymentFailingPartWay_DoesNotLoseTheOthersInTheFile()
    {
        var cash = new Mock<ICashApplicationService>();
        cash.Setup(c => c.ApplyAsync(It.Is<RemittanceAdvice>(a => a.TraceNumber == "T1"))).ThrowsAsync(new TimeoutException("db"));
        cash.Setup(c => c.ApplyAsync(It.Is<RemittanceAdvice>(a => a.TraceNumber == "T2")))
            .ReturnsAsync((RemittanceAdvice a) => new RemittanceBatch { TraceNumber = a.TraceNumber, Status = RemittanceBatchStatus.Completed });
        var controller = new RemittancesController(cash.Object, Mock.Of<IRemittanceBatchRepository>(),
            Mock.Of<IRemittanceExceptionRepository>(), NullLogger<RemittancesController>.Instance);
        var file = new Synthetic820()
            .Add(new Synthetic820.Transaction { Amount = 10m, Trace = "T1" }.Organization().Rmr("INV-A", 10m))
            .Add(new Synthetic820.Transaction { Amount = 20m, Trace = "T2" }.Organization().Rmr("INV-B", 20m))
            .ToString();
        var http = new DefaultHttpContext();
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(file));
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        var result = await controller.Upload820();

        var body = (RemittanceUploadResult)((OkObjectResult)result.Result!).Value!;
        body.Batches.Should().ContainSingle().Which.TraceNumber.Should().Be("T2");
        body.Rejected.Should().ContainSingle().Which.Error.Should().Contain("upload the file again to resume");
    }
}
