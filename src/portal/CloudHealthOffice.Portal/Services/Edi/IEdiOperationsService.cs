namespace CloudHealthOffice.Portal.Services;

/// <summary>Downloads of the EDI CHO generated for one claim or payment.</summary>
public interface IEdiOperationsService
{
    /// <summary>The 277CA claims-service built for a claim (GET api/claims/{id}/277ca).</summary>
    Task<Stream> Download277CaAsync(string claimId);

    /// <summary>A payment's 835 (GET api/payments/{id}/835, audited).</summary>
    Task<Stream> DownloadEraAsync(string paymentId);
}
