using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class SmtpEmailNotificationServiceTests
{
    private readonly Mock<ILogger<SmtpEmailNotificationService>> _logger;

    public SmtpEmailNotificationServiceTests()
    {
        _logger = new Mock<ILogger<SmtpEmailNotificationService>>();
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static SalesInquiry BuildSampleInquiry() => new()
    {
        Id = "inquiry-abc123",
        FirstName = "Jane",
        LastName = "Doe",
        Email = "jane.doe@acmepayer.com",
        Phone = "555-1234",
        CompanyName = "Acme Payer",
        JobTitle = "CTO",
        InquiryType = "Enterprise Plan",
        Message = "We need a full demo.",
        Status = "New",
        Source = "Contact Sales Page",
        CreatedAt = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public async Task SendSalesInquiryNotificationAsync_WhenSmtpHostNotConfigured_SkipsAndLogsWarning()
    {
        // Arrange
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Email:SmtpHost"] = ""
        });
        var sut = new SmtpEmailNotificationService(config, _logger.Object);
        var inquiry = BuildSampleInquiry();

        // Act — should complete without throwing even though SMTP is unconfigured
        var exception = await Record.ExceptionAsync(() => sut.SendSalesInquiryNotificationAsync(inquiry));

        // Assert
        exception.Should().BeNull();
        _logger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains(inquiry.Id)),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task SendSalesInquiryNotificationAsync_WhenSmtpHostIsNull_SkipsWithoutThrowing()
    {
        // Arrange
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            // Email:SmtpHost intentionally omitted (null)
        });
        var sut = new SmtpEmailNotificationService(config, _logger.Object);

        // Act
        var exception = await Record.ExceptionAsync(() =>
            sut.SendSalesInquiryNotificationAsync(BuildSampleInquiry()));

        // Assert
        exception.Should().BeNull();
    }

    [Fact]
    public async Task SendSalesInquiryNotificationAsync_WhenSmtpFails_DoesNotThrow()
    {
        // Arrange — point at a non-existent SMTP server to force a send failure
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Email:SmtpHost"] = "smtp.invalid.example.test",
            ["Email:SmtpPort"] = "587",
            ["Email:EnableSsl"] = "false",
            ["Email:FromAddress"] = "noreply@cloudhealthoffice.com",
            ["Email:SalesTeamAddress"] = "sales@cloudhealthoffice.com"
        });
        var sut = new SmtpEmailNotificationService(config, _logger.Object);

        // Act
        var exception = await Record.ExceptionAsync(() =>
            sut.SendSalesInquiryNotificationAsync(BuildSampleInquiry()));

        // Assert — SMTP errors must be swallowed so inquiry submission still succeeds
        exception.Should().BeNull();
        _logger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }
}
