namespace CloudHealthOffice.Tools.MccPlatformValidator;

internal static class MccMemberSeedStatus
{
    public const string Active = "Active";
    public const string Pending = "Pending";
    public const string Terminated = "Terminated";
    public const string Suspended = "Suspended";
    public const string Cobra = "COBRA";

    public static string ToMemberServiceStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return Active;
        }

        return status.Trim().ToUpperInvariant() switch
        {
            "ACTIVE" => Active,
            "PENDING" => Pending,
            "TERMINATED" => Terminated,
            "SUSPENDED" => Suspended,
            "COBRA" => Cobra,
            _ => Active
        };
    }
}
