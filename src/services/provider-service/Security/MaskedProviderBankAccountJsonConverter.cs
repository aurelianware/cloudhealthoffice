using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProviderService.Models;

namespace ProviderService.Security;

/// <summary>
/// Registered on the MVC JSON options: every <see cref="ProviderBankAccount"/>
/// written into an HTTP response is masked (<see cref="BankAccountMasking.Mask"/>:
/// last 4 of the routing and account numbers, no tax id), whichever endpoint or
/// model carries it (a provider, a version page, a search result, a
/// bank-account change). Request bodies are read unchanged, so a proposed
/// account still arrives with its full numbers. Storage (Mongo BSON, Cosmos)
/// does not use these options and is unaffected.
/// </summary>
public sealed class MaskedProviderBankAccountJsonConverter : JsonConverter<ProviderBankAccount>
{
    private readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> _withoutThis = new();

    public override ProviderBankAccount? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => JsonSerializer.Deserialize<ProviderBankAccount>(ref reader, Inner(options));

    public override void Write(Utf8JsonWriter writer, ProviderBankAccount value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, BankAccountMasking.Mask(value), Inner(options));

    /// <summary>The same options without this converter, so (de)serializing the object itself does not recurse.</summary>
    private JsonSerializerOptions Inner(JsonSerializerOptions options)
        => _withoutThis.GetValue(options, source =>
        {
            var copy = new JsonSerializerOptions(source);
            for (var i = copy.Converters.Count - 1; i >= 0; i--)
            {
                if (copy.Converters[i] is MaskedProviderBankAccountJsonConverter)
                    copy.Converters.RemoveAt(i);
            }
            return copy;
        });
}
