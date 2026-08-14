using System.Security.Claims;
using CloudHealthOffice.ReferenceData.Domain;
using CloudHealthOffice.ReferenceData.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ReferenceDataService.Controllers;
using Xunit;

namespace CloudHealthOffice.ReferenceDataService.Tests;

public sealed class CanonicalReferenceDataControllerTests
{
    [Fact]
    public async Task Anonymous_lookup_preserves_identifier_but_redacts_protected_text()
    {
        var repository = new InMemoryReferenceDataRepository();
        await repository.ImportAsync([Code()]);
        var controller = CreateController(repository);

        var response = await controller.Get("CPT", "99213", new DateOnly(2026, 8, 14));

        var result = response.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<ReferenceCode>().Subject;
        result.Coding.Code.Should().Be("99213");
        result.Coding.Display.Should().BeNull();
        result.Description.Should().BeNull();
    }

    [Fact]
    public async Task Authenticated_lookup_can_read_authenticated_reference_text()
    {
        var repository = new InMemoryReferenceDataRepository();
        await repository.ImportAsync([Code()]);
        var controller = CreateController(repository, new Claim("tenant_id", "tenant-a"));

        var response = await controller.Get("CPT", "99213", new DateOnly(2026, 8, 14));

        var result = response.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<ReferenceCode>().Subject;
        result.Coding.Display.Should().Be("Licensed display");
        result.Description.Should().Be("Licensed description");
    }

    [Fact]
    public async Task Search_does_not_trust_anonymous_tenant_header()
    {
        var repository = new InMemoryReferenceDataRepository();
        await repository.ImportAsync([Code() with
        {
            TenantId = "tenant-a",
            ExposureClassification = ExposureClassification.TenantRestricted
        }]);
        var controller = CreateController(repository);
        controller.Request.Headers["X-Tenant-ID"] = "tenant-a";

        var response = await controller.Search("CPT", pageSize: 10);

        var result = response.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<Page<ReferenceCode>>().Subject;
        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Import_returns_bad_request_for_an_invalid_batch()
    {
        var controller = CreateController(
            new InMemoryReferenceDataRepository(),
            new Claim(ClaimTypes.Role, "Administrator"));

        var response = await controller.Import([Code() with { Checksum = " " }]);

        response.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    private static CanonicalReferenceDataController CreateController(
        CloudHealthOffice.ReferenceData.Persistence.IReferenceDataRepository repository,
        params Claim[] claims)
    {
        var identity = claims.Length == 0 ? new ClaimsIdentity() : new ClaimsIdentity(claims, "test");
        return new CanonicalReferenceDataController(repository)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
            }
        };
    }

    private static ReferenceCode Code() => new()
    {
        Id = "cpt-99213-2026",
        Coding = new ChoCoding
        {
            CodeSystem = "CPT",
            Code = "99213",
            Version = "2026",
            Display = "Licensed display"
        },
        Description = "Licensed description",
        EffectiveFrom = new DateOnly(2026, 1, 1),
        SourceId = "licensed-source",
        SourceVersion = "2026",
        LicenseClassification = LicenseClassification.Licensed,
        ExposureClassification = ExposureClassification.AuthenticatedReference,
        ImportedAt = DateTimeOffset.UtcNow,
        Checksum = "checksum"
    };
}
