using System.Text.Json;
using ClaimsService.Models;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Models;

/// <summary>
/// PR #1278 round-3 verification (M4): the fingerprint of a claim's pends —
/// what the examiner viewed — changes with any pend, reason or pend time,
/// and is part of the API's JSON.
/// </summary>
public class PendFingerprintTests
{
    private static PendDetails Pend() => new()
    {
        PendCode = "DUPLICATE",
        PendReason = "possible duplicate",
        PendedAt = new DateTime(2026, 4, 19, 10, 0, 0, 123, DateTimeKind.Utc),
    };

    [Fact]
    public void SamePends_SameFingerprint_AtStoragePrecision()
    {
        var a = Pend();
        var b = Pend();
        b.PendedAt = b.PendedAt.AddTicks(4_000); // sub-millisecond: dropped by the stores

        Assert.Equal(a.Fingerprint, b.Fingerprint);
        Assert.StartsWith("20260419T100000123Z-", a.Fingerprint);
    }

    [Fact]
    public void AnyChange_ChangesTheFingerprint()
    {
        var baseline = Pend().Fingerprint;

        var added = Pend();
        added.AdditionalPendReasons.Add("MEDREVIEW: Billing: manual review required.");
        var reason = Pend();
        reason.PendReason = "Line 1 duplicates CLM-2.";
        var repended = Pend();
        repended.PendedAt = repended.PendedAt.AddMinutes(5);

        Assert.NotEqual(baseline, added.Fingerprint);
        Assert.NotEqual(baseline, reason.Fingerprint);
        Assert.NotEqual(baseline, repended.Fingerprint);
    }

    [Fact]
    public void NoPend_IsEmpty_AndTheApiJsonCarriesIt()
    {
        Assert.Equal(string.Empty, PendDetails.ComputeFingerprint(null));
        var json = JsonSerializer.Serialize(Pend(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains($"\"fingerprint\":\"{Pend().Fingerprint}\"", json);
    }
}
