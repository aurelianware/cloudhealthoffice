using IdCardService.Adapters;
using IdCardService.Models;
using IdCardService.Repositories;
using IdCardService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CloudHealthOffice.IdCardService.Tests;

public class ChoIdCardAdapterTests
{
    private readonly IMemberClient _member = Substitute.For<IMemberClient>();
    private readonly ICoverageClient _coverage = Substitute.For<ICoverageClient>();
    private readonly ISponsorClient _sponsor = Substitute.For<ISponsorClient>();
    private readonly IBenefitPlanClient _plans = Substitute.For<IBenefitPlanClient>();
    private readonly IMemberDocumentClient _documents = Substitute.For<IMemberDocumentClient>();
    private readonly IIdCardGenerator _generator = Substitute.For<IIdCardGenerator>();
    private readonly IQrCodeService _qr;
    private readonly ITemplateResolver _resolver;
    private readonly InMemoryIdCardTemplateRepository _templateRepo = new();

    public ChoIdCardAdapterTests()
    {
        _qr = TestFixtures.QrService();
        _resolver = new TemplateResolver(_templateRepo, NullLogger<TemplateResolver>.Instance);
    }

    private ChoIdCardAdapter Build() => new(
        _member, _coverage, _sponsor, _plans,
        _resolver, _qr, _generator, _documents,
        NullLogger<ChoIdCardAdapter>.Instance);

    [Fact]
    public async Task HappyPath_ReturnsRecordWithDocumentIds()
    {
        await _templateRepo.UpsertAsync(TestFixtures.GlobalDefault());

        _member.GetAsync(TestFixtures.TenantId, TestFixtures.MemberId, Arg.Any<CancellationToken>())
            .Returns(new MemberDto { MemberId = TestFixtures.MemberId, FirstName = "Jane", LastName = "Doe" });
        _coverage.GetActiveAsync(TestFixtures.TenantId, TestFixtures.MemberId, Arg.Any<CancellationToken>())
            .Returns(new CoverageDto
            {
                GroupNumber = TestFixtures.GroupNumber,
                PlanId = TestFixtures.PlanId,
                Status = 1
            });
        _sponsor.GetAsync(TestFixtures.TenantId, TestFixtures.GroupNumber, Arg.Any<CancellationToken>())
            .Returns(new SponsorDto { EmployerName = "Acme" });
        _plans.GetAsync(TestFixtures.TenantId, TestFixtures.PlanId, Arg.Any<CancellationToken>())
            .Returns(new BenefitPlanDto { PlanName = "Gold HMO" });

        _generator.RenderAsync(Arg.Any<IdCardTemplate>(), Arg.Any<CardBindings>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(new RenderedCard { Pdf = new byte[] { 0x25, 0x50, 0x44, 0x46 }, Png = new byte[] { 0x89, 0x50 } });

        _documents.UploadPdfAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("doc-pdf-1");
        _documents.UploadPngAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("doc-png-1");

        var adapter = Build();
        var result = await adapter.IssueAsync(new IdCardIssueRequest
        {
            TenantId = TestFixtures.TenantId,
            OrderId = "order-1",
            MemberId = TestFixtures.MemberId,
            Channel = IdCardDeliveryChannel.Digital
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Record);
        Assert.Equal("doc-pdf-1", result.Record!.DocumentId);
        Assert.Equal("doc-png-1", result.Record.PreviewDocumentId);
        Assert.Equal("v1", result.Record.KeyVersion);
        Assert.False(string.IsNullOrWhiteSpace(result.Record.QrCanonicalPayload));

        await _documents.Received(1).UploadPdfAsync(
            TestFixtures.TenantId, TestFixtures.MemberId,
            Arg.Any<byte[]>(),
            Arg.Is<string>(s => s.EndsWith(".pdf")),
            "IdCard", Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MissingCoverage_FailsWithCoverageNotActiveCode()
    {
        _member.GetAsync(TestFixtures.TenantId, TestFixtures.MemberId, Arg.Any<CancellationToken>())
            .Returns(new MemberDto { MemberId = TestFixtures.MemberId });
        _coverage.GetActiveAsync(TestFixtures.TenantId, TestFixtures.MemberId, Arg.Any<CancellationToken>())
            .Returns((CoverageDto?)null);

        var adapter = Build();
        var result = await adapter.IssueAsync(new IdCardIssueRequest
        {
            TenantId = TestFixtures.TenantId,
            OrderId = "order-1",
            MemberId = TestFixtures.MemberId
        });

        Assert.False(result.Success);
        Assert.Equal("COVERAGE_NOT_ACTIVE", result.FailureCode);
        Assert.Null(result.Record);
    }

    [Fact]
    public async Task MissingGlobalTemplate_FailsWithNoTemplateAvailable()
    {
        _member.GetAsync(TestFixtures.TenantId, TestFixtures.MemberId, Arg.Any<CancellationToken>())
            .Returns(new MemberDto { MemberId = TestFixtures.MemberId });
        _coverage.GetActiveAsync(TestFixtures.TenantId, TestFixtures.MemberId, Arg.Any<CancellationToken>())
            .Returns(new CoverageDto { GroupNumber = "g", PlanId = "p", Status = 1 });

        var adapter = Build();
        var result = await adapter.IssueAsync(new IdCardIssueRequest
        {
            TenantId = TestFixtures.TenantId,
            OrderId = "order-1",
            MemberId = TestFixtures.MemberId
        });

        Assert.False(result.Success);
        Assert.Equal("NO_TEMPLATE_AVAILABLE", result.FailureCode);
    }
}
