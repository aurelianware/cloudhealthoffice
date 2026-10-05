using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SmartAuthService.Services;

/// <summary>
/// One structured line per binding change and per token decision, on its own
/// log category. The actor is always the validated token's subject (or the
/// signed-in identity, for a redemption); request bodies never name it.
/// Enrolment codes and access tokens are never logged.
/// </summary>
public sealed class SmartAuthAudit
{
    public const string Category = "CloudHealthOffice.SmartAuth.Audit";

    private readonly ILogger _logger;

    public SmartAuthAudit(ILoggerFactory loggerFactory) => _logger = loggerFactory.CreateLogger(Category);

    public void Admin(string action, string tenantId, string actor, string target)
        => _logger.LogInformation(
            "SMART binding {Action}: tenant={Tenant} actor={Actor} target={Target}",
            Clean(action), Clean(tenantId), Clean(actor), Clean(target));

    public void Redeemed(string kind, string tenantId, string identity, string enrolmentId, string outcome)
        => _logger.LogInformation(
            "SMART enrolment {Outcome}: kind={Kind} tenant={Tenant} identity={Identity} enrolment={Enrolment}",
            Clean(outcome), Clean(kind), Clean(tenantId), Clean(identity), Clean(enrolmentId));

    public void TokenIssued(string context, string tenantId, string subject, string clientId)
        => _logger.LogInformation(
            "SMART token {Outcome}: context={Context} tenant={Tenant} sub={Subject} client={Client}",
            "issued", Clean(context), Clean(tenantId), Clean(subject), Clean(clientId));

    public void TokenRefused(string? identity, string? clientId, string reason)
        => _logger.LogWarning(
            "SMART token {Outcome}: identity={Identity} client={Client} reason={Reason}",
            "refused", Clean(identity), Clean(clientId), Clean(reason));

    public void SignedIn(string method, string identity)
        => _logger.LogInformation(
            "SMART sign-in {Outcome}: method={Method} identity={Identity}",
            "succeeded", Clean(method), Clean(identity));

    public void SignInRefused(string method, string reason)
        => _logger.LogWarning(
            "SMART sign-in {Outcome}: method={Method} reason={Reason}",
            "refused", Clean(method), Clean(reason));

    internal static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "-";
        var buffer = new StringBuilder(Math.Min(value.Length, 256));
        foreach (var ch in value)
        {
            if (buffer.Length == 256) break;
            buffer.Append(char.IsControl(ch) ? '_' : ch);
        }
        return buffer.ToString();
    }
}

/// <summary>Input rules for identifiers that end up in tokens.</summary>
public static partial class SmartIdentifiers
{
    /// <summary>A FHIR resource id: [A-Za-z0-9\-\.]{1,64}.</summary>
    public static bool IsFhirId(string? value)
        => !string.IsNullOrEmpty(value) && FhirIdPattern().IsMatch(value);

    public static bool IsClientId(string? value)
        => !string.IsNullOrEmpty(value) && ClientIdPattern().IsMatch(value);

    /// <summary>Ten digits with a valid check digit (Luhn over the 80840 prefix).</summary>
    public static bool IsNpi(string? value)
    {
        if (value is not { Length: 10 } || !value.All(char.IsAsciiDigit)) return false;
        var digits = "80840" + value;
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 1)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
        }
        return sum % 10 == 0;
    }

    /// <summary>A 160-bit random enrolment code, shown once.</summary>
    public static string NewEnrolmentCode()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(20))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

    public static string HashCode(string code)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim())));

    [GeneratedRegex("^[A-Za-z0-9\\-\\.]{1,64}$")]
    private static partial Regex FhirIdPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9\\-]{2,63}$")]
    private static partial Regex ClientIdPattern();
}
