using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BenefitPlanService.Models.Benefits;

/// <summary>
/// Polymorphic <see cref="JsonConverter{T}"/> for <see cref="Benefit"/> and
/// its typed subclasses (<see cref="MedicalBenefit"/>,
/// <see cref="DentalBenefit"/>, <see cref="PharmacyBenefit"/>, etc).
///
/// <para>
/// On read, peeks at the <c>benefitType</c> discriminator and dispatches to
/// the matching concrete subclass. A missing or empty discriminator — which
/// is the shape every <see cref="Benefit"/> persisted before 5.4 has on the
/// wire — hydrates as <see cref="MedicalBenefit"/>, the catch-all default.
/// An unknown discriminator value also falls back to <see cref="MedicalBenefit"/>
/// so we never throw on legacy or unfamiliar data.
/// </para>
///
/// <para>
/// On write, serializes using the runtime type so the type-specific facets
/// of subclasses are preserved on the wire. The discriminator is emitted
/// via the virtual <see cref="Benefit.BenefitType"/> property each subclass
/// overrides.
/// </para>
///
/// <para>
/// Implementation note: to avoid infinite recursion, the converter strips
/// itself from a copy of the active <see cref="JsonSerializerOptions"/>
/// before delegating to <see cref="JsonSerializer"/> for the inner work.
/// </para>
/// </summary>
public sealed class BenefitJsonConverter : JsonConverter<Benefit>
{
    // Cache the "without self" options copy keyed by the original instance so
    // we reuse both the copy and its internally-built type-metadata cache.
    // Without this, creating a new JsonSerializerOptions on every Write call
    // caused excessive allocations and performance degradation under parallel
    // test execution, due to each copy building its own type-metadata cache
    // from scratch on first use.
    private static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> _optionsCache = new();

    public override Benefit Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null!;
        }

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        var discriminator = ReadDiscriminator(root);
        var concreteType = ResolveConcreteType(discriminator);

        var rawJson = root.GetRawText();
        var inner = WithoutSelf(options);
        var result = JsonSerializer.Deserialize(rawJson, concreteType, inner);
        return (Benefit)result!;
    }

    public override void Write(Utf8JsonWriter writer, Benefit value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        var inner = WithoutSelf(options);
        JsonSerializer.Serialize(writer, value, value.GetType(), inner);
    }

    private static string ReadDiscriminator(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return BenefitTypeDiscriminators.Medical;
        }

        // Property-name lookup is case-insensitive so payloads that arrive
        // with "BenefitType" / "BENEFITTYPE" / "benefittype" all dispatch
        // the same way. STJ's JsonElement.TryGetProperty is case-sensitive
        // and ignores PropertyNameCaseInsensitive on the options, so we
        // enumerate explicitly. Discriminator value comparison further down
        // (ResolveConcreteType) is also case-insensitive for the same reason.
        JsonElement? prop = null;
        foreach (var p in root.EnumerateObject())
        {
            if (string.Equals(p.Name, "benefitType", StringComparison.OrdinalIgnoreCase))
            {
                prop = p.Value;
                break;
            }
        }

        if (prop is null || prop.Value.ValueKind != JsonValueKind.String)
        {
            return BenefitTypeDiscriminators.Medical;
        }

        var value = prop.Value.GetString();
        return string.IsNullOrWhiteSpace(value) ? BenefitTypeDiscriminators.Medical : value;
    }

    private static Type ResolveConcreteType(string discriminator)
    {
        // Comparison is case-insensitive so e.g. "BehavioralHealth", "behavioralhealth",
        // and "behavioralHealth" all resolve to the same type. Unknown values fall
        // back to MedicalBenefit — we never throw on read.
        if (string.Equals(discriminator, BenefitTypeDiscriminators.Dental, StringComparison.OrdinalIgnoreCase))
            return typeof(DentalBenefit);
        if (string.Equals(discriminator, BenefitTypeDiscriminators.Pharmacy, StringComparison.OrdinalIgnoreCase))
            return typeof(PharmacyBenefit);
        if (string.Equals(discriminator, BenefitTypeDiscriminators.BehavioralHealth, StringComparison.OrdinalIgnoreCase))
            return typeof(BehavioralHealthBenefit);
        if (string.Equals(discriminator, BenefitTypeDiscriminators.Vision, StringComparison.OrdinalIgnoreCase))
            return typeof(VisionBenefit);
        if (string.Equals(discriminator, BenefitTypeDiscriminators.DME, StringComparison.OrdinalIgnoreCase))
            return typeof(DMEBenefit);
        if (string.Equals(discriminator, BenefitTypeDiscriminators.Maternity, StringComparison.OrdinalIgnoreCase))
            return typeof(MaternityBenefit);
        if (string.Equals(discriminator, BenefitTypeDiscriminators.Preventive, StringComparison.OrdinalIgnoreCase))
            return typeof(PreventiveBenefit);
        return typeof(MedicalBenefit);
    }

    private static JsonSerializerOptions WithoutSelf(JsonSerializerOptions options)
    {
        return _optionsCache.GetValue(options, static original =>
        {
            var copy = new JsonSerializerOptions(original);
            for (var i = copy.Converters.Count - 1; i >= 0; i--)
            {
                if (copy.Converters[i] is BenefitJsonConverter)
                {
                    copy.Converters.RemoveAt(i);
                }
            }
            return copy;
        });
    }
}
