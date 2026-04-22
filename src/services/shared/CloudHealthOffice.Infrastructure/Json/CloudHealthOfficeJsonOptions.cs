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
///   - allowIntegerValues: false — kept at framework default (true) for
///     backward compatibility with any external caller still POSTing ints.
///     Flipping to strict is tracked as a follow-up.
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
    public static IMvcBuilder AddCloudHealthOfficeJsonOptions(this IMvcBuilder builder)
    {
        return builder.AddJsonOptions(o =>
        {
            o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
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
        opts.Converters.Add(new JsonStringEnumConverter());
        // Freeze. A mutable singleton surfaces as a subtle global: any
        // caller adding a Converter here would reshape serialization for
        // every sibling service that consumes this property. MakeReadOnly
        // converts future mutation into a clear InvalidOperationException.
        opts.MakeReadOnly();
        return opts;
    }
}
