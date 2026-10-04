using CloudHealthOffice.Infrastructure.Security;
using MemberDocumentService.Models;
using MemberDocumentService.Repositories;
using MemberDocumentService.Services;
using MemberDocumentService.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Security.Claims;

namespace MemberDocumentService.Tests.Controllers;

public class MemberDocumentsControllerTests
{
    private readonly Mock<IMemberDocumentRepository> _repositoryMock = new();
    private readonly Mock<IMemberDocumentBlobService> _blobServiceMock = new();
    private readonly IRetentionPolicyService _retentionPolicyService = new RetentionPolicyService();

    private MemberDocumentsController CreateController(string tenantId = "test-tenant")
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ChoClaimTypes.Subject, "compliance-1")], authenticationType: "test"))
        };
        context.Items["TenantId"] = tenantId;

        var controller = new MemberDocumentsController(
            _repositoryMock.Object,
            _blobServiceMock.Object,
            _retentionPolicyService,
            new HttpContextCurrentActor(new HttpContextAccessor { HttpContext = context }),
            MemberDocumentUploadPolicy.Default,
            NullLogger<MemberDocumentsController>.Instance);

        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    [Fact]
    public async Task GetMemberDocument_ReturnsOk_WhenDocumentExists()
    {
        var doc = new MemberDocument
        {
            Id = "doc1",
            TenantId = "test-tenant",
            MemberId = "m1",
            Category = "Lab",
            BlobPath = "members/m1/doc1.pdf",
            BlobContainer = "member-documents"
        };
        _repositoryMock.Setup(r => r.GetByIdAsync("test-tenant", "doc1"))
            .ReturnsAsync(doc);

        var controller = CreateController();
        var result = await controller.GetMemberDocument("doc1");

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = ok.Value.Should().BeOfType<MemberDocument>().Subject;
        returned.Id.Should().Be("doc1");
    }

    [Fact]
    public async Task GetMemberDocument_ReturnsNotFound_WhenMissing()
    {
        _repositoryMock.Setup(r => r.GetByIdAsync("test-tenant", "missing"))
            .ReturnsAsync((MemberDocument?)null);

        var controller = CreateController();
        var result = await controller.GetMemberDocument("missing");

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task ListMemberDocuments_ReturnsOk()
    {
        var docs = new List<MemberDocument>
        {
            new MemberDocument { Id = "d1", TenantId = "test-tenant", MemberId = "m1", Category = "Lab", BlobPath = "x", BlobContainer = "c" },
            new MemberDocument { Id = "d2", TenantId = "test-tenant", MemberId = "m1", Category = "EOB", BlobPath = "y", BlobContainer = "c" },
        };
        _repositoryMock.Setup(r => r.ListByMemberIdAsync("test-tenant", "m1", null))
            .ReturnsAsync(docs.AsReadOnly());

        var controller = CreateController();
        var result = await controller.ListMemberDocuments("m1", null);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeEquivalentTo(docs);
    }

    [Fact]
    public async Task UpdateLegalHold_ReturnsNotFound_WhenDocumentMissing()
    {
        _repositoryMock.Setup(r => r.GetByIdAsync("test-tenant", "missing"))
            .ReturnsAsync((MemberDocument?)null);

        var controller = CreateController();
        var result = await controller.UpdateLegalHold("missing", new LegalHoldRequest { LegalHold = true }, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task UpdateLegalHold_SetsLegalHoldAndUpdatesBlobTags()
    {
        var doc = new MemberDocument
        {
            Id = "doc1",
            TenantId = "test-tenant",
            MemberId = "m1",
            Category = "Lab",
            BlobPath = "members/m1/doc1.pdf",
            BlobContainer = "member-documents",
            LegalHold = false
        };
        _repositoryMock.Setup(r => r.GetByIdAsync("test-tenant", "doc1")).ReturnsAsync(doc);
        _repositoryMock.Setup(r => r.UpdateAsync(It.IsAny<MemberDocument>())).ReturnsAsync((MemberDocument d) => d);
        _blobServiceMock.Setup(b => b.SetTagsAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var controller = CreateController();
        var result = await controller.UpdateLegalHold("doc1", new LegalHoldRequest { LegalHold = true }, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var updated = ok.Value.Should().BeOfType<MemberDocument>().Subject;
        updated.LegalHold.Should().BeTrue();

        _blobServiceMock.Verify(b => b.SetTagsAsync(
            "member-documents",
            "members/m1/doc1.pdf",
            It.Is<IDictionary<string, string>>(tags => tags["legalHold"] == "true"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FinalizeUpload_ReturnsNotFound_WhenDocumentMissing()
    {
        _repositoryMock.Setup(r => r.GetByIdAsync("test-tenant", "missing"))
            .ReturnsAsync((MemberDocument?)null);

        var controller = CreateController();
        var result = await controller.FinalizeUpload("missing", CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task FinalizeUpload_AppliesBlobTagsAndSyncsSize()
    {
        var doc = new MemberDocument
        {
            Id = "doc2",
            TenantId = "test-tenant",
            MemberId = "m2",
            Category = "EOB",
            BlobPath = "members/m2/doc2.pdf",
            BlobContainer = "member-documents",
            RetentionPolicyId = "DEFAULT-10Y",
            LegalHold = false,
            SizeBytes = 0
        };
        _repositoryMock.Setup(r => r.GetByIdAsync("test-tenant", "doc2")).ReturnsAsync(doc);
        _repositoryMock.Setup(r => r.UpdateAsync(It.IsAny<MemberDocument>())).ReturnsAsync((MemberDocument d) => d);
        _blobServiceMock.Setup(b => b.SetTagsAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _blobServiceMock.Setup(b => b.GetBlobSizeAsync("member-documents", "members/m2/doc2.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(4096L);

        var controller = CreateController();
        var result = await controller.FinalizeUpload("doc2", CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var updated = ok.Value.Should().BeOfType<MemberDocument>().Subject;
        updated.SizeBytes.Should().Be(4096L);

        _blobServiceMock.Verify(b => b.SetTagsAsync(
            "member-documents",
            "members/m2/doc2.pdf",
            It.Is<IDictionary<string, string>>(tags =>
                tags.ContainsKey("retentionPolicyId") && tags.ContainsKey("legalHold")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private MemberDocument HeldDocument() => new()
    {
        Id = "doc3",
        TenantId = "test-tenant",
        MemberId = "m3",
        Category = "Letter",
        BlobPath = "tenants/test-tenant/members/m3/doc3.pdf",
        BlobContainer = "member-documents",
        LegalHold = true,
        LegalHoldSetBy = "someone",
        LegalHoldReason = "litigation 2026-17",
        LegalHoldHistory = { new LegalHoldEvent { Action = LegalHoldAction.Set, Actor = "someone", Reason = "litigation 2026-17" } }
    };

    [Fact]
    public async Task UpdateLegalHold_RecordsWhoPlacedTheHoldAndWhy()
    {
        var doc = HeldDocument();
        doc.LegalHold = false;
        doc.LegalHoldHistory.Clear();
        _repositoryMock.Setup(r => r.GetByIdAsync("test-tenant", "doc3")).ReturnsAsync(doc);
        _repositoryMock.Setup(r => r.UpdateAsync(It.IsAny<MemberDocument>())).ReturnsAsync((MemberDocument d) => d);

        var result = await CreateController().UpdateLegalHold("doc3",
            new LegalHoldRequest { LegalHold = true, Reason = "  subpoena 44  " }, CancellationToken.None);

        var updated = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<MemberDocument>().Subject;
        updated.LegalHold.Should().BeTrue();
        updated.LegalHoldSetBy.Should().Be("compliance-1");
        updated.LegalHoldReason.Should().Be("subpoena 44");
        updated.LegalHoldHistory.Should().ContainSingle(e =>
            e.Action == LegalHoldAction.Set && e.Actor == "compliance-1" && e.Reason == "subpoena 44");
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
        _blobServiceMock.Verify(b => b.SetTagsAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
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
        _blobServiceMock.Verify(b => b.SetTagsAsync("member-documents", "tenants/test-tenant/members/m3/doc3.pdf",
            It.Is<IDictionary<string, string>>(tags => tags["legalHold"] == "false"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FinalizeUpload_OfAFinalizedHeldDocument_ChangesNothing()
    {
        _repositoryMock.Setup(r => r.GetByIdAsync("test-tenant", "doc3")).ReturnsAsync(HeldDocument());

        var result = await CreateController().FinalizeUpload("doc3", CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        _repositoryMock.Verify(r => r.UpdateAsync(It.IsAny<MemberDocument>()), Times.Never);
        _blobServiceMock.VerifyNoOtherCalls();
    }
}
