using System.Net;
using System.Net.Http.Json;
using Bunit;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

public class MemberServiceCaseDocumentsTests : TestContext
{
    private readonly Mock<IDocumentCatalogService> _catalog = new();
    private readonly CaseApi _api = new();

    public MemberServiceCaseDocumentsTests()
    {
        Services.AddMudServices();
        Services.AddSingleton(new HttpClient(_api));
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:AppealsService"] = "http://appeals/api"
        }).Build());
        Services.AddSingleton(_catalog.Object);
        _catalog.Setup(c => c.GetForSubjectAsync(It.IsAny<DocumentSubjectKind>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentSubjectListing
            {
                Documents = [new CatalogDocumentSummary { Id = "doc-clean", Title = "Synthetic evidence", ScanStatus = DocumentScanStatus.Clean }]
            });
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<MemberServiceCases> Open(params string[] permissions)
    {
        Services.AddSingleton(GatingUsers.With(permissions).Object);
        return RenderComponent<MemberServiceCases>(p => p.Add(x => x.CaseId, "case-1"));
    }

    [Fact]
    public void Case_uses_contact_documents_and_download_scope_without_copying_files()
    {
        var cut = Open("appeals:read", "service:read", "documents:read");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Synthetic evidence"));
        cut.Find("[data-testid=case-contact-documents]").TextContent.Should().Contain("original contact");
        cut.Find("a[href*='/documents/doc-clean/download']").GetAttribute("href")
            .Should().Contain("subjectKind=Call").And.Contain("subjectId=contact-1");
        _catalog.Verify(c => c.GetForSubjectAsync(DocumentSubjectKind.Call, "contact-1", It.IsAny<CancellationToken>()), Times.Once);
        _catalog.Verify(c => c.GetForSubjectAsync(It.IsAny<DocumentSubjectKind>(), "case-1", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Appeals_permission_does_not_grant_contact_document_access()
    {
        var cut = Open("appeals:read", "documents:read");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("MemberServices"));
        cut.FindAll("[data-testid=documents-panel]").Should().BeEmpty();
        _catalog.Verify(c => c.GetForSubjectAsync(It.IsAny<DocumentSubjectKind>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Refused_case_does_not_request_its_documents()
    {
        _api.Status = HttpStatusCode.Forbidden;
        var cut = Open("appeals:read", "service:read", "documents:read");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Case records could not be opened"));
        cut.FindAll("[data-testid=documents-panel]").Should().BeEmpty();
        _catalog.Verify(c => c.GetForSubjectAsync(It.IsAny<DocumentSubjectKind>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Switching_case_replaces_the_contact_document_scope()
    {
        var cut = Open("appeals:read", "service:read", "documents:read");
        cut.WaitForElement("a[href*='subjectId=contact-1']");
        _api.ContactId = "contact-2";
        cut.SetParametersAndRender(p => p.Add(x => x.CaseId, "case-2"));
        cut.WaitForElement("a[href*='subjectId=contact-2']");
        cut.FindAll("a[href*='subjectId=contact-1']").Should().BeEmpty();
    }

    private sealed class CaseApi : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string ContactId { get; set; } = "contact-1";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = JsonContent.Create(new { contactId = ContactId, memberId = "synthetic-member", classification = "Grievance", status = "Received", summary = "Synthetic intake" })
            });
    }
}
