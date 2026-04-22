using CloudHealthOffice.Infrastructure.Configuration;
using IdCardService.Models;
using IdCardService.Repositories;
using IdCardService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CloudHealthOffice.IdCardService.Tests;

internal static class TestFixtures
{
    public const string TenantId = "tenant-test";
    public const string MemberId = "mem-123";
    public const string GroupNumber = "G-001";
    public const string PlanId = "P-001";

    public static IConfiguration Configuration(params (string Key, string? Value)[] extra)
    {
        var data = new Dictionary<string, string?>
        {
            ["IdCard:SigningKeySecretPrefix"] = "idcard-signing-key",
            ["IdCard:CurrentKeyVersion"] = "v1",
            ["IdCard:AcceptedKeyVersions:0"] = "v1",
            ["IdCard:DevSigningKeys:v1"] = "dev-key-v1-bytes-for-hmac-signing",
            ["IdCard:Qr:PixelsPerModule"] = "4",
            ["IdCard:Qr:EccLevel"] = "M"
        };
        foreach (var (k, v) in extra) data[k] = v;
        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    public static ISecretProvider EmptySecretProvider()
    {
        var sp = Substitute.For<ISecretProvider>();
        sp.GetSecretAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>(null));
        return sp;
    }

    public static QrCodeService QrService(params (string Key, string? Value)[] extraConfig) =>
        new(
            new RotatingKeyProvider(EmptySecretProvider(), NullLogger<RotatingKeyProvider>.Instance),
            Configuration(extraConfig),
            NullLogger<QrCodeService>.Instance);

    public static IdCardTemplate GlobalDefault(string tenantId = TenantId) => new()
    {
        Id = "tmpl-global",
        TenantId = tenantId,
        Name = "Global Default",
        IsGlobalDefault = true,
        SupportedLanguages = new() { "en-US", "es-US" },
        LayoutSvg = string.Empty
    };

    public static IdCardTemplate SponsorDefault(string tenantId, string sponsorId) => new()
    {
        Id = $"tmpl-{sponsorId}",
        TenantId = tenantId,
        SponsorId = sponsorId,
        Name = $"Sponsor {sponsorId} default",
        SupportedLanguages = new() { "en-US" },
        LayoutSvg = string.Empty
    };

    public static IdCardTemplate SponsorPlan(string tenantId, string sponsorId, string planId) => new()
    {
        Id = $"tmpl-{sponsorId}-{planId}",
        TenantId = tenantId,
        SponsorId = sponsorId,
        PlanId = planId,
        Name = $"Sponsor {sponsorId} plan {planId}",
        SupportedLanguages = new() { "en-US" },
        LayoutSvg = string.Empty
    };
}
