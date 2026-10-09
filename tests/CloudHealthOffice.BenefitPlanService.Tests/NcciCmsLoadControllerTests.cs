using System.Security.Claims;
using System.Text;
using BenefitPlanService.Controllers;
using CloudHealthOffice.NcciEngine.Domain;
using CloudHealthOffice.NcciEngine.Import;
using CloudHealthOffice.NcciEngine.Models;
using CloudHealthOffice.NcciEngine.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.BenefitPlanService.Tests;

/// <summary>
/// POST /api/v1/ncci/cms-load: request validation, tenant from the token
/// context (never the form), and loader errors mapped to 400.
/// </summary>
public class NcciCmsLoadControllerTests
{
    private const string Tenant = "tenant-from-token";

    private static (NcciController controller, INcciQuarterlyLoader loader) Build()
    {
        var loader = Substitute.For<INcciQuarterlyLoader>();
        var controller = new NcciController(
            Substitute.For<INcciEditService>(), loader, NullLogger<NcciController>.Instance);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "ops-user")], "test")),
        };
        http.Items["TenantId"] = Tenant;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return (controller, loader);
    }

    private static IFormFile File(string content = "36415,2,2 Date of Service Edit: Policy,CMS Policy\n")
    {
        var bytes = Encoding.ASCII.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "mue.csv");
    }

    [Fact]
    public async Task LoadCmsFile_PassesTokenTenantAndSubjectToLoader()
    {
        var (controller, loader) = Build();
        NcciLoadRequest? seen = null;
        loader.LoadAsync(Arg.Do<NcciLoadRequest>(r => seen = r), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new NcciLoadResult
            {
                Quarter = "2026Q4", FileKind = NcciCmsFileKind.Mue, Setting = NcciSettings.Practitioner,
                Sha256 = "abc", RowsLoaded = 1,
            });

        var response = await controller.LoadCmsFile(File(), "2026q4", "MUE", "practitioner", part: null, force: true);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        Assert.Equal(1, Assert.IsType<NcciLoadResult>(ok.Value).RowsLoaded);
        Assert.NotNull(seen);
        Assert.Equal(Tenant, seen!.TenantId);
        Assert.Equal("2026Q4", seen.Quarter);
        Assert.Equal(NcciCmsFileKind.Mue, seen.FileKind);
        Assert.Equal("ops-user", seen.LoadedBy);
        Assert.True(seen.Force);
        Assert.Equal("mue.csv", seen.FileName);
    }

    [Fact]
    public async Task LoadCmsFile_MissingFile_Returns400()
    {
        var (controller, _) = Build();
        var response = await controller.LoadCmsFile(null, "2026Q4", "ptp", "practitioner");
        Assert.IsType<BadRequestObjectResult>(response.Result);
    }

    [Fact]
    public async Task LoadCmsFile_UnknownKind_Returns400()
    {
        var (controller, _) = Build();
        var response = await controller.LoadCmsFile(File(), "2026Q4", "dme", "practitioner");
        Assert.IsType<BadRequestObjectResult>(response.Result);
    }

    [Fact]
    public async Task LoadCmsFile_LoaderRejectsInput_Returns400()
    {
        var (controller, loader) = Build();
        loader.LoadAsync(Arg.Any<NcciLoadRequest>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns<NcciLoadResult>(_ => throw new InvalidDataException("No Mue rows"));

        var response = await controller.LoadCmsFile(File("garbage"), "2026Q4", "mue", "practitioner");

        Assert.IsType<BadRequestObjectResult>(response.Result);
    }
}
