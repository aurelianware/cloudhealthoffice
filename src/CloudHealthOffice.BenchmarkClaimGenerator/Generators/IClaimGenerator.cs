using CloudHealthOffice.BenchmarkClaimGenerator.Models;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Generators;

/// <summary>
/// Interface for claim generators. Each implementation produces a specific type
/// of synthetic claim (Professional, Institutional, Dental, or EdgeCase).
/// </summary>
public interface IClaimGenerator
{
    /// <summary>The claim type this generator produces.</summary>
    string ClaimType { get; }

    /// <summary>
    /// Generate a single synthetic claim with deterministic output for a given sequence number.
    /// </summary>
    /// <param name="sequenceNumber">Unique sequence number for claim ID generation.</param>
    /// <param name="subType">Sub-type hint for distribution stratification.</param>
    /// <param name="random">Seeded random instance for deterministic generation.</param>
    /// <returns>A fully populated synthetic claim with expected outcome.</returns>
    SyntheticClaim Generate(int sequenceNumber, string subType, Random random);
}
