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
        Services.AddSingleton(Mock.Of<IUserContextService>());
        Services.AddSingleton<IDocumentCatalogService>(_catalog);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private sealed class StubCatalog : IDocumentCatalogService
    {
        public Task<string?> UploadAsync(DocumentSubjectKind kind, string subjectId, string title, string type,
            Stream content, string fileName, string contentType, CancellationToken ct = default) => Task.FromResult<string?>(null);

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

        public ClinicalSummaryView Summary { get; set; } = new();
        public bool SummaryUnavailable { get; set; }
        public List<string> SummaryCalls { get; } = new();

        public Task<ClinicalSummaryView> ClinicalSummaryAsync(
            string documentId, DocumentSubjectKind kind, string subjectId, CancellationToken ct = default)
        {
            SummaryCalls.Add(documentId);
            if (SummaryUnavailable) throw new ServiceUnavailableException("Document Service", new HttpRequestException());
            return Task.FromResult(Summary);
        }
    }

    private static CatalogDocumentSummary Document(
        string id = "doc-1",
        DocumentScanStatus scan = DocumentScanStatus.Clean,
        bool uploadPending = false,
        IReadOnlyList<DocumentLinkSummary>? links = null,
        int otherLinks = 0,
        string contentType = "application/pdf") => new()
    {
        Id = id,
        Type = "Medical record",
        Title = "Operative report",
        ContentType = contentType,
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

    // ── The clinical summary of a C-CDA ─────────────────────────────

    private const string Cda = "application/hl7-cda+xml";

    private static ClinicalSummaryView Summary(params ClinicalSummarySectionView[] sections) => new()
    {
        Title = "Discharge Summary",
        MemberLinked = true,
        Sections = sections,
    };

    private IRenderedComponent<DocumentsPanel> RenderWithSummary(
        ClinicalSummaryView summary, string contentType = Cda)
    {
        _catalog.Listing = new DocumentSubjectListing { Documents = [Document(contentType: contentType)] };
        _catalog.Summary = summary;
        return Render();
    }

    [Fact]
    public void Only_a_clinical_document_offers_a_summary()
    {
        var pdf = RenderWithSummary(Summary(), contentType: "application/pdf");

        pdf.FindAll("[data-testid=document-summary-doc-1]").Should().BeEmpty();
    }

    [Fact]
    public void A_clinical_document_offers_a_summary()
    {
        var cut = RenderWithSummary(Summary());

        cut.FindAll("[data-testid=document-summary-doc-1]").Should().ContainSingle();
        // Asked for only when opened: reading the document is an audited act.
        _catalog.SummaryCalls.Should().BeEmpty();
    }

    [Fact]
    public void Opening_the_summary_lists_what_cho_understood()
    {
        var cut = RenderWithSummary(Summary(new ClinicalSummarySectionView
        {
            Kind = "Problems",
            Title = "Problems",
            Mapped = true,
            EntryCount = 1,
            Items = [new ClinicalSummaryItemView { Display = "Hypertension", Status = "active", Detail = "Onset 2020-01-15" }],
        }));

        cut.Find("[data-testid=document-summary-doc-1]").Click();

        _catalog.SummaryCalls.Should().Equal("doc-1");
        cut.Find("[data-testid=clinical-summary]").TextContent
            .Should().Contain("Hypertension").And.Contain("active").And.Contain("Onset 2020-01-15");
    }

    [Fact]
    public void A_section_cho_does_not_read_is_shown_with_its_count_rather_than_dropped()
    {
        // Dropping it would make the document look smaller than it is to
        // somebody deciding an authorization on it.
        var cut = RenderWithSummary(Summary(new ClinicalSummarySectionView
        {
            Kind = "Immunizations",
            Title = "Immunizations",
            Mapped = false,
            EntryCount = 3,
            NarrativeXml = "<text><table><tr><td>Influenza vaccine</td></tr></table></text>",
        }));

        cut.Find("[data-testid=document-summary-doc-1]").Click();

        var section = cut.Find("[data-testid=clinical-section-Immunizations]").TextContent;
        section.Should().Contain("Immunizations").And.Contain("3 entries").And.Contain("Not read by CHO");
    }

    [Fact]
    public void The_senders_narrative_is_shown_as_text_never_as_their_markup()
    {
        var cut = RenderWithSummary(Summary(new ClinicalSummarySectionView
        {
            Kind = "Immunizations",
            Mapped = false,
            EntryCount = 1,
            NarrativeXml = "<text><table><tr><td>Influenza <b>vaccine</b></td></tr><script>alert(1)</script></table></text>",
        }));

        cut.Find("[data-testid=document-summary-doc-1]").Click();

        // It is another organisation's HTML, arriving over an attachment
        // channel, so it reaches the page as text and nothing else.
        cut.Markup.Should().NotContain("<script>").And.NotContain("<b>vaccine</b>");
        cut.Find("[data-testid=clinical-summary]").TextContent.Should().Contain("Influenza vaccine");
    }

    [Fact]
    public void Entries_that_carried_nothing_codeable_are_said_so_rather_than_shown_as_an_empty_section()
    {
        var cut = RenderWithSummary(Summary(new ClinicalSummarySectionView
        {
            Kind = "Problems",
            Mapped = true,
            EntryCount = 2,
            Items = [],
        }));

        cut.Find("[data-testid=document-summary-doc-1]").Click();

        cut.Find("[data-testid=clinical-section-nothing-coded-Problems]").TextContent
            .Should().Contain("nothing coded");
    }

    [Fact]
    public void A_document_filed_against_no_member_says_why_its_entries_are_not_listed()
    {
        var summary = Summary(new ClinicalSummarySectionView { Kind = "Problems", Mapped = true, EntryCount = 1 });
        var cut = RenderWithSummary(new ClinicalSummaryView
        {
            Title = summary.Title,
            MemberLinked = false,
            Sections = summary.Sections,
        });

        cut.Find("[data-testid=document-summary-doc-1]").Click();

        cut.Find("[data-testid=clinical-summary-unlinked]").TextContent.Should().Contain("not filed against a member");
        cut.FindAll("[data-testid=clinical-section-nothing-coded-Problems]").Should().BeEmpty();
    }

    [Fact]
    public void A_document_cho_cannot_read_says_what_was_wrong_with_it()
    {
        var cut = RenderWithSummary(ClinicalSummaryView.Unreadable("it is not a clinical document"));

        cut.Find("[data-testid=document-summary-doc-1]").Click();

        cut.Find("[data-testid=clinical-summary-refused]").TextContent.Should().Contain("not a clinical document");
    }

    [Fact]
    public void A_summary_the_user_may_not_read_says_so()
    {
        var cut = RenderWithSummary(ClinicalSummaryView.Denied);

        cut.Find("[data-testid=document-summary-doc-1]").Click();

        cut.Find("[data-testid=clinical-summary-forbidden]").TextContent.Should().Contain("do not have access");
    }

    [Fact]
    public void An_unreachable_catalog_never_reads_as_an_empty_document()
    {
        var cut = RenderWithSummary(Summary());
        _catalog.SummaryUnavailable = true;

        cut.Find("[data-testid=document-summary-doc-1]").Click();

        cut.Find("[data-testid=clinical-summary-unavailable]").TextContent.Should().Contain("could not be reached");
    }

    [Fact]
    public void The_summary_can_be_closed_again()
    {
        var cut = RenderWithSummary(Summary());

        cut.Find("[data-testid=document-summary-doc-1]").Click();
        cut.Find("[data-testid=clinical-summary-close]").Click();

        cut.FindAll("[data-testid=clinical-summary]").Should().BeEmpty();
    }

}
