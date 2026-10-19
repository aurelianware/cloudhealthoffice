using System.Text.Json.Serialization;

namespace CloudHealthOffice.Portal.Services;

public class AgedBuckets
{
    public decimal Bucket0_30 { get; set; }
    public decimal Bucket31_60 { get; set; }
    public decimal Bucket61_90 { get; set; }
    public decimal Bucket91Plus { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public decimal Total => Bucket0_30 + Bucket31_60 + Bucket61_90 + Bucket91Plus;
}
