using System.Security.Claims;
using CloudHealthOffice.Infrastructure.Middleware;
using CloudHealthOffice.Infrastructure.Security;
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
        var controller = CreateController(repository, "tenant-a", new Claim("tenant_id", "tenant-a"));

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
        // A global record needs platform:admin; the batch is refused only for being invalid.
        var controller = CreateController(
            new InMemoryReferenceDataRepository(),
            "tenant-a",
            new Claim(ChoClaimTypes.Role, ChoRolePermissions.PlatformAdmin));

        var response = await controller.Import([Code() with { Checksum = " " }]);

        response.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Token_tenant_resolves_tenant_context()
    {
        var repository = new InMemoryReferenceDataRepository();
        await repository.ImportAsync([Code() with
        {
            TenantId = "tenant-a",
            ExposureClassification = ExposureClassification.TenantRestricted
        }]);
        // The shared tenant middleware puts the token's tenant in HttpContext.Items.
        var controller = CreateController(repository, "tenant-a", new Claim(ChoClaimTypes.TenantId, "tenant-a"));

        var response = await controller.Search("CPT", pageSize: 10);

        var result = response.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<Page<ReferenceCode>>().Subject;
        result.Items.Should().ContainSingle();
        result.Items[0].Coding.Display.Should().Be("Licensed display");
    }

    [Fact]
    public async Task Authenticated_caller_without_token_tenant_cannot_choose_tenant_by_header()
    {
        var repository = new InMemoryReferenceDataRepository();
        await repository.ImportAsync([Code() with
        {
            TenantId = "tenant-a",
            ExposureClassification = ExposureClassification.TenantRestricted
        }]);
        // Authenticated, but no tenant from the token: a header must not supply one.
        var controller = CreateController(repository, tenantId: null, new Claim(ChoClaimTypes.Subject, "user-1"));
        controller.Request.Headers["X-Tenant-ID"] = "tenant-a";

        var act = () => controller.Search("CPT", pageSize: 10);

        await act.Should().ThrowAsync<TenantContextMissingException>();
    }

    private static CanonicalReferenceDataController CreateController(
        CloudHealthOffice.ReferenceData.Persistence.IReferenceDataRepository repository,
        params Claim[] claims)
        => CreateController(repository, null, claims);

    private static CanonicalReferenceDataController CreateController(
        CloudHealthOffice.ReferenceData.Persistence.IReferenceDataRepository repository,
        string? tenantId,
        params Claim[] claims)
    {
        var identity = claims.Length == 0 ? new ClaimsIdentity() : new ClaimsIdentity(claims, "test");
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        if (tenantId != null)
            httpContext.Items["TenantId"] = tenantId;
        return new CanonicalReferenceDataController(repository)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
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
