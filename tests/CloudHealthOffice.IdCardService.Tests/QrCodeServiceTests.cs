using CloudHealthOffice.Infrastructure.Configuration;
using IdCardService.Models;
using IdCardService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CloudHealthOffice.IdCardService.Tests;

public class QrCodeServiceTests
{
    [Fact]
    public async Task RoundTrip_ValidPayload_Verifies()
    {
        var svc = TestFixtures.QrService();
        var issuedAt = DateTime.UtcNow;

        var (png, qr, keyVersion, _) = await svc.GenerateAsync(
            TestFixtures.TenantId, TestFixtures.MemberId, "card-abc", issuedAt);

        Assert.Equal("v1", keyVersion);
        Assert.NotEmpty(png);

        var (payload, err, _) = await svc.VerifyAsync(qr);
        Assert.Null(err);
        Assert.NotNull(payload);
        Assert.Equal(TestFixtures.TenantId, payload!.TenantId);
        Assert.Equal(TestFixtures.MemberId, payload.MemberId);
        Assert.Equal("card-abc", payload.CardId);
        Assert.Equal("v1", payload.KeyVersion);
    }

    [Fact]
    public async Task Verify_TamperedSignature_Rejected()
    {
        var svc = TestFixtures.QrService();
        var (_, qr, _, _) = await svc.GenerateAsync(
            TestFixtures.TenantId, TestFixtures.MemberId, "c1", DateTime.UtcNow);

        // flip a byte in the signature segment
        var parts = qr.Split('.');
        var signature = parts[1].Replace('-', '+').Replace('_', '/');
        var padding = (signature.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => string.Empty
        };
        signature += padding;
        var signatureBytes = Convert.FromBase64String(signature);
        signatureBytes[0] ^= 0x01;
        var flipped = Convert.ToBase64String(signatureBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var bad = parts[0] + "." + flipped;

        var (payload, err, _) = await svc.VerifyAsync(bad);
        Assert.Null(payload);
        Assert.Equal(ScanErrorCodes.InvalidSignature, err);
    }

    [Fact]
    public async Task Verify_MalformedPayload_Rejected()
    {
        var svc = TestFixtures.QrService();

        var (p1, e1, _) = await svc.VerifyAsync("no-dot-here");
        Assert.Null(p1);
        Assert.Equal(ScanErrorCodes.MalformedPayload, e1);

        var (p2, e2, _) = await svc.VerifyAsync("!!!.###");
        Assert.Null(p2);
        Assert.Equal(ScanErrorCodes.MalformedPayload, e2);
    }

    [Fact]
    public async Task RollingWindow_CardFromPreviousKeyVersion_StillVerifies()
    {
        // Issue under v1 with v1 as the current key.
        var v1Svc = TestFixtures.QrService();
        var (_, qrV1, _, _) = await v1Svc.GenerateAsync(
            TestFixtures.TenantId, TestFixtures.MemberId, "c1", DateTime.UtcNow);

        // Rotate: v2 is now current, but v1 is still in the accepted window.
        // The card issued under v1 must still scan successfully because its
        // key version is within the rolling window.
        var rolledSvc = TestFixtures.QrService(
            ("IdCard:CurrentKeyVersion", "v2"),
            ("IdCard:AcceptedKeyVersions:0", "v2"),
            ("IdCard:AcceptedKeyVersions:1", "v1"),
            ("IdCard:DevSigningKeys:v2", "dev-key-v2-bytes-for-hmac-signing"));

        var (payload, err, _) = await rolledSvc.VerifyAsync(qrV1);
        Assert.Null(err);
        Assert.NotNull(payload);
        Assert.Equal("v1", payload!.KeyVersion);
    }

    [Fact]
    public async Task RollingWindow_CardFromStaleKey_ReturnsCardSignatureStale()
    {
        // Card originally issued under v0.
        var v0Svc = TestFixtures.QrService(
            ("IdCard:CurrentKeyVersion", "v0"),
            ("IdCard:AcceptedKeyVersions:0", "v0"),
            ("IdCard:DevSigningKeys:v0", "dev-key-v0-bytes-for-hmac-signing"));
        var (_, qrV0, _, _) = await v0Svc.GenerateAsync(
            TestFixtures.TenantId, TestFixtures.MemberId, "c0", DateTime.UtcNow);

        // Current service only accepts v1 and v2 (rolling window has moved on).
        var currentSvc = TestFixtures.QrService(
            ("IdCard:CurrentKeyVersion", "v2"),
            ("IdCard:AcceptedKeyVersions:0", "v2"),
            ("IdCard:AcceptedKeyVersions:1", "v1"),
            ("IdCard:DevSigningKeys:v2", "dev-key-v2-bytes-for-hmac-signing"));

        var (payload, err, msg) = await currentSvc.VerifyAsync(qrV0);
        Assert.Null(payload);
        Assert.Equal(ScanErrorCodes.StaleKey, err);
        Assert.Contains("key version", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CanonicalPayload_PersistedOnRecord_RoundTrips()
    {
        // Audit contract: the QrCanonicalPayload we persist on IdCardRecord
        // is the same base64url segment we can later decode, verify, and
        // compare byte-for-byte with what the scanner receives.
        var svc = TestFixtures.QrService();
        var (_, qr, _, canonical) = await svc.GenerateAsync(
            TestFixtures.TenantId, TestFixtures.MemberId, "c1", DateTime.UtcNow);

        Assert.Equal(canonical, qr.Split('.')[0]);
    }
}
