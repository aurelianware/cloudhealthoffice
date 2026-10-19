namespace CloudHealthOffice.Portal.Services;

public class CorrespondenceItem
{
    public string LetterId { get; set; } = string.Empty;
    public string LetterType { get; set; } = string.Empty;
    public string RecipientName { get; set; } = string.Empty;
    public string RecipientType { get; set; } = string.Empty;
    public string RelatedId { get; set; } = string.Empty;
    public DateTime? GeneratedDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public string DeliveryMethod { get; set; } = string.Empty;
}
