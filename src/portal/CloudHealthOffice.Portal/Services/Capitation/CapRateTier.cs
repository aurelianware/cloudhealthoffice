namespace CloudHealthOffice.Portal.Services;

public class CapRateTier
{
    public string TierName { get; set; } = string.Empty;
    public int AgeFrom { get; set; }
    public int AgeTo { get; set; }
    public string? Gender { get; set; }
    public string? AgeSexCategory { get; set; }
    public decimal BasePMPM { get; set; }
    public string? ServiceCategory { get; set; }
}
