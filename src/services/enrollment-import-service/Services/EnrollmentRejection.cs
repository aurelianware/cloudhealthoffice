using System.Net;
using EnrollmentImportService.Clients;
using EnrollmentImportService.Models;

namespace EnrollmentImportService.Services;

/// <summary>
/// Why a transaction was not applied (or a note on one that was), in the
/// shape stored on <see cref="EnrollmentTransaction"/>. Messages follow
/// <see cref="ClientErrorText"/>: an unexpected exception's text stays in
/// the log, never in the stored record.
/// </summary>
internal sealed record EnrollmentRejection(string ReasonCode, IReadOnlyList<EnrollmentTransactionError> Errors)
{
    public static EnrollmentRejection Of(string reasonCode, string message) =>
        new(reasonCode, [new EnrollmentTransactionError { Code = reasonCode, Message = message }]);

    /// <summary>For an accepted transaction that needs explaining (e.g. a skipped termination).</summary>
    public static EnrollmentRejection Note(string reasonCode, string message) => Of(reasonCode, message);

    public static EnrollmentRejection FromValidation(EnrollmentValidationResult validation) =>
        new(EnrollmentRejectionReasons.ValidationFailed,
            validation.Errors.Select(e => new EnrollmentTransactionError { Code = e.Code, Field = e.Field, Message = e.Message }).ToList());

    public static EnrollmentRejection FromException(Exception ex)
    {
        var code = ex switch
        {
            EnrollmentConcurrencyException => EnrollmentRejectionReasons.ConcurrentUpdate,
            MemberServiceException m => Downstream(m.StatusCode),
            CoverageServiceException c => Downstream(c.StatusCode),
            SponsorServiceException s => Downstream(s.StatusCode),
            HttpRequestException { StatusCode: { } status } => Downstream(status),
            HttpRequestException or TaskCanceledException => EnrollmentRejectionReasons.DownstreamUnavailable,
            InvalidOperationException or ArgumentException => EnrollmentRejectionReasons.InvalidTransaction,
            _ => EnrollmentRejectionReasons.UnexpectedError,
        };
        var message = ex switch
        {
            // The status, not the downstream body: it can echo identifiers.
            MemberServiceException m => Answer("member-service", m.StatusCode),
            CoverageServiceException c => Answer("coverage-service", c.StatusCode),
            SponsorServiceException s => Answer("sponsor-service", s.StatusCode),
            _ when code == EnrollmentRejectionReasons.DownstreamUnavailable && ClientErrorText.For(ex) == ClientErrorText.Unexpected
                => "A service needed to apply this change could not be reached.",
            _ => ClientErrorText.For(ex),
        };
        return Of(code, message);
    }

    private static string Answer(string service, HttpStatusCode status) =>
        $"{service} answered {(int)status} ({status}).";

    /// <summary>A 5xx or 429 is the service being unavailable (safe to retry); anything else is a refusal.</summary>
    private static string Downstream(HttpStatusCode status) =>
        (int)status >= 500 || status == HttpStatusCode.TooManyRequests
            ? EnrollmentRejectionReasons.DownstreamUnavailable
            : EnrollmentRejectionReasons.DownstreamRejected;
}
