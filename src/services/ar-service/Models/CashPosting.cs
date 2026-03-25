using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ArService.Models;

/// <summary>
/// Cash receipt — applies payments received to open AR balances with batch posting rules.
/// QNXT analog: Cash Receipt.
/// </summary>
public class CashPosting
{
    [Required]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [StringLength(50)]
    public string PostingNumber { get; set; } = string.Empty;

    [Required]
    public DateTime ReceiptDate { get; set; }

    [Required]
    public decimal Amount { get; set; }

    public PaymentMethod PaymentMethod { get; set; }

    [StringLength(100)]
    public string? CheckNumber { get; set; }

    [StringLength(100)]
    public string? BankReference { get; set; }

    [Required]
    public PayerType PayerType { get; set; }

    [Required]
    public string PayerReferenceId { get; set; } = string.Empty;

    [StringLength(200)]
    public string? PayerName { get; set; }

    /// <summary>
    /// How the cash is applied to open balances
    /// </summary>
    public List<CashApplication> Applications { get; set; } = new();

    public decimal AppliedAmount { get; set; }
    public decimal UnappliedAmount { get; set; }

    public string? BatchRuleId { get; set; }

    [Required]
    public CashPostingStatus Status { get; set; } = CashPostingStatus.Pending;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;

    [StringLength(200)]
    public string? CreatedBy { get; set; }
}

public class CashApplication
{
    [Required]
    public string GlAccountId { get; set; } = string.Empty;

    [Required]
    public string ArBalanceId { get; set; } = string.Empty;

    [Required]
    public DateTime Period { get; set; }

    [Required]
    public decimal AmountApplied { get; set; }

    [StringLength(500)]
    public string? Memo { get; set; }
}

public enum PaymentMethod { Check = 1, Eft = 2, Wire = 3, Ach = 4, CreditCard = 5, Other = 99 }
public enum PayerType { Sponsor = 1, Member = 2, Medicare = 3, Medicaid = 4, Other = 99 }
public enum CashPostingStatus { Pending = 1, Applied = 2, PartiallyApplied = 3, Voided = 4 }
