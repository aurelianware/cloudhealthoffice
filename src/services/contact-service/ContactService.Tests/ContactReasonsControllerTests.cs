using ContactService.Controllers;
using ContactService.Models;
using ContactService.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContactService.Tests;

/// <summary>
/// The reason taxonomy endpoint: what it refuses, and what it normalises
/// before the store sees it.
/// </summary>
public class ContactReasonsControllerTests
{
    private readonly Mock<IContactReasonRepository> _reasons = new();

    private ContactReasonsController Controller() =>
        new(_reasons.Object, NullLogger<ContactReasonsController>.Instance);

    [Fact]
    public async Task The_contact_screen_asks_for_the_active_reasons_only()
    {
        _reasons.Setup(r => r.ListAsync(true, It.IsAny<CancellationToken>()))
                .ReturnsAsync([new ContactReason { Code = "BENEFITS", Label = "Benefits" }]);

        var result = await Controller().List(activeOnly: true, default);

        result.Result.Should().BeOfType<OkObjectResult>()
              .Which.Value.Should().BeAssignableTo<IReadOnlyList<ContactReason>>()
              .Which.Should().ContainSingle();
    }

    [Fact]
    public async Task The_configuration_screen_sees_the_retired_ones_too()
    {
        _reasons.Setup(r => r.ListAsync(false, It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);

        await Controller().List(activeOnly: false, default);

        _reasons.Verify(r => r.ListAsync(false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("", "Benefits")]
    [InlineData("   ", "Benefits")]
    [InlineData("BENEFITS", "")]
    [InlineData("BENEFITS", "   ")]
    public async Task A_reason_needs_both_a_code_and_a_label(string code, string label)
    {
        var result = await Controller().Save(
            new SaveContactReasonRequest { Code = code, Label = label }, default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        _reasons.Verify(r => r.SaveAsync(It.IsAny<ContactReason>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_saved_reason_is_trimmed_before_the_store_sees_it()
    {
        ContactReason? saved = null;
        _reasons.Setup(r => r.SaveAsync(It.IsAny<ContactReason>(), It.IsAny<CancellationToken>()))
                .Callback((ContactReason r, CancellationToken _) => saved = r)
                .ReturnsAsync((ContactReason r, CancellationToken _) => r);

        await Controller().Save(new SaveContactReasonRequest
        {
            Code = "ID_CARD",
            Label = "  ID card  ",
            Category = "  Enrollment  ",
            Order = 3,
            IsComplaintIndicator = true,
        }, default);

        saved!.Label.Should().Be("ID card");
        saved.Category.Should().Be("Enrollment");
        saved.Order.Should().Be(3);
        saved.IsComplaintIndicator.Should().BeTrue();
    }

    [Fact]
    public async Task A_blank_category_is_stored_as_none_rather_than_as_whitespace()
    {
        ContactReason? saved = null;
        _reasons.Setup(r => r.SaveAsync(It.IsAny<ContactReason>(), It.IsAny<CancellationToken>()))
                .Callback((ContactReason r, CancellationToken _) => saved = r)
                .ReturnsAsync((ContactReason r, CancellationToken _) => r);

        await Controller().Save(new SaveContactReasonRequest
        {
            Code = "OTHER", Label = "Other", Category = "   ",
        }, default);

        saved!.Category.Should().BeNull();
    }

    [Fact]
    public async Task Seeding_reports_what_it_added()
    {
        _reasons.Setup(r => r.SeedStarterReasonsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(14);

        var result = await Controller().Seed(default);

        result.Result.Should().BeOfType<OkObjectResult>()
              .Which.Value.Should().BeOfType<ContactReasonsController.SeedResult>()
              .Which.Added.Should().Be(14);
    }

    [Fact]
    public async Task Seeding_a_tenant_that_already_has_reasons_reports_zero()
    {
        _reasons.Setup(r => r.SeedStarterReasonsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var result = await Controller().Seed(default);

        result.Result.Should().BeOfType<OkObjectResult>()
              .Which.Value.Should().BeOfType<ContactReasonsController.SeedResult>()
              .Which.Added.Should().Be(0);
    }
}
