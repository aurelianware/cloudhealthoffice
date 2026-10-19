namespace CloudHealthOffice.Portal.Services;

public interface IEmailNotificationService
{
    Task SendSalesInquiryNotificationAsync(SalesInquiry inquiry);
}
