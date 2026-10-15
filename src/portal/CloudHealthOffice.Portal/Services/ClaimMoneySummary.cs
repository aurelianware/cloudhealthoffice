namespace CloudHealthOffice.Portal.Services;

/// <summary>How the claim was priced against the plan's networks.</summary>
public enum ClaimNetwork
{
    Unknown,
    InNetwork,
    OutOfNetwork,
    OutOfArea,
}

/// <summary>
/// The amount between what the provider billed and what the plan allowed,
/// and who carries it. In network it's the contractual write-off the
/// provider absorbs (835 group CO) and can't bill the member for. Out of
/// network there's no contract, so the member may be balance-billed for it,
/// subject to the No Surprises Act and state law.
/// </summary>
/// <param name="Amount">The amount, or null before the claim has been priced.</param>
/// <param name="FromRemittance">True when it's the sum of the lines' CO adjustments; false when it's billed minus allowed.</param>
public sealed record ClaimAboveAllowed(
    decimal? Amount,
    ClaimNetwork Network,
    bool FromRemittance,
    string Label,
    string NetworkLabel,
    string Explanation);

public static class ClaimMoneySummary
{
    public static ClaimNetwork Network(string? tier) => tier?.Replace("-", "").Replace(" ", "").Trim().ToLowerInvariant() switch
    {
        "innetwork" or "in" or "par" => ClaimNetwork.InNetwork,
        "outofnetwork" or "out" or "nonpar" => ClaimNetwork.OutOfNetwork,
        "outofarea" => ClaimNetwork.OutOfArea,
        _ => ClaimNetwork.Unknown,
    };

    public static ClaimAboveAllowed AboveAllowed(ClaimDetails claim)
    {
        var network = Network(claim.NetworkTier);
        var lines = claim.ServiceLines ?? new List<ClaimServiceLine>();

        // The lines' CO adjustments are what the 835 tells the provider to
        // write off, so they're the right number when the engine produced them.
        var contractual = lines
            .SelectMany(l => l.Adjustments ?? new List<ClaimLineAdjustment>())
            .Where(a => string.Equals(a.GroupCode?.Trim(), "CO", StringComparison.OrdinalIgnoreCase))
            .ToList();

        decimal? amount;
        bool fromRemittance;
        if (contractual.Count > 0)
        {
            amount = contractual.Sum(a => a.Amount);
            fromRemittance = true;
        }
        else if (claim.AllowedAmount > 0 || claim.PaidAmount > 0)
        {
            amount = Math.Max(0, claim.TotalChargeAmount - claim.AllowedAmount);
            fromRemittance = false;
        }
        else
        {
            amount = null;
            fromRemittance = false;
        }

        return network switch
        {
            ClaimNetwork.InNetwork => new(amount, network, fromRemittance,
                "Contractual adjustment", "In-network",
                "Provider write-off under the network contract. The member can't be billed for it."),
            ClaimNetwork.OutOfNetwork or ClaimNetwork.OutOfArea => new(amount, network, fromRemittance,
                "Above allowed amount", network == ClaimNetwork.OutOfArea ? "Out-of-area" : "Out-of-network",
                "No network contract. The member may be billed for this, subject to the No Surprises Act and state law."),
            _ => new(amount, network, fromRemittance,
                fromRemittance ? "Contractual adjustment" : "Above allowed amount", "Network not recorded",
                fromRemittance
                    ? "Written off by the provider (835 group CO)."
                    : "Billed charges above the allowed amount."),
        };
    }
}
