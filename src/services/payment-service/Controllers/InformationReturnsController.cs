using System.Globalization;
using System.Text;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using PaymentService.Repositories;
using PaymentService.Services.InformationReturns;

namespace PaymentService.Controllers;

/// <summary>
/// Year-end information returns for provider payments: the 1099-MISC box 6
/// (medical and health care payments) total for every payee TIN, from the
/// payments this plan issued, with the payees that cannot be reported yet
/// (no TIN on file). For finance:read. TINs are shown by their last four
/// digits only; the IRS filing itself (FIRE/IRIS) is produced elsewhere.
/// </summary>
[ApiController]
[Route("api/payments/1099-misc/{taxYear:int}")]
[Produces("application/json")]
public sealed class InformationReturnsController : ControllerBase
{
    /// <summary>Distinct payee NPIs looked up at once.</summary>
    private const int LookupConcurrency = 8;

    private readonly IPaymentRepository _payments;
    private readonly IProviderTaxIdentitySource _identities;
    private readonly ICurrentActor _actor;

    public InformationReturnsController(IPaymentRepository payments, IProviderTaxIdentitySource identities, ICurrentActor actor)
    {
        _payments = payments;
        _identities = identities;
        _actor = actor;
    }

    [HttpGet]
    [RequirePermission("finance:read")]
    [ProducesResponseType(typeof(Form1099Summary), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult> Get(int taxYear, CancellationToken ct)
    {
        var built = await BuildAsync(taxYear, ct);
        return built.Error ?? Ok(built.Summary);
    }

    /// <summary>The same as CSV for the year-end workpaper (TIN last four only).</summary>
    [HttpGet("csv")]
    [RequirePermission("finance:read")]
    [Produces("text/csv")]
    public async Task<ActionResult> Csv(int taxYear, CancellationToken ct)
    {
        var built = await BuildAsync(taxYear, ct);
        if (built.Error != null) return built.Error;
        var s = built.Summary!;
        var csv = new StringBuilder("recipient_key,tin_last4,tin_type,name,npis,box6_medical_payments,payments,reportable,w9_on_file\n");
        foreach (var r in s.Recipients)
            csv.Append(Field(r.RecipientKey)).Append(',').Append(r.TinLast4).Append(',').Append(r.TinType).Append(',')
               .Append(Field(r.Name)).Append(',').Append(Field(string.Join(' ', r.Npis))).Append(',')
               .Append(r.Box6MedicalPayments.ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
               .Append(r.Payments).Append(',').Append(r.Reportable ? "Y" : "N").Append(',').Append(r.W9OnFile ? "Y" : "N").Append('\n');
        foreach (var e in s.Exceptions)
            csv.Append("EXCEPTION,,,").Append(Field(e.PayeeName)).Append(',').Append(Field(e.PayeeNpi)).Append(',')
               .Append(e.Amount.ToString("0.00", CultureInfo.InvariantCulture)).Append(",,,").Append(Field(e.Reason)).Append('\n');
        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"1099-misc-box6-{taxYear}.csv");
    }

    private async Task<(Form1099Summary? Summary, ActionResult? Error)> BuildAsync(int taxYear, CancellationToken ct)
    {
        if (taxYear < 2000 || taxYear > DateTime.UtcNow.Year)
            return (null, BadRequest(new { error = "taxYear must be a past or current calendar year" }));

        var from = new DateTime(taxYear, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var payments = await _payments.GetIssuedPaymentsAsync(from, from.AddYears(1), ct);
        var npis = payments.Select(p => p.PayeeNpi?.Trim()).Where(n => !string.IsNullOrEmpty(n)).Distinct().Cast<string>().ToList();

        var identities = new System.Collections.Concurrent.ConcurrentDictionary<string, PayeeTaxIdentity?>();
        try
        {
            await Parallel.ForEachAsync(npis, new ParallelOptions { MaxDegreeOfParallelism = LookupConcurrency, CancellationToken = ct },
                async (npi, token) => identities[npi] = await _identities.GetAsync(_actor.TenantId, npi, token));
        }
        catch (HttpRequestException ex)
        {
            return (null, StatusCode(StatusCodes.Status502BadGateway,
                new { error = "Payee TINs could not be read from provider-service; nothing was totalled", detail = ex.Message }));
        }

        return (Form1099MiscBuilder.Build(taxYear, payments, identities), null);
    }

    private static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
