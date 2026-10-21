using System.Net;
using System.Security.Claims;
using CloudHealthOffice.Infrastructure.Security;
using MemberDocumentService.Controllers;
using MemberDocumentService.Models;
using MemberDocumentService.Repositories;
using MemberDocumentService.Services;
using MemberDocumentService.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace MemberDocumentService.Tests.Controllers;

public class MemberDocumentsControllerTests
{
    private readonly Mock<IMemberDocumentRepository> _repositoryMock = new();
    private readonly FakeDocumentCatalog _catalog = new();
    private readonly IRetentionPolicyService _retentionPolicyService = new RetentionPolicyService();

    private static readonly string[] AllRights =
    [
        "members:read", "members:write",
        ChoRolePermissions.DocumentsRead, ChoRolePermissions.DocumentsWrite,
        ChoRolePermissions.LegalHold
    ];

    private MemberDocumentsController CreateController(string tenantId = "test-tenant", params string[] permissions)
    {
        var claims = new List<Claim> { new(ChoClaimTypes.Subject, "compliance-1") };
        claims.AddRange((permissions.Length > 0 ? permissions : AllRights).Select(p => new Claim(ChoClaimTypes.Permission, p)));
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"))
        };
        context.Items["TenantId"] = tenantId;

        var controller = new MemberDocumentsController(
            _repositoryMock.Object,
            _catalog,
            _retentionPolicyService,
            new HttpContextCurrentActor(new HttpContextAccessor { HttpContext = context }),
            MemberDocumentUploadPolicy.Default,
            NullLogger<MemberDocumentsController>.Instance);

        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    private static MemberDocument Filed(string id = "doc1", string memberId = "m1", string category = "Lab") => new()
    {
        Id = id,
        TenantId = "test-tenant",
        MemberId = memberId,
        Category = category,
        DocumentId = $"cat-{id}",
        ContentType = "application/pdf"
    };

    [Fact]
    public async Task GetMemberDocument_ReturnsOk_WhenDocumentExists()
    {
        _repositoryMock.Setup(r => r.GetByIdAsync("test-tenant", "doc1")).ReturnsAsync(Filed());

        var result = await CreateController().GetMemberDocument("doc1");

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<MemberDocument>().Which.Id.Should().Be("doc1");
    }

