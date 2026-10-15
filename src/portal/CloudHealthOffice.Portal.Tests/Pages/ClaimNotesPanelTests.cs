using Bunit;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// The Notes panel on claim detail: the claim's real, append-only notes from
/// claims-service, and an Add Note form for people who can work claims.
/// </summary>
public class ClaimNotesPanelTests : TestContext
{
    private readonly Mock<IClaimsService> _claims = new();
    private readonly Mock<IUserContextService> _user = new();

    public ClaimNotesPanelTests()
    {
        Services.AddMudServices();
        Services.AddSingleton(_claims.Object);
        Services.AddSingleton(Mock.Of<IWorkQueueService>());
        Services.AddSingleton(_user.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;

        _claims.Setup(c => c.GetClaimByIdAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => new ClaimDetails { ClaimId = id, ClaimNumber = $"CLM-{id}", Status = "Pended" });
        _claims.Setup(c => c.GetDuplicateMatchesAsync(It.IsAny<string>())).ReturnsAsync(new List<ClaimDuplicateMatch>());
        _claims.Setup(c => c.GetAdjudicationDataAsync(It.IsAny<string>())).ReturnsAsync((AdjudicationTransparencyData?)null);
    }

    private void CanWork(bool canWork) => _user.Setup(u => u.HasPermission("claims:work")).Returns(canWork);

    private static ClaimNoteView Note(string text, string category = ClaimNoteCategory.Decision, string? authorName = "Pat Examiner") => new()
    {
        Id = Guid.NewGuid().ToString(),
        ClaimId = "A",
        Category = category,
        Text = text,
        AuthorId = "examiner-1",
        AuthorName = authorName,
        CreatedAt = new DateTime(2026, 10, 15, 18, 30, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void Shows_the_claims_notes_with_author_and_category()
    {
        CanWork(false);
        _claims.Setup(c => c.GetClaimNotesAsync("A")).ReturnsAsync(new List<ClaimNoteView>
        {
            Note("Called Dr. Lee's office; corrected claim coming.", ClaimNoteCategory.ProviderContact),
            Note("Second unit denied per MUE.", authorName: null),
        });

        var cut = RenderComponent<ClaimDetailsNew>(p => p.Add(x => x.ClaimId, "A"));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=claim-note]").Should().HaveCount(2));
        var notes = cut.FindAll("[data-testid=claim-note]").ToList();
        notes[0].TextContent.Should().Contain("Provider contact").And.Contain("Pat Examiner").And.Contain("corrected claim coming");
        notes[1].TextContent.Should().Contain("Decision").And.Contain("examiner-1", "the author id stands in when the token had no name");
    }

    [Fact]
    public void Read_only_users_see_notes_but_no_add_form()
    {
        CanWork(false);
        _claims.Setup(c => c.GetClaimNotesAsync("A")).ReturnsAsync(new List<ClaimNoteView>());

        var cut = RenderComponent<ClaimDetailsNew>(p => p.Add(x => x.ClaimId, "A"));

        cut.WaitForAssertion(() => cut.Find("[data-testid=claim-notes-empty]"));
        cut.FindAll("[data-testid=add-note]").Should().BeEmpty();
    }

    [Fact]
    public void Adding_a_note_saves_it_and_shows_it_without_reloading_the_claim()
    {
        CanWork(true);
        _claims.Setup(c => c.GetClaimNotesAsync("A")).ReturnsAsync(new List<ClaimNoteView>());
        _claims.Setup(c => c.AddClaimNoteAsync("A", ClaimNoteCategory.Internal, "Waiting on records."))
            .ReturnsAsync(Note("Waiting on records.", ClaimNoteCategory.Internal));

        var cut = RenderComponent<ClaimDetailsNew>(p => p.Add(x => x.ClaimId, "A"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=add-note]"));

        cut.Find("textarea").Change("  Waiting on records.  ");
        cut.Find("[data-testid=add-note]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=claim-note]").Should().ContainSingle()
            .Which.TextContent.Should().Contain("Waiting on records."));
        cut.Find("textarea").GetAttribute("value").Should().BeNullOrEmpty();
        _claims.Verify(c => c.AddClaimNoteAsync("A", ClaimNoteCategory.Internal, "Waiting on records."), Times.Once);
        _claims.Verify(c => c.GetClaimByIdAsync("A"), Times.Once);
    }

    [Fact]
    public void A_failed_save_keeps_the_draft()
    {
        CanWork(true);
        _claims.Setup(c => c.GetClaimNotesAsync("A")).ReturnsAsync(new List<ClaimNoteView>());
        _claims.Setup(c => c.AddClaimNoteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("You don't have permission to add notes to claims."));

        var cut = RenderComponent<ClaimDetailsNew>(p => p.Add(x => x.ClaimId, "A"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=add-note]"));
        cut.Find("textarea").Change("draft");
        cut.Find("[data-testid=add-note]").Click();

        cut.WaitForAssertion(() => cut.Find("textarea").GetAttribute("value").Should().Be("draft"));
        cut.FindAll("[data-testid=claim-note]").Should().BeEmpty();
    }

    [Fact]
    public void Money_row_explains_the_gap_between_billed_and_allowed()
    {
        CanWork(false);
        _claims.Setup(c => c.GetClaimNotesAsync("A")).ReturnsAsync(new List<ClaimNoteView>());
        _claims.Setup(c => c.GetClaimByIdAsync("A")).ReturnsAsync(new ClaimDetails
        {
            ClaimId = "A", ClaimNumber = "CLM-A", Status = "Pended", NetworkTier = "InNetwork",
            TotalChargeAmount = 12963m, AllowedAmount = 5285m, PaidAmount = 5165m, PatientResponsibility = 120m,
        });

        var cut = RenderComponent<ClaimDetailsNew>(p => p.Add(x => x.ClaimId, "A"));

        cut.WaitForAssertion(() => cut.Find("[data-testid=money-above-allowed-amount]").TextContent.Should().Contain("7,678.00"));
        cut.Find("[data-testid=money-above-allowed-label]").TextContent.Should().Be("Contractual adjustment");
        cut.Find("[data-testid=money-network]").TextContent.Should().Contain("In-network");
        cut.Find("[data-testid=money-pended-note]").TextContent.Should().Contain("before review");
    }

    [Theory]
    [InlineData("ProviderContact", "Provider contact")]
    [InlineData("HandOff", "Hand-off")]
    [InlineData("handoff", "Hand-off")]
    [InlineData("Something", "Something")]
    public void Category_labels(string value, string label) =>
        ClaimNoteCategory.Label(value).Should().Be(label);
}
