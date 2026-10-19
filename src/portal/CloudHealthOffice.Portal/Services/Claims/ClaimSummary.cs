using System.Text.Json.Serialization;

namespace CloudHealthOffice.Portal.Services;

// DTOs
public class ClaimSummary
{
    private string _claimId = string.Empty;

    public string ClaimId
    {
        get => _claimId;
        set => _claimId = string.IsNullOrWhiteSpace(value) ? _claimId : value;
    }

    [JsonPropertyName("id")]
    public string? Id
    {
        get => _claimId;
        set => _claimId = string.IsNullOrWhiteSpace(value) ? _claimId : value;
    }

    public string ClaimNumber { get; set; } = string.Empty;

    // claims-service returns its Claim document, not this view model: member and
    // provider names and the adjudicated amounts arrive under their own field
    // names. These fill the display fields when the explicit ones are absent.
    private string _memberName = string.Empty;
    private string _providerName = string.Empty;
    private string _providerId = string.Empty;
    private string? _subscriberFirstName, _subscriberLastName, _patientFirstName, _patientLastName;

    [JsonPropertyName("subscriberFirstName")] public string? SubscriberFirstNameWire { set => _subscriberFirstName = value; }
    [JsonPropertyName("subscriberLastName")] public string? SubscriberLastNameWire { set => _subscriberLastName = value; }
    [JsonPropertyName("patientFirstName")] public string? PatientFirstNameWire { set => _patientFirstName = value; }
    [JsonPropertyName("patientLastName")] public string? PatientLastNameWire { set => _patientLastName = value; }

    public string BillingProviderName { get; set; } = string.Empty;
    public string BillingProviderNPI { get; set; } = string.Empty;

    /// <summary>Adjudication result on the claims-service document; maps the amounts.</summary>
    [JsonPropertyName("adjudicationResult")]
    public ClaimAdjudicationProjection? AdjudicationResult
    {
        set
        {
            if (value is null)
            {
                return;
            }

            AllowedAmount = value.AllowedAmount;
            PaidAmount = value.PayerPayment;
            OnAdjudicationResult(value);
        }
    }

    protected virtual void OnAdjudicationResult(ClaimAdjudicationProjection value)
    {
    }

    private static string JoinName(string? first, string? last) =>
        string.Join(' ', new[] { first, last }.Where(p => !string.IsNullOrWhiteSpace(p))).Trim();

    [JsonIgnore]
    public string DisplayClaimNumber => string.IsNullOrWhiteSpace(ClaimNumber) ? ClaimId : ClaimNumber;
    public string MemberName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_memberName)) return _memberName;
            var patient = JoinName(_patientFirstName, _patientLastName);
            return patient.Length > 0 ? patient : JoinName(_subscriberFirstName, _subscriberLastName);
        }
        set => _memberName = value ?? string.Empty;
    }
    public string MemberId { get; set; } = string.Empty;
    public string ProviderName
    {
        get => string.IsNullOrWhiteSpace(_providerName) ? BillingProviderName : _providerName;
        set => _providerName = value ?? string.Empty;
    }
    public string ProviderId
    {
        get => string.IsNullOrWhiteSpace(_providerId) ? BillingProviderNPI : _providerId;
        set => _providerId = value ?? string.Empty;
    }
    [JsonConverter(typeof(FlexibleClaimTypeJsonConverter))]
    public string ClaimType { get; set; } = string.Empty; // Professional, Institutional, Dental
    [JsonConverter(typeof(FlexibleDecimalJsonConverter))]
    public decimal TotalChargeAmount { get; set; }
    [JsonConverter(typeof(FlexibleDecimalJsonConverter))]
    public decimal AllowedAmount { get; set; }
    [JsonConverter(typeof(FlexibleDecimalJsonConverter))]
    public decimal PaidAmount { get; set; }
    [JsonConverter(typeof(FlexibleClaimStatusJsonConverter))]
    public string Status { get; set; } = string.Empty;
    public DateTime ServiceDateFrom { get; set; }
    public DateTime ServiceDateTo { get; set; }
    public DateTime SubmittedDate { get; set; }
    public DateTime? AdjudicatedDate { get; set; }
    public int ProcessingTimeMs { get; set; }
    public string? PriorAuthorizationNumber { get; set; }
    public int LineCount { get; set; }
}