    [Fact]
    public async Task GetMemberDocument_ReturnsNotFound_WhenMissing()
    {
        _repositoryMock.Setup(r => r.GetByIdAsync("test-tenant", "missing")).ReturnsAsync((MemberDocument?)null);

        var result = await CreateController().GetMemberDocument("missing");

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task ListMemberDocuments_ReturnsOk()
    {
        var docs = new List<MemberDocument> { Filed("d1"), Filed("d2", category: "EOB") };
        _repositoryMock.Setup(r => r.ListByMemberIdAsync("test-tenant", "m1", null)).ReturnsAsync(docs.AsReadOnly());

        var result = await CreateController().ListMemberDocuments("m1", null);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeEquivalentTo(docs);
    }

    [Fact]
    public async Task Reading_WithMembersReadButNotDocumentsRead_IsForbidden()
    {
        // The catalog's rule: members:read alone does not open documents.
        var controller = CreateController("test-tenant", "members:read", "members:write");

        var get = await controller.GetMemberDocument("doc1");
        var list = await controller.ListMemberDocuments("m1", null);
        var content = await controller.GetMemberDocumentContent("doc1", CancellationToken.None);

        foreach (var result in new[] { get, list, content })
            result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        _repositoryMock.VerifyNoOtherCalls();
        _catalog.NothingCalled.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateLegalHold_ReturnsNotFound_WhenDocumentMissing()
    {
        _repositoryMock.Setup(r => r.GetByIdAsync("test-tenant", "missing")).ReturnsAsync((MemberDocument?)null);

        var result = await CreateController().UpdateLegalHold("missing", new LegalHoldRequest { LegalHold = true }, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        _catalog.Holds.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateLegalHold_PlacesTheHoldInTheCatalog_ThenMirrorsIt()
    {
        _repositoryMock.Setup(r => r.GetByIdAsync("test-tenant", "doc1")).ReturnsAsync(Filed());
        _repositoryMock.Setup(r => r.UpdateAsync(It.IsAny<MemberDocument>())).ReturnsAsync((MemberDocument d) => d);

        var result = await CreateController().UpdateLegalHold("doc1",
            new LegalHoldRequest { LegalHold = true, Reason = "  subpoena 44  " }, CancellationToken.None);

        var updated = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<MemberDocument>().Subject;
        updated.LegalHold.Should().BeTrue();
        updated.LegalHoldSetBy.Should().Be("compliance-1");
        updated.LegalHoldReason.Should().Be("subpoena 44");
        updated.LegalHoldHistory.Should().ContainSingle(e =>
            e.Action == LegalHoldAction.Set && e.Actor == "compliance-1" && e.Reason == "subpoena 44");
        _catalog.Holds.Should().ContainSingle().Which.Should().Be(("test-tenant", "compliance-1", "cat-doc1", true, "subpoena 44"));
    }

    private static MemberDocument HeldDocument()
    {
        var doc = Filed("doc3", "m3", "Letter");
        doc.LegalHold = true;
        doc.LegalHoldSetBy = "someone";
        doc.LegalHoldReason = "litigation 2026-17";
        doc.LegalHoldHistory.Add(new LegalHoldEvent { Action = LegalHoldAction.Set, Actor = "someone", Reason = "litigation 2026-17" });
        return doc;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UpdateLegalHold_ReleaseWithoutAReason_IsRejected(string? reason)
    {
        _repositoryMock.Setup(r => r.GetByIdAsync("test-tenant", "doc3")).ReturnsAsync(HeldDocument());

        var result = await CreateController().UpdateLegalHold("doc3",
            new LegalHoldRequest { LegalHold = false, Reason = reason }, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        _repositoryMock.Verify(r => r.UpdateAsync(It.IsAny<MemberDocument>()), Times.Never);
        _catalog.Holds.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateLegalHold_ReleaseRecordsWhoReleasedItAndWhy()
    {
        _repositoryMock.Setup(r => r.GetByIdAsync("test-tenant", "doc3")).ReturnsAsync(HeldDocument());
        _repositoryMock.Setup(r => r.UpdateAsync(It.IsAny<MemberDocument>())).ReturnsAsync((MemberDocument d) => d);

        var result = await CreateController().UpdateLegalHold("doc3",
            new LegalHoldRequest { LegalHold = false, Reason = "case dismissed" }, CancellationToken.None);

        var updated = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<MemberDocument>().Subject;
        updated.LegalHold.Should().BeFalse();
        updated.LegalHoldSetBy.Should().BeNull();
        updated.LegalHoldHistory.Should().HaveCount(2);
        updated.LegalHoldHistory[^1].Should().Match<LegalHoldEvent>(e =>
            e.Action == LegalHoldAction.Released && e.Actor == "compliance-1" && e.Reason == "case dismissed");
        _catalog.Holds.Should().ContainSingle().Which.LegalHold.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateLegalHold_WhenTheCatalogRefuses_ChangesNothingHere()
    {
        _repositoryMock.Setup(r => r.GetByIdAsync("test-tenant", "doc1")).ReturnsAsync(Filed());
        _catalog.Failures["hold"] = null;

        var result = await CreateController().UpdateLegalHold("doc1", new LegalHoldRequest { LegalHold = true }, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
        _repositoryMock.Verify(r => r.UpdateAsync(It.IsAny<MemberDocument>()), Times.Never);
    }

    [Fact]
    public async Task UpdateLegalHold_OfARecordWhoseFileIsNotFiledYet_Is409()
    {
        var doc = Filed();
        doc.DocumentId = null;
        _repositoryMock.Setup(r => r.GetByIdAsync("test-tenant", "doc1")).ReturnsAsync(doc);

        var result = await CreateController().UpdateLegalHold("doc1", new LegalHoldRequest { LegalHold = true }, CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
        _catalog.Holds.Should().BeEmpty();
    }

    [Fact]
    public async Task FinalizeUpload_ReturnsNotFound_WhenDocumentMissing()
    {
        _repositoryMock.Setup(r => r.GetByIdAsync("test-tenant", "missing")).ReturnsAsync((MemberDocument?)null);

        var result = await CreateController().FinalizeUpload("missing", CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task FinalizeUpload_FinishesTheCatalogUpload_AndRecordsWhatItStored()
    {
        var doc = Filed("doc2", "m2", "EOB");
        doc.UploadPending = true;
        _repositoryMock.Setup(r => r.GetByIdAsync("test-tenant", "doc2")).ReturnsAsync(doc);
        _repositoryMock.Setup(r => r.UpdateAsync(It.IsAny<MemberDocument>())).ReturnsAsync((MemberDocument d) => d);
        _catalog.FinalizeResult = id => new CatalogDocumentInfo(id, new string('a', 64), 4096L);

        var result = await CreateController().FinalizeUpload("doc2", CancellationToken.None);

        var updated = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<MemberDocument>().Subject;
        updated.UploadPending.Should().BeFalse();
        updated.SizeBytes.Should().Be(4096L);
        updated.ContentHashSha256.Should().Be(new string('a', 64));
        _catalog.Finalized.Should().ContainSingle().Which.Should().Be(("test-tenant", "compliance-1", "cat-doc2"));
    }

    [Fact]
    public async Task FinalizeUpload_OfDuplicateContent_PointsAtTheCatalogsExistingDocument()
    {
        var doc = Filed("doc2", "m2", "EOB");
        doc.UploadPending = true;
        _repositoryMock.Setup(r => r.GetByIdAsync("test-tenant", "doc2")).ReturnsAsync(doc);
        _repositoryMock.Setup(r => r.UpdateAsync(It.IsAny<MemberDocument>())).ReturnsAsync((MemberDocument d) => d);
        _catalog.FinalizeResult = _ => new CatalogDocumentInfo("cat-existing", null, null);

        var result = await CreateController().FinalizeUpload("doc2", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<MemberDocument>()
            .Which.DocumentId.Should().Be("cat-existing");
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, StatusCodes.Status409Conflict)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, StatusCodes.Status413PayloadTooLarge)]
    [InlineData(HttpStatusCode.UnsupportedMediaType, StatusCodes.Status415UnsupportedMediaType)]
    [InlineData(HttpStatusCode.InternalServerError, StatusCodes.Status502BadGateway)]
    public async Task FinalizeUpload_PassesOnWhatTheCatalogRefused(HttpStatusCode catalogStatus, int expected)
    {
        // Nothing uploaded yet, too large, or not the declared type: the catalog
        // checks the staged object and discards a bad one.
        var doc = Filed("doc2");
        doc.UploadPending = true;
        _repositoryMock.Setup(r => r.GetByIdAsync("test-tenant", "doc2")).ReturnsAsync(doc);
        _catalog.Failures["finalize"] = catalogStatus;

        var result = await CreateController().FinalizeUpload("doc2", CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(expected);
        _repositoryMock.Verify(r => r.UpdateAsync(It.IsAny<MemberDocument>()), Times.Never);
    }

    [Fact]
    public async Task FinalizeUpload_StartedBySomeoneElse_Is404()
    {
        var doc = Filed("doc2");
        doc.UploadPending = true;
        _repositoryMock.Setup(r => r.GetByIdAsync("test-tenant", "doc2")).ReturnsAsync(doc);
        _catalog.Failures["finalize"] = HttpStatusCode.NotFound;

        var result = await CreateController().FinalizeUpload("doc2", CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task FinalizeUpload_OfAFinalizedHeldDocument_ChangesNothing()
    {
        _repositoryMock.Setup(r => r.GetByIdAsync("test-tenant", "doc3")).ReturnsAsync(HeldDocument());

        var result = await CreateController().FinalizeUpload("doc3", CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        _repositoryMock.Verify(r => r.UpdateAsync(It.IsAny<MemberDocument>()), Times.Never);
        _catalog.NothingCalled.Should().BeTrue();
    }

    [Fact]
    public async Task FinalizeUpload_OfAFinalizedDocument_ChangesNothing()
    {
        _repositoryMock.Setup(r => r.GetByIdAsync("test-tenant", "doc1")).ReturnsAsync(Filed());

        var result = await CreateController().FinalizeUpload("doc1", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        _repositoryMock.Verify(r => r.UpdateAsync(It.IsAny<MemberDocument>()), Times.Never);
        _catalog.NothingCalled.Should().BeTrue();
    }

    [Theory]
    [InlineData("IdCard", "IdCard")]
    [InlineData("Lab", "LabResult")]
    [InlineData("EOB", "EOB")]
    [InlineData("Appeal", "Appeal")]
    [InlineData("Proof of authority", "Other")]
    public void Categories_MapToCatalogTypes(string category, string type)
    {
        MemberDocumentUploadPolicy.CatalogTypeFor(category).Should().Be(type);
    }
}
