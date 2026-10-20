using System.Text;

namespace CloudHealthOffice.PlanEdits.Contracts.Tests;

public class PlanEditSignatureTests
{
    private const string Secret = "a-signing-secret-of-at-least-32-chars";
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 18, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"contractVersion\":\"1\"}");

    [Fact]
    public void Signature_is_hmac_sha256_over_timestamp_dot_body_in_lower_hex()
    {
        // Independent computation: what a plan on any stack would write.
        var expected = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(Secret),
            Encoding.UTF8.GetBytes($"{Now.ToUnixTimeSeconds()}.{{\"contractVersion\":\"1\"}}"))).ToLowerInvariant();

        PlanEditSignature.Sign(Secret, Now.ToUnixTimeSeconds(), Body).Should().Be("v1=" + expected);
    }

    [Fact]
    public void A_signature_verifies_against_the_same_body_and_time()
    {
        var ts = Now.ToUnixTimeSeconds();
        var signature = PlanEditSignature.Sign(Secret, ts, Body);

        PlanEditSignature.Verify(Secret, ts.ToString(), signature, Body, Now).Should().BeTrue();
        PlanEditSignature.Verify(Secret, ts.ToString(), signature, Body, Now.AddMinutes(4)).Should().BeTrue();
    }

    [Fact]
    public void A_changed_body_secret_or_timestamp_does_not_verify()
    {
        var ts = Now.ToUnixTimeSeconds();
        var signature = PlanEditSignature.Sign(Secret, ts, Body);

        PlanEditSignature.Verify(Secret, ts.ToString(), signature, Encoding.UTF8.GetBytes("{}"), Now).Should().BeFalse();
        PlanEditSignature.Verify(Secret + "x", ts.ToString(), signature, Body, Now).Should().BeFalse();
        PlanEditSignature.Verify(Secret, (ts + 1).ToString(), signature, Body, Now).Should().BeFalse();
    }

    [Fact]
    public void A_stale_or_future_timestamp_is_refused_even_with_a_valid_signature()
    {
        var old = Now.AddMinutes(-6).ToUnixTimeSeconds();
        var future = Now.AddMinutes(6).ToUnixTimeSeconds();

        PlanEditSignature.Verify(Secret, old.ToString(), PlanEditSignature.Sign(Secret, old, Body), Body, Now).Should().BeFalse();
        PlanEditSignature.Verify(Secret, future.ToString(), PlanEditSignature.Sign(Secret, future, Body), Body, Now).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, "v1=00")]
    [InlineData("", "v1=00")]
    [InlineData("-5", "v1=00")]
    [InlineData("12x", "v1=00")]
    [InlineData("99999999999999999999", "v1=00")]
    [InlineData("1792512000", null)]
    [InlineData("1792512000", "")]
    [InlineData("1792512000", "v2=00")]
    [InlineData("1792512000", "v1=zz")]
    public void Malformed_headers_never_verify_and_never_throw(string? timestamp, string? signature)
    {
        PlanEditSignature.Verify(Secret, timestamp, signature, Body, Now).Should().BeFalse();
    }
}
