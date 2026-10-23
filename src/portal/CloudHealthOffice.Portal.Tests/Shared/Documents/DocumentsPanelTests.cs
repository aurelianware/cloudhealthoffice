using Bunit;
using CloudHealthOffice.Portal.Services;
using CloudHealthOffice.Portal.Shared.Documents;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Shared.Documents;

/// <summary>
/// bUnit tests for <see cref="DocumentsPanel"/>, the one documents panel on
/// the claim, authorization, appeal and provider records. The panel applies
/// no access rule of its own; what it must get right is saying honestly what
/// it was told — withheld, refused, unreachable and unopenable each read
/// differently from "nothing on file".
/// </summary>
public class DocumentsPanelTests : TestContext
{
    private readonly StubCatalog _catalog = new();

    public DocumentsPanelTests()
    {
        Services.AddMudServices();
        Services.AddSingleton<IDocumentCatalogService>(_catalog);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private sealed class StubCatalog : IDocumentCatalogService
    {
        public DocumentSubjectListing Listing { get; set; } = DocumentSubjectListing.Empty;
        public bool Unavailable { get; set; }
        public List<(DocumentSubjectKind Kind, string Id)> Calls { get; } = new();

        public Task<DocumentSubjectListing> GetForSubjectAsync(
            DocumentSubjectKind kind, string subjectId, CancellationToken ct = default)
        {
            Calls.Add((kind, subjectId));
            if (Unavailable) throw new ServiceUnavailableException("Document Service", new HttpRequestException());
            return Task.FromResult(Listing);
        }

        public Task<HttpResponseMessage> DownloadAsync(
            string documentId, DocumentSubjectKind kind, string subjectId, CancellationToken ct = default) =>
            Task.FromResult(new HttpResponseMessage());
    }

    private static CatalogDocumentSummary Document(
        string id = "doc-1",
        DocumentScanStatus scan = DocumentScanStatus.Clean,
        bool uploadPending = false,
        IReadOnlyList<DocumentLinkSummary>? links = null,
        int otherLinks = 0) => new()
    {
        Id = id,
        Type = "Medical record",
        Title = "Operative report",
        ContentType = "application/pdf",
        SizeBytes = 2048,
        DocumentVersion = 1,
        Source = new DocumentSourceSummary { Channel = "X12 275" },
        Links = links ?? [new DocumentLinkSummary { Kind = DocumentSubjectKind.Claim, Id = "CLM-1" }],
        OtherLinks = otherLinks,
        ScanStatus = scan,
        UploadPending = uploadPending,
        ReceivedAt = new DateTime(2026, 10, 20, 15, 0, 0, DateTimeKind.Utc),
    };

    private IRenderedComponent<DocumentsPanel> Render(
        DocumentSubjectKind kind = DocumentSubjectKind.Claim, string subjectId = "CLM-1") =>
        RenderComponent<DocumentsPanel>(p => p
            .Add(x => x.SubjectKind, kind)
            .Add(x => x.SubjectId, subjectId));

    [Fact]
    public void It_asks_the_catalog_for_the_subject_it_was_given()
    {
        Render(DocumentSubjectKind.Appeal, "APL-7");

        _catalog.Calls.Should().ContainSingle()
            .Which.Should().Be((DocumentSubjectKind.Appeal, "APL-7"));
    }

    [Fact]
    public void An_empty_subject_is_not_asked_about()
    {
        Render(subjectId: string.Empty);

        _catalog.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Nothing_on_file_says_so()
    {
        var cut = Render();

        cut.Find("[data-testid=documents-empty]").TextContent.Should().Contain("No documents on file");
    }

    [Fact]
    public void A_document_is_listed_with_a_link_that_names_the_subject_it_opens_from()
    {
        _catalog.Listing = new DocumentSubjectListing { Documents = [Document()] };

        var cut = Render();

        var open = cut.Find("[data-testid=document-open-doc-1]");
        var href = open.GetAttribute("href");
        // The catalog refuses a document not asked for through a subject, so
        // the download link carries the subject the row is shown under.
        href.Should().Contain("/documents/doc-1/download")
            .And.Contain("subjectKind=Claim")
            .And.Contain("subjectId=CLM-1");
    }

    [Theory]
    [InlineData(DocumentScanStatus.Pending, false, "Waiting for the virus scan")]
    [InlineData(DocumentScanStatus.Infected, false, "Quarantined")]
    [InlineData(DocumentScanStatus.Failed, false, "could not complete")]
    [InlineData(DocumentScanStatus.Clean, true, "upload has not finished")]
    public void Nothing_opens_before_the_scan_is_clean(DocumentScanStatus scan, bool uploadPending, string reason)
    {
        _catalog.Listing = new DocumentSubjectListing { Documents = [Document(scan: scan, uploadPending: uploadPending)] };

        var cut = Render();

        cut.FindAll("[data-testid=document-open-doc-1]").Should().BeEmpty();
        cut.Markup.Should().Contain("Not available").And.Contain(reason);
    }

    [Fact]
    public void Withheld_documents_are_counted_so_a_filtered_list_is_not_read_as_nothing()
    {
        _catalog.Listing = new DocumentSubjectListing { Documents = [Document()], Withheld = 2 };

        var cut = Render();

        cut.Find("[data-testid=documents-withheld]").TextContent
            .Should().Contain("2 more documents").And.Contain("sensitivity");
    }

    [Fact]
    public void One_withheld_document_reads_as_one()
    {
        _catalog.Listing = new DocumentSubjectListing { Documents = [], Withheld = 1 };

        var cut = Render();

        cut.Find("[data-testid=documents-withheld]").TextContent.Should().Contain("One more document");
    }

    [Fact]
    public void A_subject_the_user_may_not_read_says_so_rather_than_showing_an_empty_list()
    {
        _catalog.Listing = DocumentSubjectListing.Denied;

        var cut = Render();

        cut.Find("[data-testid=documents-forbidden]").TextContent.Should().Contain("do not have access");
        cut.FindAll("[data-testid=documents-empty]").Should().BeEmpty();
    }

    [Fact]
    public void An_unreachable_catalog_never_reads_as_no_documents()
    {
        _catalog.Unavailable = true;

        var cut = Render();

        cut.Find("[data-testid=documents-unavailable]").TextContent
            .Should().Contain("could not be reached");
        cut.FindAll("[data-testid=documents-empty]").Should().BeEmpty();
    }

    [Fact]
    public void The_other_subjects_a_document_supports_are_named_and_the_rest_counted()
    {
        _catalog.Listing = new DocumentSubjectListing
        {
            Documents =
            [
                Document(links:
                [
                    new DocumentLinkSummary { Kind = DocumentSubjectKind.Claim, Id = "CLM-1" },
                    new DocumentLinkSummary { Kind = DocumentSubjectKind.Appeal, Id = "APL-9" },
                ], otherLinks: 1),
            ],
        };

        var cut = Render();

        // The subject on screen is not repeated back; what the caller cannot
        // read is counted, never named.
        cut.Markup.Should().Contain("Appeal APL-9").And.Contain("1 you cannot see");
    }

    [Fact]
    public void Changing_the_subject_reloads_without_the_panel_being_rebuilt()
    {
        var cut = Render();
        cut.SetParametersAndRender(p => p.Add(x => x.SubjectId, "CLM-2"));

        _catalog.Calls.Select(c => c.Id).Should().Equal("CLM-1", "CLM-2");
    }

    [Fact]
    public void Re_rendering_with_the_same_subject_does_not_ask_again()
    {
        var cut = Render();
        cut.SetParametersAndRender(p => p.Add(x => x.Title, "Claim documents"));

        _catalog.Calls.Should().ContainSingle();
    }
}
