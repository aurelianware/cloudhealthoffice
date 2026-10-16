namespace EnrollmentImportService;

/// <summary>
/// The text an import result records for a subscriber or dependent that
/// failed; the result is returned to the caller. A refusal this service
/// raises for its caller (an <see cref="InvalidOperationException"/> or
/// <see cref="ArgumentException"/> with a validation message), a concurrency
/// refusal or a downstream service's status answer keeps its message; any
/// other exception (a database driver, a connection or a parser failure,
/// which can carry hosts, connection strings or document ids) is replaced by
/// <see cref="Unexpected"/> and stays in the log.
/// </summary>
internal static class ClientErrorText
{
    public const string Unexpected = "An unexpected error occurred; it has been logged.";

    public static string For(Exception ex) =>
        ex is InvalidOperationException or ArgumentException or Services.EnrollmentConcurrencyException
            or HttpRequestException { StatusCode: not null }
            ? ex.Message
            : Unexpected;
}
