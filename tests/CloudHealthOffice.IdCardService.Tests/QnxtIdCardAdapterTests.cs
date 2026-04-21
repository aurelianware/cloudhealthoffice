using IdCardService.Adapters;
using IdCardService.Models;
using IdCardService.Repositories;
using IdCardService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CloudHealthOffice.IdCardService.Tests;

public class QnxtIdCardAdapterTests
{
    [Fact]
    public async Task SuccessfulIssue_EnqueuesMirrorMessage()
    {
        var cho = BuildHappyPathCho(out var documents);
        var queue = new InMemoryQnxtMirrorQueue();
        var adapter = new QnxtIdCardAdapter(cho, queue, NullLogger<QnxtIdCardAdapter>.Instance);

        var result = await adapter.IssueAsync(new IdCardIssueRequest
        {
            TenantId = TestFixtures.TenantId,
            OrderId = "order-1",
            MemberId = TestFixtures.MemberId
        });

        Assert.True(result.Success);
        var enqueued = queue.PeekEnqueued();
        Assert.Single(enqueued);
        Assert.Equal(result.Record!.CardId, enqueued.First().CardId);
    }

    [Fact]
    public async Task MirrorEnqueueFailure_DoesNotBlockIssuance()
    {
        var cho = BuildHappyPathCho(out _);
        var queue = Substitute.For<IQnxtMirrorQueue>();
        queue.When(q => q.EnqueueMirrorAsync(Arg.Any<QnxtMirrorMessage>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("service bus down"));

        var adapter = new QnxtIdCardAdapter(cho, queue, NullLogger<QnxtIdCardAdapter>.Instance);

        var result = await adapter.IssueAsync(new IdCardIssueRequest
        {
            TenantId = TestFixtures.TenantId,
            OrderId = "order-1",
            MemberId = TestFixtures.MemberId
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Record);
    }

    private static ChoIdCardAdapter BuildHappyPathCho(out IMemberDocumentClient documents)
    {
        var member = Substitute.For<IMemberClient>();
        var coverage = Substitute.For<ICoverageClient>();
        var sponsor = Substitute.For<ISponsorClient>();
        var plans = Substitute.For<IBenefitPlanClient>();
        documents = Substitute.For<IMemberDocumentClient>();
        var generator = Substitute.For<IIdCardGenerator>();

        var templateRepo = new InMemoryIdCardTemplateRepository();
        templateRepo.UpsertAsync(TestFixtures.GlobalDefault()).GetAwaiter().GetResult();
        var resolver = new TemplateResolver(templateRepo, NullLogger<TemplateResolver>.Instance);
        var qr = TestFixtures.QrService();

        member.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new MemberDto { MemberId = TestFixtures.MemberId });
        coverage.GetActiveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new CoverageDto { GroupNumber = "g", PlanId = "p", Status = 1 });
        generator.RenderAsync(Arg.Any<IdCardTemplate>(), Arg.Any<CardBindings>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(new RenderedCard { Pdf = new byte[] { 0x25, 0x50 }, Png = Array.Empty<byte>() });
        documents.UploadPdfAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("doc-1");

        return new ChoIdCardAdapter(member, coverage, sponsor, plans,
            resolver, qr, generator, documents,
            NullLogger<ChoIdCardAdapter>.Instance);
    }
}
