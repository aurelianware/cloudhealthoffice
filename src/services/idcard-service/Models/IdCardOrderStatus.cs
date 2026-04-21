namespace IdCardService.Models;

public enum IdCardOrderStatus
{
    Pending = 0,
    Rendering = 1,
    Uploading = 2,
    Issued = 3,
    Failed = 4,
    Cancelled = 5
}

public enum IdCardDeliveryChannel
{
    Digital = 0,
    Wallet = 1,   // Phase 2
    Physical = 2  // Phase 2
}
