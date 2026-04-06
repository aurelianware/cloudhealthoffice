namespace CloudHealthOffice.BenchmarkClaimGenerator.Models;

/// <summary>
/// Represents a synthetic rendering or billing provider for benchmark claim generation.
/// </summary>
public class SyntheticProvider
{
    /// <summary>National Provider Identifier (10-digit NPI).</summary>
    public string Npi { get; set; } = string.Empty;

    /// <summary>Tax identification number.</summary>
    public string TaxId { get; set; } = string.Empty;

    /// <summary>Provider first name (for individual providers).</summary>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>Provider last name or organization name.</summary>
    public string LastName { get; set; } = string.Empty;

    /// <summary>Provider specialty code (taxonomy code).</summary>
    public string SpecialtyCode { get; set; } = string.Empty;

    /// <summary>Whether this is a participating (in-network) provider.</summary>
    public bool IsParticipating { get; set; }

    /// <summary>Provider state (two-letter code).</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Provider ZIP code.</summary>
    public string ZipCode { get; set; } = string.Empty;
}
