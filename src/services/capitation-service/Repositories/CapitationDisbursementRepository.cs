using CapitationService.Models;

namespace CapitationService.Repositories;

public interface ICapitationDisbursementRepository
{
    Task<CapitationDisbursement?> GetByIdAsync(string id);
    Task<IEnumerable<CapitationDisbursement>> GetByStatementIdAsync(string statementId);
    Task<IEnumerable<CapitationDisbursement>> GetByStatusAsync(DisbursementStatus status);
    Task<IEnumerable<CapitationDisbursement>> GetByStripeTransferIdAsync(string transferId);
    Task<CapitationDisbursement> CreateAsync(CapitationDisbursement disbursement);
    Task<CapitationDisbursement> UpdateAsync(CapitationDisbursement disbursement);
}
