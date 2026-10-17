namespace CapitationService;

/// <summary>
/// The text a capitation run or a disbursement batch records for a failure
/// the caller will read. A refusal this service raises for its caller (an
/// <see cref="InvalidOperationException"/> or <see cref="ArgumentException"/>
/// with a state or validation message) keeps its message; any other exception
/// (a database driver, a connection or a parser failure, which can carry
/// hosts, connection strings or document ids) is replaced by
/// <see cref="Unexpected"/> and stays in the log.
/// </summary>
internal static class ClientErrorText
{
    public const string Unexpected = "An unexpected error occurred; it has been logged.";

    public static string For(Exception ex) =>
        ex is InvalidOperationException or ArgumentException ? ex.Message : Unexpected;
}
