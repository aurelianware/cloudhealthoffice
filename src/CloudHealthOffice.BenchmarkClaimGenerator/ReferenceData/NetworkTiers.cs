namespace CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;

/// <summary>
/// Network tier definitions for provider participation levels.
/// </summary>
public static class NetworkTiers
{
    /// <summary>Network tier with name, level, and description.</summary>
    public record NetworkTierEntry(string Name, int Level, string Description);

    /// <summary>Standard network tiers.</summary>
    public static readonly NetworkTierEntry[] Tiers =
    {
        new("Tier1", 1, "Preferred In-Network - Highest discounts, lowest member cost sharing"),
        new("Tier2", 2, "Standard In-Network - Standard contracted rates"),
        new("Tier3", 3, "Out-of-Network - Non-contracted, paid at OON fee schedule or UCR"),
    };

    /// <summary>Provider network status values.</summary>
    public static class Status
    {
        /// <summary>Provider is in-network with active contract.</summary>
        public const string InNetwork = "InNetwork";

        /// <summary>Provider is out-of-network with no active contract.</summary>
        public const string OutOfNetwork = "OutOfNetwork";

        /// <summary>Provider's network participation has been terminated.</summary>
        public const string Terminated = "Terminated";
    }

    /// <summary>Provider credentialing status values.</summary>
    public static class CredentialingStatus
    {
        /// <summary>Provider is fully credentialed.</summary>
        public const string Active = "Active";

        /// <summary>Provider has provisional/temporary credentialing.</summary>
        public const string Provisional = "Provisional";

        /// <summary>Provider's credentialing has expired.</summary>
        public const string Expired = "Expired";
    }
}
