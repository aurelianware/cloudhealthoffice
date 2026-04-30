using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.Infrastructure.Json;

/// <summary>
/// Shared MVC JSON conventions for Cloud Health Office services.
///
/// The only convention applied here is <see cref="JsonStringEnumConverter"/>,
/// which ensures enums serialize/deserialize as their member names (e.g.
/// "LitigationHold") instead of their underlying integer values. Portal
/// clients and sibling services consistently send/receive string enum
/// payloads; relying on the framework default (integer) has caused silent
/// contract mismatches — see PRs #656 (PcpValidationError) and #657
/// (MemberAlertType, MemberNoteCategory).
///
/// Deliberately NOT applied here (see docs/architecture/shared-json-options.md):
///   - PropertyNamingPolicy — existing services differ (PascalCase vs camelCase
///     on the wire). A repo-wide change would break contracts.
///   - DefaultIgnoreCondition — same reason.
/// </summary>
public static class CloudHealthOfficeJsonOptions
{
    /// <summary>
    /// Registers <see cref="JsonStringEnumConverter"/> on the MVC JSON options.
    /// Call on the result of <c>AddControllers()</c>:
    /// <code>
    /// builder.Services.AddControllers()
    ///     .AddCloudHealthOfficeJsonOptions();
    /// </code>
    /// </summary>
    /// <param name="builder">The MVC builder to configure.</param>
    /// <param name="camelCaseEnums">
    /// When <c>true</c>, registers <see cref="JsonStringEnumConverter"/> with
    /// <see cref="JsonNamingPolicy.CamelCase"/> so enum names are emitted in
    /// camelCase (e.g. <c>"medicareFeeSchedule"</c>). Use this for services
    /// whose published wire format already uses camelCase enum names.
    /// When <c>false</c> (the default), enum names are emitted exactly as
    /// declared (e.g. <c>"MedicareFeeSchedule"</c>).
    /// </param>
    public static IMvcBuilder AddCloudHealthOfficeJsonOptions(
        this IMvcBuilder builder,
        bool camelCaseEnums = false)
    {
        return builder.AddJsonOptions(o =>
        {
            var converter = camelCaseEnums
                ? new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
                : new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false);
            o.JsonSerializerOptions.Converters.Add(converter);
        });
    }

    /// <summary>
    /// Default <see cref="JsonSerializerOptions"/> for non-MVC serialization paths —
    /// Redis cache payloads, Service Bus envelopes, Cosmos document bodies that
    /// round-trip through the app. camelCase on the wire, omit-null-on-write
    /// (smaller payloads, cheaper Redis), and the same string-enum convention
    /// the MVC pipeline uses.
    ///
    /// PascalCase-on-the-wire surfaces must continue to use their own local
    /// <c>JsonSerializerOptions</c> — see docs/architecture/shared-json-options.md.
    /// </summary>
    public static JsonSerializerOptions DefaultOptions { get; } = CreateDefault();

    private static JsonSerializerOptions CreateDefault()
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        opts.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        // Freeze. A mutable singleton surfaces as a subtle global: any
        // caller adding a Converter here would reshape serialization for
        // every sibling service that consumes this property. MakeReadOnly
        // converts future mutation into a clear InvalidOperationException.
        opts.MakeReadOnly(populateMissingResolver: true);
        return opts;
    }
}
