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
}
