namespace PremiumBillingService;

/// <summary>
/// The text a billing run, a delinquency run or an EFT batch records for a
/// failure the caller will read. A refusal this service raises for its caller
/// (an <see cref="InvalidOperationException"/> or <see cref="ArgumentException"/>
/// with a state or validation message) or a downstream service's status
/// answer keeps its message; any other exception (a database driver, a
/// connection or a parser failure, which can carry hosts, connection strings
/// or document ids) is replaced by <see cref="Unexpected"/> and stays in the log.
/// </summary>
internal static class ClientErrorText
{
    public const string Unexpected = "An unexpected error occurred; it has been logged.";

    public static string For(Exception ex) =>
        ex is InvalidOperationException or ArgumentException or HttpRequestException { StatusCode: not null }
            ? ex.Message
            : Unexpected;
}
