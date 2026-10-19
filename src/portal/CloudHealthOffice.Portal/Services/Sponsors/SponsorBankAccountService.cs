namespace CloudHealthOffice.Portal.Services;

// ── Sponsor bank accounts (sponsor-service) ─────────────────────────────

public class SponsorBankAccountService : ISponsorBankAccountService
{
    internal const string ServiceName = "Sponsor Service";
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SponsorBankAccountService> _logger;

    public SponsorBankAccountService(HttpClient httpClient, IConfiguration configuration, ILogger<SponsorBankAccountService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    private string Url(string groupNumber, string path)
        => $"{_configuration["Services:SponsorService"]}/sponsors/{Uri.EscapeDataString(groupNumber.Trim())}/{path}";

    private Task<T> SendAsync<T>(HttpMethod method, string groupNumber, string path, object? body = null)
        => BillingApiHttp.SendAsync<T>(_httpClient, _logger, ServiceName, method, Url(groupNumber, path), body);

    public async Task<SponsorBankAccountView?> GetBankAccountAsync(string groupNumber)
    {
        try
        {
            return await SendAsync<SponsorBankAccountView>(HttpMethod.Get, groupNumber, "bank-account");
        }
        catch (BillingApiException ex) when (ex.StatusCode == 404)
        {
            return null;
        }
    }

    public async Task<List<SponsorBankAccountChange>> GetChangesAsync(string groupNumber)
        => await SendAsync<List<SponsorBankAccountChange>?>(HttpMethod.Get, groupNumber, "bank-account-changes") ?? new();

    public Task<SponsorBankAccountChange> ProposeChangeAsync(string groupNumber, ProposeSponsorBankAccountRequest request)
        => SendAsync<SponsorBankAccountChange>(HttpMethod.Post, groupNumber, "bank-account-changes", request);

    public Task<SponsorBankAccountChange> ApproveChangeAsync(string groupNumber, string changeId, string? reason)
        => Decide(groupNumber, changeId, "approve", reason);

    public Task<SponsorBankAccountChange> RejectChangeAsync(string groupNumber, string changeId, string? reason)
        => Decide(groupNumber, changeId, "reject", reason);

    public Task<SponsorBankAccountChange> CancelChangeAsync(string groupNumber, string changeId, string? reason)
        => Decide(groupNumber, changeId, "cancel", reason);

    private Task<SponsorBankAccountChange> Decide(string groupNumber, string changeId, string action, string? reason)
        => SendAsync<SponsorBankAccountChange>(HttpMethod.Post, groupNumber,
            $"bank-account-changes/{Uri.EscapeDataString(changeId)}/{action}",
            new DecisionBody { Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim() });

    private sealed class DecisionBody
    {
        public string? Reason { get; set; }
    }
}
