using CapitationService.Models;

namespace CapitationService.Repositories;

public interface ICapitationStatementRepository
{
    Task<CapitationStatement?> GetByIdAsync(string id);
    Task<IEnumerable<CapitationStatement>> GetByRunIdAsync(string runId);
    Task<IEnumerable<CapitationStatement>> GetByProviderNpiAsync(string npi, DateTime? periodFrom = null, DateTime? periodTo = null);
    Task<IEnumerable<CapitationStatement>> GetByStatusAsync(CapitationStatementStatus status);
    Task<IEnumerable<CapitationStatement>> GetUnpaidStatementsAsync();
    Task<CapitationStatement> CreateAsync(CapitationStatement statement);
    Task<CapitationStatement> UpdateAsync(CapitationStatement statement);

    /// <summary>
    /// Approved to PaymentInitiated naming <paramref name="disbursementId"/>, as one
    /// conditional write before any money moves: of two releases of the same
    /// statement exactly one gets it. False when it is no longer Approved.
    /// </summary>
    Task<bool> TryStartPaymentAsync(string statementId, string disbursementId);

    /// <summary>
    /// PaymentInitiated by <paramref name="disbursementId"/> back to Approved: that
    /// payment did not go out. Does nothing otherwise.
    /// </summary>
    Task UndoStartPaymentAsync(string statementId, string disbursementId);

    /// <summary>
    /// PaymentInitiated by <paramref name="disbursementId"/> to PaymentUnknown:
    /// the payment may have gone out, so the statement is not payable until
    /// someone checks. False when it is not in that state.
    /// </summary>
    Task<bool> MarkPaymentUnknownAsync(string statementId, string disbursementId);

    /// <summary>
    /// PaymentUnknown by <paramref name="disbursementId"/>, once someone checked:
    /// paid goes to PaymentInitiated (settled later); not paid goes back to
    /// Approved (payable again). False when it is not in that state.
    /// </summary>
    Task<bool> ResolvePaymentUnknownAsync(string statementId, string disbursementId, bool paid);
}
