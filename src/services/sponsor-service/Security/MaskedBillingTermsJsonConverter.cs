using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using SponsorService.Contracts;
using SponsorService.Models;

namespace SponsorService.Security;

/// <summary>
/// The same masking <see cref="MaskedBillingInfoJsonConverter"/> applies to the
/// sponsor record's <c>BillingInfo</c>, applied to a contract term's
/// <see cref="BillingTerms"/>: <c>billingAccountNumber</c> is left out of every
/// response and <c>billingAccountNumberLast4</c> shows its last four. Request
/// bodies are read unchanged, and storage does not use these options.
///
/// <para>
/// The contract term carries the same account number as the sponsor record, so
/// writing it in the clear here while masking it there would mean the stricter
/// of the two rules decided nothing: a caller that wanted the number would
/// simply read the contract instead.
/// </para>
/// </summary>
public sealed class MaskedBillingTermsJsonConverter : JsonConverter<BillingTerms>
{
    private readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> _withoutThis = new();

    public override BillingTerms? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => JsonSerializer.Deserialize<BillingTerms>(ref reader, Inner(options));

    public override void Write(Utf8JsonWriter writer, BillingTerms value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, MaskedBillingTerms.From(value), Inner(options));

    private JsonSerializerOptions Inner(JsonSerializerOptions options)
        => _withoutThis.GetValue(options, source =>
        {
            var copy = new JsonSerializerOptions(source);
            for (var i = copy.Converters.Count - 1; i >= 0; i--)
            {
                if (copy.Converters[i] is MaskedBillingTermsJsonConverter)
                    copy.Converters.RemoveAt(i);
            }
            return copy;
        });

    /// <summary>The response shape of <see cref="BillingTerms"/>.</summary>
    public sealed class MaskedBillingTerms
    {
        [JsonPropertyName("method")]
        public BillingMethod Method { get; init; }

        [JsonPropertyName("frequency")]
        public BillingFrequency Frequency { get; init; }

        [JsonPropertyName("dueDay")]
        public int DueDay { get; init; }

        [JsonPropertyName("gracePeriodDays")]
        public int GracePeriodDays { get; init; }

        [JsonPropertyName("billingAccountNumberLast4")]
        public string? BillingAccountNumberLast4 { get; init; }

        [JsonPropertyName("paymentMethod")]
        public string? PaymentMethod { get; init; }

        public static MaskedBillingTerms From(BillingTerms terms) => new()
        {
            Method = terms.Method,
            Frequency = terms.Frequency,
            DueDay = terms.DueDay,
            GracePeriodDays = terms.GracePeriodDays,
            // A still-encrypted value (never decrypted on this path) shows nothing rather than ciphertext.
            BillingAccountNumberLast4 = terms.BillingAccountNumber?.StartsWith("enc:", StringComparison.Ordinal) == true
                ? null
                : SponsorBankAccountMasking.Last4(terms.BillingAccountNumber),
            PaymentMethod = terms.PaymentMethod,
        };
    }
}
