namespace AuthorizationService.Consumers;

public class RfaiDocsReceivedMessage
{
    public string TenantId { get; set; } = string.Empty;
    public string RfaiCaseId { get; set; } = string.Empty;
    public string AuthNumber { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
    public List<string> AttachmentIds { get; set; } = new();
    public bool AllRequestedItemsReceived { get; set; }
}
