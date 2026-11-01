using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using ProviderContractsService.Controllers;
using ProviderContractsService.Models;
using ProviderContractsService.Repositories;
using ProviderContractsService.Services;

namespace ProviderContractsService.Tests.Controllers;

/// <summary>
/// sync-children answered 501 and nothing else propagated a contract change,
/// so a capitation rate config kept the contract number, NPI, provider name
/// and line of business it was created with (TX-06, CA-07).
/// </summary>
public class SyncChildrenTests
{
    private readonly Mock<IProviderContractRepository> _repo = new();
    private readonly Mock<ICapitationChildSync> _sync = new();
    private readonly ProviderContractsController _controller;

    public SyncChildrenTests()
    {
        _controller = new ProviderContractsController(_repo.Object, NullLogger<ProviderContractsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static ProviderContract Contract(string name = "Lone Star Pediatrics", LineOfBusiness lob = LineOfBusiness.Medicaid) => new()
    {
        Id = "pc-1",
        TenantId = "tenant-1",
        ContractNumber = "PC-1234567890-2026",
        ProviderNPI = "1234567890",
        ProviderName = name,
        LineOfBusiness = lob,
        PaymentMethodology = PaymentMethodology.FullCapitation,
        EffectiveDate = new DateTime(2026, 1, 1),
        CapitationRateConfigIds = ["cap-1", "cap-2"]
    };

    [Fact]
    public async Task SyncChildren_PushesToCapitation_AndReportsTheCount()
    {
        var contract = Contract();
        _repo.Setup(r => r.GetByIdAsync("pc-1")).ReturnsAsync(contract);
        _sync.Setup(s => s.SyncAsync(contract, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CapitationChildSyncResult(2, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), null));

        var result = await _controller.SyncChildren("pc-1", _sync.Object);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(200);
        ok.Value!.GetType().GetProperty("capitationRateConfigsUpdated")!.GetValue(ok.Value).Should().Be(2L);
    }

    [Fact]
    public async Task SyncChildren_WhenCapitationRefuses_Answers502_NotSuccess()
    {
        _repo.Setup(r => r.GetByIdAsync("pc-1")).ReturnsAsync(Contract());
        _sync.Setup(s => s.SyncAsync(It.IsAny<ProviderContract>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CapitationChildSyncResult(0, null, "capitation-service answered 403"));

        var result = await _controller.SyncChildren("pc-1", _sync.Object);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
    }

    [Fact]
    public async Task SyncChildren_UnknownContract_Is404()
    {
        _repo.Setup(r => r.GetByIdAsync("nope")).ReturnsAsync((ProviderContract?)null);

        var result = await _controller.SyncChildren("nope", _sync.Object);

        result.Should().BeOfType<NotFoundObjectResult>();
        _sync.Verify(s => s.SyncAsync(It.IsAny<ProviderContract>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateContract_ThatRenamesTheProvider_PropagatesToCapitation()
    {
        _repo.Setup(r => r.GetByIdAsync("pc-1")).ReturnsAsync(Contract());
        _repo.Setup(r => r.UpdateAsync(It.IsAny<ProviderContract>())).ReturnsAsync((ProviderContract c) => c);
        _sync.Setup(s => s.SyncAsync(It.IsAny<ProviderContract>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CapitationChildSyncResult(2, DateTime.UtcNow, null));

        await _controller.UpdateContract("pc-1", Contract(name: "Lone Star Pediatrics PLLC"), _sync.Object);

        _sync.Verify(s => s.SyncAsync(It.Is<ProviderContract>(c => c.ProviderName == "Lone Star Pediatrics PLLC"),
            It.IsAny<CancellationToken>()), Times.Once);
        _controller.Response.Headers["X-Capitation-Sync"].ToString().Should().StartWith("synced");
    }

    [Fact]
    public async Task UpdateContract_WhenPushFails_StillSaves_AndSaysSo()
    {
        _repo.Setup(r => r.GetByIdAsync("pc-1")).ReturnsAsync(Contract());
        _repo.Setup(r => r.UpdateAsync(It.IsAny<ProviderContract>())).ReturnsAsync((ProviderContract c) => c);
        _sync.Setup(s => s.SyncAsync(It.IsAny<ProviderContract>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CapitationChildSyncResult(0, null, "capitation-service could not be reached"));

        var result = await _controller.UpdateContract("pc-1", Contract(lob: LineOfBusiness.Medicare), _sync.Object);

        result.Result.Should().BeOfType<OkObjectResult>();
        _controller.Response.Headers["X-Capitation-Sync"].ToString().Should().StartWith("failed");
    }

    [Fact]
    public async Task UpdateContract_ThatChangesNoCopiedField_DoesNotCallCapitation()
    {
        _repo.Setup(r => r.GetByIdAsync("pc-1")).ReturnsAsync(Contract());
        _repo.Setup(r => r.UpdateAsync(It.IsAny<ProviderContract>())).ReturnsAsync((ProviderContract c) => c);
        var edit = Contract();
        edit.ContractOwner = "network-team-east";

        await _controller.UpdateContract("pc-1", edit, _sync.Object);

        _sync.Verify(s => s.SyncAsync(It.IsAny<ProviderContract>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
