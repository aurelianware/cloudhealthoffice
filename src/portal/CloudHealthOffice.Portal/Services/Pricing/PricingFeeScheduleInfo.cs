namespace CloudHealthOffice.Portal.Services;

public class PricingFeeScheduleInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Version { get; set; } = "";
    public int CodeCount { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset LastUpdated { get; set; }
}
