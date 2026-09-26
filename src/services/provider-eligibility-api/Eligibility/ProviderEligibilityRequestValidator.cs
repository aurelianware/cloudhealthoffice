using System.Text.RegularExpressions;
using ProviderEligibilityApi.Contracts;

namespace ProviderEligibilityApi.Eligibility;

/// <summary>
/// Rejects requests that cannot produce a useful payer answer before a paid
/// clearinghouse transaction is spent. Error messages name the field only and
/// never echo submitted values.
/// </summary>
internal static partial class ProviderEligibilityRequestValidator
{
    private static readonly HashSet<string> DependentRelationships =
        new(StringComparer.OrdinalIgnoreCase) { "spouse", "child", "other" };

    public static Dictionary<string, string> Validate(ProviderEligibilityCheckRequest? request, DateOnly today)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (request is null)
        {
            errors["body"] = "A JSON request body is required.";
            return errors;
        }

        RequireText(errors, "payerId", request.PayerId, 80);

        if (request.Provider is null)
        {
            errors["provider"] = "Provider is required.";
        }
        else if (string.IsNullOrWhiteSpace(request.Provider.Npi) || !NpiPattern().IsMatch(request.Provider.Npi.Trim()))
        {
            errors["provider.npi"] = "Provider NPI must be 10 digits.";
        }
        else
        {
            OptionalText(errors, "provider.organizationName", request.Provider.OrganizationName, 60);
        }

        if (request.Subscriber is null)
        {
            errors["subscriber"] = "Subscriber is required.";
        }
        else
        {
            RequireText(errors, "subscriber.memberId", request.Subscriber.MemberId, 80);
            ValidatePerson(errors, "subscriber", request.Subscriber, today);
        }

        if (request.Patient is not null)
        {
            ValidatePerson(errors, "patient", request.Patient, today);
            OptionalText(errors, "patient.memberId", request.Patient.MemberId, 80);
            if (!string.IsNullOrWhiteSpace(request.Patient.RelationshipToSubscriber) &&
                !DependentRelationships.Contains(request.Patient.RelationshipToSubscriber.Trim()))
            {
                errors["patient.relationshipToSubscriber"] =
                    "Relationship must be spouse, child or other. Omit the patient when they are the subscriber.";
            }
        }

        OptionalText(errors, "groupNumber", request.GroupNumber, 50);

        if (request.ServiceTypeCode is not null && !ServiceTypePattern().IsMatch(request.ServiceTypeCode.Trim()))
        {
            errors["serviceTypeCode"] = "Service type code must be one or two letters or digits.";
        }

        if (request.ServiceDate is { } serviceDate &&
            (serviceDate < today.AddYears(-2) || serviceDate > today.AddYears(1)))
        {
            errors["serviceDate"] = "Service date must be within the last two years or the next year.";
        }

        if (request.CorrelationId is not null && !CorrelationPattern().IsMatch(request.CorrelationId))
        {
            errors["correlationId"] = "Correlation id must be 1-64 letters, digits, '.', '_' or '-'.";
        }

        return errors;
    }

    private static void ValidatePerson(
        Dictionary<string, string> errors, string prefix, ProviderEligibilityPerson person, DateOnly today)
    {
        RequireText(errors, $"{prefix}.firstName", person.FirstName, 60);
        RequireText(errors, $"{prefix}.lastName", person.LastName, 60);
        if (person.DateOfBirth is not { } dob)
        {
            errors[$"{prefix}.dateOfBirth"] = "Date of birth is required.";
        }
        else if (dob > today || dob < new DateOnly(1900, 1, 1))
        {
            errors[$"{prefix}.dateOfBirth"] = "Date of birth is out of range.";
        }
    }

    private static void RequireText(Dictionary<string, string> errors, string field, string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[field] = "Required.";
        }
        else
        {
            OptionalText(errors, field, value, maxLength);
        }
    }

    private static void OptionalText(Dictionary<string, string> errors, string field, string? value, int maxLength)
    {
        if (value is null) return;
        if (value.Trim().Length > maxLength)
        {
            errors[field] = $"Must be {maxLength} characters or fewer.";
        }
        else if (value.Any(char.IsControl))
        {
            errors[field] = "Contains unsupported characters.";
        }
    }

    [GeneratedRegex(@"^\d{10}$")]
    private static partial Regex NpiPattern();

    [GeneratedRegex(@"^[A-Za-z0-9]{1,2}$")]
    private static partial Regex ServiceTypePattern();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex CorrelationPattern();
}
