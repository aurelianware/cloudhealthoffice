using System.Net.Mail;
using System.Net;

namespace CloudHealthOffice.Portal.Services;

public class SmtpEmailNotificationService : IEmailNotificationService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<SmtpEmailNotificationService> _logger;

    public SmtpEmailNotificationService(IConfiguration configuration, ILogger<SmtpEmailNotificationService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendSalesInquiryNotificationAsync(SalesInquiry inquiry)
    {
        var smtpHost = _configuration["Email:SmtpHost"];
        if (string.IsNullOrWhiteSpace(smtpHost))
        {
            _logger.LogWarning("SMTP host is not configured. Skipping email notification for inquiry {InquiryId}", inquiry.Id);
            return;
        }

        if (string.IsNullOrWhiteSpace(inquiry.Email) || !IsValidEmail(inquiry.Email))
        {
            _logger.LogWarning("Invalid submitter email for inquiry {InquiryId}. Skipping confirmation email.", inquiry.Id);
        }

        var smtpPort = int.TryParse(_configuration["Email:SmtpPort"], out var port) ? port : 587;
        var enableSsl = !string.Equals(_configuration["Email:EnableSsl"], "false", StringComparison.OrdinalIgnoreCase);
        var fromAddress = _configuration["Email:FromAddress"] ?? "noreply@cloudhealthoffice.com";
        var salesTeamAddress = _configuration["Email:SalesTeamAddress"] ?? "sales@cloudhealthoffice.com";
        var username = _configuration["Email:Username"];
        var password = _configuration["Email:Password"];

        using var client = new SmtpClient(smtpHost, smtpPort)
        {
            EnableSsl = enableSsl,
            Credentials = !string.IsNullOrWhiteSpace(username)
                ? new NetworkCredential(username, password)
                : CredentialCache.DefaultNetworkCredentials
        };

        try
        {
            using var salesNotification = BuildSalesTeamEmail(fromAddress, salesTeamAddress, inquiry);
            await client.SendMailAsync(salesNotification);
            _logger.LogInformation("Sales team notification sent for inquiry {InquiryId}", inquiry.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send sales team notification for inquiry {InquiryId}", inquiry.Id);
        }

        if (!string.IsNullOrWhiteSpace(inquiry.Email) && IsValidEmail(inquiry.Email))
        {
            try
            {
                using var confirmation = BuildConfirmationEmail(fromAddress, inquiry);
                await client.SendMailAsync(confirmation);
                _logger.LogInformation("Confirmation email sent to {Email} for inquiry {InquiryId}", inquiry.Email, inquiry.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send confirmation email to {Email} for inquiry {InquiryId}", inquiry.Email, inquiry.Id);
            }
        }
    }

    private static bool IsValidEmail(string email)
    {
        try
        {
            var addr = new MailAddress(email);
            return addr.Address == email;
        }
        catch
        {
            return false;
        }
    }

    private static MailMessage BuildSalesTeamEmail(string from, string to, SalesInquiry inquiry)
    {
        var body =
            $"New Sales Inquiry Received\n\n" +
            $"Inquiry ID:  {inquiry.Id}\n" +
            $"Submitted:   {inquiry.CreatedAt:yyyy-MM-dd HH:mm} UTC\n\n" +
            $"Contact Information\n" +
            $"-------------------\n" +
            $"Name:        {inquiry.FirstName} {inquiry.LastName}\n" +
            $"Email:       {inquiry.Email}\n" +
            $"Phone:       {inquiry.Phone ?? "Not provided"}\n" +
            $"Company:     {inquiry.CompanyName}\n" +
            $"Job Title:   {inquiry.JobTitle ?? "Not provided"}\n\n" +
            $"Inquiry Details\n" +
            $"---------------\n" +
            $"Type:        {inquiry.InquiryType}\n" +
            $"Message:\n{inquiry.Message}\n\n" +
            $"Source: {inquiry.Source}\n\n" +
            $"Reply directly to this email to reach the prospect.";

        var message = new MailMessage(from, to)
        {
            Subject = $"[Cloud Health Office] New Sales Inquiry from {inquiry.CompanyName} – {inquiry.InquiryType}",
            Body = body,
            IsBodyHtml = false
        };
        message.ReplyToList.Add(new MailAddress(inquiry.Email, $"{inquiry.FirstName} {inquiry.LastName}"));
        return message;
    }

    private static MailMessage BuildConfirmationEmail(string from, SalesInquiry inquiry)
    {
        var body =
            $"Hi {inquiry.FirstName},\n\n" +
            $"Thank you for reaching out to Cloud Health Office!\n\n" +
            $"We have received your inquiry and our sales team will be in touch within 1 business day.\n\n" +
            $"Your reference ID is: {inquiry.Id}\n\n" +
            $"Inquiry Summary\n" +
            $"---------------\n" +
            $"Type:    {inquiry.InquiryType}\n" +
            $"Company: {inquiry.CompanyName}\n\n" +
            $"If you have urgent questions in the meantime, please email us at sales@cloudhealthoffice.com.\n\n" +
            $"Best regards,\n" +
            $"The Cloud Health Office Sales Team";

        return new MailMessage(from, inquiry.Email)
        {
            Subject = $"[Cloud Health Office] We received your inquiry – {inquiry.Id}",
            Body = body,
            IsBodyHtml = false
        };
    }
}
