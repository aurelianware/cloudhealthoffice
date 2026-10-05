using System.Security.Cryptography;
using System.Text;
using CloudHealthOffice.PricingApi.Models;
using MongoDB.Bson;

namespace CloudHealthOffice.PricingApi.Security;

/// <summary>
/// Pricing API keys are stored as SHA-256 plus a short prefix. The key is
/// 128 random bits, so an unsalted hash is enough: it cannot be brute-forced,
/// and a lookup by hash needs no per-record work.
/// </summary>
public static class ApiKeyHashing
{
    /// <summary>"cho_" plus 8 hex characters: enough to tell keys apart, nothing to guess with.</summary>
    public const int PrefixLength = 12;

    public static string Hash(string apiKey)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey))).ToLowerInvariant();

    public static string Prefix(string apiKey) => apiKey[..Math.Min(PrefixLength, apiKey.Length)];

    public static string NewKeyId() => "pk_" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// A new key and the record to store for it. The key is returned to the
    /// caller once and kept nowhere.
    /// </summary>
    public static (string ApiKey, ApiKeyRecord Record) Issue(
        string tenantName, string? contactEmail, PricingTier tier, int monthlyLimit, string? createdBy)
    {
        var apiKey = "cho_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        return (apiKey, new ApiKeyRecord
        {
            Id = ObjectId.GenerateNewId().ToString(),
            KeyId = NewKeyId(),
            KeyHash = Hash(apiKey),
            KeyPrefix = Prefix(apiKey),
            TenantName = tenantName,
            ContactEmail = contactEmail,
            Tier = tier,
            MonthlyLimit = monthlyLimit,
            CurrentMonthUsage = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            IsActive = true,
            CreatedBy = createdBy,
        });
    }
}
