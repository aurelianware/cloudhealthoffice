using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using SponsorService.Models;

namespace SponsorService.Security;

/// <summary>
/// Registered on the MVC JSON options: every <see cref="BillingInfo"/> written
/// into an HTTP response (sponsor get, list, create and update) carries the
/// billing account number masked: <c>billingAccountNumber</c> is left out and
/// <c>billingAccountNumberLast4</c> shows its last 4. Request bodies are read
/// unchanged. Storage does not use these options.
/// </summary>
public sealed class MaskedBillingInfoJsonConverter : JsonConverter<BillingInfo>
{
    private readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> _withoutThis = new();

    public override BillingInfo? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => JsonSerializer.Deserialize<BillingInfo>(ref reader, Inner(options));

    public override void Write(Utf8JsonWriter writer, BillingInfo value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, MaskedBillingInfo.From(value), Inner(options));

    private JsonSerializerOptions Inner(JsonSerializerOptions options)
        => _withoutThis.GetValue(options, source =>
        {
            var copy = new JsonSerializerOptions(source);
            for (var i = copy.Converters.Count - 1; i >= 0; i--)
            {
                if (copy.Converters[i] is MaskedBillingInfoJsonConverter)
                    copy.Converters.RemoveAt(i);
            }
            return copy;
        });

    /// <summary>The response shape of <see cref="BillingInfo"/>.</summary>
    public sealed class MaskedBillingInfo
    {
        public decimal PremiumAmount { get; init; }
        public BillingFrequency Frequency { get; init; }
        public int BillingDay { get; init; }
        public string? BillingAccountNumberLast4 { get; init; }
        public string? PaymentMethod { get; init; }
        public int GracePeriodDays { get; init; }

        public static MaskedBillingInfo From(BillingInfo info) => new()
        {
            PremiumAmount = info.PremiumAmount,
            Frequency = info.Frequency,
            BillingDay = info.BillingDay,
            // A still-encrypted value (never decrypted on this path) shows nothing rather than ciphertext.
            BillingAccountNumberLast4 = info.BillingAccountNumber?.StartsWith("enc:", StringComparison.Ordinal) == true
                ? null
                : SponsorBankAccountMasking.Last4(info.BillingAccountNumber),
            PaymentMethod = info.PaymentMethod,
            GracePeriodDays = info.GracePeriodDays,
        };
    }
}
