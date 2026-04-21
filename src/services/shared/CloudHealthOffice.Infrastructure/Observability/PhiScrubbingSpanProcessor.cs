using System.Diagnostics;
using OpenTelemetry;

namespace CloudHealthOffice.Infrastructure.Observability;

/// <summary>
/// Mandatory PHI-scrubbing SpanProcessor. Runs on <see cref="OnEnd"/> and
/// removes prohibited attributes from every exported Activity before it
/// reaches any exporter.
///
/// Enforced as a denylist: an attribute is dropped if its name exactly
/// matches one on the prohibited list, OR if the segment after the final
/// '.' matches one on the list (so <c>http.request.header.authorization</c>
/// is scrubbed because the suffix <c>authorization</c> is prohibited, even
/// though <c>http.*</c> is otherwise a standard OTel namespace). Everything
/// not matching is allowed through. Comparisons are case-insensitive.
///
/// Drops are counted via <c>cho.telemetry.scrub.total</c> with
/// <c>attribute_name</c> and <c>service_name</c> labels so the rate can be
/// alerted on — a non-zero rate in production means a raw PHI attribute is
/// being written somewhere.
///
/// This processor cannot be disabled by configuration — it is a compliance
/// control. Callers wanting different scrubbing rules must fork the shared
/// extension, not opt out.
/// </summary>
public sealed class PhiScrubbingSpanProcessor : BaseProcessor<Activity>
{
    private static readonly HashSet<string> ProhibitedAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ssn", "social_security_number", "socialSecurityNumber",
        "mbi", "medicareBeneficiaryIdentifier",
        "dob", "dateOfBirth", "date_of_birth", "birthDate",
        "member_id", "memberId",
        "subscriber_id", "subscriberId",
        "patient_id", "patientId",
        "email", "emailAddress", "email_address",
        "phone", "phoneNumber", "phone_number",
        "address", "streetAddress", "street",
        "first_name", "firstName", "last_name", "lastName", "full_name", "fullName",
        "password", "api_key", "apiKey", "token", "secret", "authorization",
    };

    private readonly string _serviceName;

    public PhiScrubbingSpanProcessor(string? serviceName = null)
    {
        _serviceName = serviceName ?? "cho-unknown-service";
    }

    public override void OnEnd(Activity activity)
    {
        List<string>? toRemove = null;

        foreach (var tag in activity.TagObjects)
        {
            if (IsProhibited(tag.Key))
            {
                toRemove ??= new List<string>();
                toRemove.Add(tag.Key);
            }
        }

        if (toRemove is null) return;

        foreach (var key in toRemove)
        {
            activity.SetTag(key, null);
            ChoMetrics.TelemetryScrubCount.Add(
                1,
                new KeyValuePair<string, object?>("attribute_name", key),
                new KeyValuePair<string, object?>("service_name", _serviceName));
        }
    }

    private static bool IsProhibited(string attributeName)
    {
        if (ProhibitedAttributes.Contains(attributeName))
            return true;

        var lastDot = attributeName.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < attributeName.Length - 1)
        {
            var suffix = attributeName[(lastDot + 1)..];
            if (ProhibitedAttributes.Contains(suffix))
                return true;
        }

        return false;
    }
}
