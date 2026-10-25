using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using AuthorizationService.Services.Cms0057;

namespace AuthorizationService.Models;

public sealed class FreezePaReportingPackage
{
    [Required, StringLength(128), RegularExpression(@"^[A-Za-z0-9][A-Za-z0-9._:-]*$")]
    public string RequestKey { get; set; } = string.Empty;
    [Required, StringLength(100), RegularExpression(@"^[A-Za-z0-9][A-Za-z0-9._:-]*$")]
    public string PlanId { get; set; } = string.Empty;
    /// <summary>Opaque reference to the proposed tenant/LOB-to-plan mapping; not proof of acceptance.</summary>
    [Required, StringLength(200), RegularExpression(@"^[A-Za-z0-9][A-Za-z0-9._:/-]*$")]
    public string PlanScopeReference { get; set; } = string.Empty;
    public int Year { get; set; }
    [Required, StringLength(100)]
    public string TimeZone { get; set; } = string.Empty;
    public LineOfBusiness LineOfBusiness { get; set; }
    public DateTimeOffset OutcomeCutoff { get; set; }
    public DateTimeOffset KnowledgeCutoff { get; set; }
}

/// <summary>Declared scope. No unverified plan mapping becomes an accepted fact.</summary>
public sealed class PaReportingPackageScope
{
    public string PlanId { get; set; } = string.Empty;
    public string PlanScopeReference { get; set; } = string.Empty;
    public int Year { get; set; }
    public string TimeZone { get; set; } = string.Empty;
    public LineOfBusiness LineOfBusiness { get; set; }
    public DateTime OutcomeCutoffUtc { get; set; }
    public DateTime KnowledgeCutoffUtc { get; set; }
}

public sealed class PaReportingFrozenInput
{
    public string AuthorizationId { get; set; } = string.Empty;
    public long Revision { get; set; }
    public LineOfBusiness LegacyLineOfBusiness { get; set; }
    public DateTime LegacySubmittedAt { get; set; }
    public PaReportingLedger? Ledger { get; set; }
}

public sealed class PaReportingPackageInput
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string PackageId { get; set; } = string.Empty;
    public int Ordinal { get; set; }
    public string InputHash { get; set; } = string.Empty;
    public PaReportingFrozenInput Input { get; set; } = new();
}

public sealed class PaReportingPackage
{
    public const string FormatVersion = "cho-pa-frozen-package-v1";
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Version { get; set; } = FormatVersion;
    public string BuildVersion { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string InputsHash { get; set; } = string.Empty;
    public string PackageHash { get; set; } = string.Empty;
    public int InputCount { get; set; }
    public long InputBytes { get; set; }
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public PaReportingPackageScope Scope { get; set; } = new();
    public AnnualPaDraft Report { get; set; } = new();
    public bool PublicationReady => false;
    public bool PlanMappingVerified => false;
    public string ApprovalStatus => "Unapproved";
}

/// <summary>Versioned server-generated hashes; not a digital signature or regulatory certificate.</summary>
public static class PaReportingPackageHash
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    public static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
    public static string Of<T>(T value) => Convert.ToHexString(SHA256.HashData(Bytes(value)));
    public static string Identity(string tenant, string key) => Of(new[] { tenant, key });
    public static string Header(PaReportingPackage package) => Of(new
    {
        package.Id, package.TenantId, package.Version, package.BuildVersion, package.RequestHash,
        package.InputsHash, package.InputCount, package.InputBytes, package.CreatedAt, package.CreatedBy,
        package.Scope, package.Report,
    });
    public static string Inputs(IEnumerable<PaReportingPackageInput> inputs)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var input in inputs.OrderBy(i => i.Ordinal)) hash.AppendData(Encoding.ASCII.GetBytes(input.InputHash));
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
