using System.Net;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Portal.Tests.Services;

/// <summary>
/// The portal's reader of the document catalog. It adds no access rule — the
/// catalog decides and audits — so what matters here is that each answer it
/// gets back keeps its meaning: refused is not empty, and unreachable is
/// neither.
/// </summary>
public class DocumentCatalogServiceTests
{
    private const string BaseUrl = "http://document-service.test";

    private static (DocumentCatalogService Service, FakeHandler Handler) Build(
        HttpStatusCode status, string body = "")
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Services:DocumentService"] = BaseUrl })
            .Build();
        return (new DocumentCatalogService(
            new HttpClient(handler), configuration, NullLogger<DocumentCatalogService>.Instance), handler);
    }

    [Fact]
    public async Task Adding_a_link_posts_target_and_escaped_source_context()
    {
        string? body = null;
        HttpMethod? method = null;
        var handler = new FakeHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            method = request.Method;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Services:DocumentService"] = BaseUrl }).Build();
        var service = new DocumentCatalogService(new HttpClient(handler), configuration,
            NullLogger<DocumentCatalogService>.Instance);

        var refusal = await service.AddLinkAsync("doc/1", DocumentSubjectKind.Call, "contact & 7",
            DocumentSubjectKind.Claim, " CLM-2 ");

        refusal.Should().BeNull();
        method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls.Should().ContainSingle().Which.Should().Be(
            BaseUrl + "/api/v1/documents/doc%2F1/links?subjectKind=Call&subjectId=contact%20%26%207");
        System.Text.Json.JsonDocument.Parse(body!).RootElement.GetProperty("link").GetString().Should().Be("Claim:CLM-2");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "refused")]
    [InlineData(HttpStatusCode.NotFound, "refused")]
    [InlineData(HttpStatusCode.Conflict, "legal hold")]
    [InlineData(HttpStatusCode.BadRequest, "record reference")]
    [InlineData(HttpStatusCode.InternalServerError, "did not confirm")]
    public async Task Link_failures_are_not_reported_as_success(HttpStatusCode status, string message)
    {
        var (service, _) = Build(status, "sensitive backend detail");
        var refusal = await service.AddLinkAsync("doc-1", DocumentSubjectKind.Claim, "CLM-1",
            DocumentSubjectKind.Call, "CONTACT-7");
        refusal.Should().Contain(message).And.NotContain("sensitive backend detail");
    }

    [Fact]
    public async Task It_asks_for_the_subject_and_reads_the_listing()
    {
        var (service, handler) = Build(HttpStatusCode.OK, """
            {"documents":[{"id":"doc-1","type":"Medical record","scanStatus":"Clean","sizeBytes":10}],"withheld":2}
            """);

        var listing = await service.GetForSubjectAsync(DocumentSubjectKind.Claim, "CLM-1");

        handler.CapturedUrls.Should().Equal($"{BaseUrl}/api/v1/documents?subjectKind=Claim&subjectId=CLM-1");
        listing.Documents.Should().ContainSingle().Which.Id.Should().Be("doc-1");
        listing.Documents[0].ScanStatus.Should().Be(DocumentScanStatus.Clean);
        listing.Withheld.Should().Be(2);
        listing.Forbidden.Should().BeFalse();
    }

    [Fact]
    public async Task A_subject_id_is_escaped_into_the_query()
    {
        var (service, handler) = Build(HttpStatusCode.OK, """{"documents":[],"withheld":0}""");

        await service.GetForSubjectAsync(DocumentSubjectKind.Provider, "NPI 199 9999984");

        handler.CapturedUrls.Single().Should().EndWith("subjectId=NPI%20199%209999984");
    }

    [Fact]
    public async Task Refused_is_not_empty()
    {
        var (service, _) = Build(HttpStatusCode.Forbidden);

        var listing = await service.GetForSubjectAsync(DocumentSubjectKind.Claim, "CLM-1");

        listing.Forbidden.Should().BeTrue();
    }

    [Fact]
    public async Task An_unknown_subject_is_empty_rather_than_refused()
    {
        var (service, _) = Build(HttpStatusCode.NotFound);

        var listing = await service.GetForSubjectAsync(DocumentSubjectKind.Claim, "CLM-1");

        listing.Documents.Should().BeEmpty();
        listing.Forbidden.Should().BeFalse();
    }

    [Fact]
    public async Task An_unreachable_catalog_is_neither_empty_nor_refused()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("document-service down"));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Services:DocumentService"] = BaseUrl })
            .Build();
        var service = new DocumentCatalogService(
            new HttpClient(handler), configuration, NullLogger<DocumentCatalogService>.Instance);

        var act = () => service.GetForSubjectAsync(DocumentSubjectKind.Claim, "CLM-1");

        await act.Should().ThrowAsync<ServiceUnavailableException>();
    }

    [Fact]
    public async Task No_subject_is_not_asked_about()
    {
        var (service, handler) = Build(HttpStatusCode.OK, """{"documents":[],"withheld":0}""");

        var listing = await service.GetForSubjectAsync(DocumentSubjectKind.Claim, "   ");

        handler.CapturedUrls.Should().BeEmpty();
        listing.Documents.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unconfigured_catalog_fails_where_the_mistake_is()
    {
        var service = new DocumentCatalogService(
            new HttpClient(new FakeHandler(HttpStatusCode.OK)),
            new ConfigurationBuilder().Build(),
            NullLogger<DocumentCatalogService>.Instance);

        var act = () => service.GetForSubjectAsync(DocumentSubjectKind.Claim, "CLM-1");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*Services:DocumentService*");
    }
}
