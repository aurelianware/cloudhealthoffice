using Bunit;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// The held-275 screen. Its job is to let a person resolve what the matcher
/// could not, and to be honest about four states that would otherwise all
/// look like an empty table: held, nothing held, unreachable, and refused.
/// The billing-NPI refusal in particular has to reach the screen in the
/// service's own words, because "it didn't work" would send somebody looking
/// for a bug instead of a different claim.
/// </summary>
public class UnmatchedAttachmentsTests : TestContext
{
    private readonly StubHeld _attachments = new();

    public UnmatchedAttachmentsTests()
    {
        Services.AddMudServices();
        Services.AddSingleton<IUnmatchedAttachmentService>(_attachments);
        Services.AddSingleton(GatingUsers.With("attachments:read").Object);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private sealed class StubHeld : IUnmatchedAttachmentService
    {
        public IReadOnlyList<HeldAttachment>? Held { get; set; } = new List<HeldAttachment>();
        public LinkOutcome Outcome { get; set; } = LinkOutcome.Success;
        public List<(string AttachmentId, string ClaimId, string? Reason)> Links { get; } = new();

        public Task<IReadOnlyList<HeldAttachment>?> GetHeldAsync(CancellationToken ct = default) =>
            Task.FromResult(Held);

        public Task<LinkOutcome> LinkToClaimAsync(
            string attachmentId, string claimId, string? reason, CancellationToken ct = default)
        {
            Links.Add((attachmentId, claimId, reason));
            return Task.FromResult(Outcome);
        }
    }

    private static HeldAttachment Attachment(
        string id = "att-1", string provider = "1234567893", int daysAgo = 2) =>
        new(id, "ACN-77", provider, "Synthetic Clinic", "Pat", "Example",
            "Medical Records", DateTime.UtcNow.AddDays(-daysAgo), null);

    [Fact]
    public void Nothing_held_says_so_rather_than_showing_an_empty_table()
    {
        _attachments.Held = new List<HeldAttachment>();

        var page = RenderComponent<UnmatchedAttachments>();

        Assert.NotNull(page.Find("[data-testid=held-empty]"));
        Assert.Empty(page.FindAll("[data-testid=held-table]"));
    }

    [Fact]
    public void An_unreachable_service_is_not_reported_as_nothing_held()
    {
        _attachments.Held = null;

        var page = RenderComponent<UnmatchedAttachments>();

        Assert.NotNull(page.Find("[data-testid=held-unavailable]"));
        Assert.Empty(page.FindAll("[data-testid=held-empty]"));
    }

    [Fact]
    public void Held_attachments_are_listed()
    {
        _attachments.Held = new List<HeldAttachment> { Attachment() };

        var page = RenderComponent<UnmatchedAttachments>();

        Assert.NotNull(page.Find("[data-testid=held-table]"));
        Assert.Contains("ACN-77", page.Markup);
        Assert.Contains("Synthetic Clinic", page.Markup);
    }

    [Theory]
    [InlineData(0, "30 days")]
    [InlineData(23, "7 days")]
    [InlineData(29, "Tomorrow")]
    [InlineData(30, "Today")]
    [InlineData(40, "Past the hold")]
    public void How_long_is_left_before_rejection_is_shown(int daysAgo, string expected)
    {
        _attachments.Held = new List<HeldAttachment> { Attachment(daysAgo: daysAgo) };

        var page = RenderComponent<UnmatchedAttachments>();

        Assert.Contains(expected, page.Find("[data-testid=held-expiry-att-1]").TextContent);
    }

    [Fact]
    public void Filing_sends_the_claim_and_the_reason()
    {
        _attachments.Held = new List<HeldAttachment> { Attachment() };
        var page = RenderComponent<UnmatchedAttachments>();

        page.Find("[data-testid=held-file-att-1]").Click();
        page.Find("[data-testid=held-claim-id]").Change("CLM-42");
        page.Find("[data-testid=held-reason]").Change("Same patient and date of service.");
        page.Find("[data-testid=held-confirm]").Click();

        var sent = Assert.Single(_attachments.Links);
        Assert.Equal("att-1", sent.AttachmentId);
        Assert.Equal("CLM-42", sent.ClaimId);
        Assert.Equal("Same patient and date of service.", sent.Reason);
    }

    [Fact]
    public void A_refusal_is_shown_in_the_services_own_words()
    {
        _attachments.Held = new List<HeldAttachment> { Attachment() };
        _attachments.Outcome = LinkOutcome.Failed(
            "That claim's billing provider is not the provider who sent this attachment.");
        var page = RenderComponent<UnmatchedAttachments>();

        page.Find("[data-testid=held-file-att-1]").Click();
        page.Find("[data-testid=held-claim-id]").Change("CLM-OTHER");
        page.Find("[data-testid=held-confirm]").Click();

        Assert.Contains("billing provider", page.Find("[data-testid=held-problem]").TextContent);
    }

    [Fact]
    public void A_refusal_leaves_the_form_open_so_another_claim_can_be_tried()
    {
        _attachments.Held = new List<HeldAttachment> { Attachment() };
        _attachments.Outcome = LinkOutcome.Failed("No such claim in this tenant.");
        var page = RenderComponent<UnmatchedAttachments>();

        page.Find("[data-testid=held-file-att-1]").Click();
        page.Find("[data-testid=held-claim-id]").Change("CLM-NOPE");
        page.Find("[data-testid=held-confirm]").Click();

        Assert.NotNull(page.Find("[data-testid=held-filing]"));
    }

    [Fact]
    public void A_successful_filing_says_so_and_reloads()
    {
        _attachments.Held = new List<HeldAttachment> { Attachment() };
        var page = RenderComponent<UnmatchedAttachments>();

        page.Find("[data-testid=held-file-att-1]").Click();
        page.Find("[data-testid=held-claim-id]").Change("CLM-42");

        _attachments.Held = new List<HeldAttachment>();
        page.Find("[data-testid=held-confirm]").Click();

        Assert.Contains("CLM-42", page.Find("[data-testid=held-filed]").TextContent);
        Assert.NotNull(page.Find("[data-testid=held-empty]"));
    }

    [Fact]
    public void Filing_is_refused_before_it_starts_without_a_claim_id()
    {
        _attachments.Held = new List<HeldAttachment> { Attachment() };
        var page = RenderComponent<UnmatchedAttachments>();

        page.Find("[data-testid=held-file-att-1]").Click();
        page.Find("[data-testid=held-confirm]").Click();

        Assert.Empty(_attachments.Links);
    }

    [Fact]
    public void Someone_without_attachments_read_does_not_see_the_queue()
    {
        Services.AddSingleton(GatingUsers.With().Object);
        _attachments.Held = new List<HeldAttachment> { Attachment() };

        var page = RenderComponent<UnmatchedAttachments>();

        Assert.Empty(page.FindAll("[data-testid=held-table]"));
    }
}
