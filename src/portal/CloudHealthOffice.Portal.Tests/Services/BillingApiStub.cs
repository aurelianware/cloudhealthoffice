using System.Net;
using System.Text;

namespace CloudHealthOffice.Portal.Tests.Services;

/// <summary>
/// Stands in for premium-billing-service and sponsor-service: answers by
/// method and path, and records every call with its body (read while the
/// request is alive, since the clients dispose their requests).
/// </summary>
public sealed class BillingApiStub : HttpMessageHandler
{
    public sealed record Call(string Method, string PathAndQuery, string? Body)
    {
        public string Path => PathAndQuery.Split('?')[0];
    }

    private readonly List<(string Method, string Path, Func<Call, HttpResponseMessage> Respond)> _routes = new();
    private readonly List<Call> _calls = new();

    public IReadOnlyList<Call> Calls => _calls;

    public bool ThrowConnectionError { get; set; }

    /// <summary>Answer <paramref name="method"/> on <paramref name="path"/> (exact, without query) with a JSON body.</summary>
    public BillingApiStub On(string method, string path, HttpStatusCode status, string? json = null)
        => On(method, path, _ => Json(status, json));

    public BillingApiStub On(string method, string path, Func<Call, HttpResponseMessage> respond)
    {
        _routes.Insert(0, (method, path, respond)); // later registrations win
        return this;
    }

    public IEnumerable<Call> CallsTo(string method, string path)
        => _calls.Where(c => c.Method == method && c.Path == path);

    public static HttpResponseMessage Json(HttpStatusCode status, string? json)
    {
        var response = new HttpResponseMessage(status);
        if (json != null)
            response.Content = new StringContent(json, Encoding.UTF8,
                (int)status >= 400 && json.Contains("\"title\"") ? "application/problem+json" : "application/json");
        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (ThrowConnectionError) throw new HttpRequestException("connection refused");
        var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var call = new Call(request.Method.Method, request.RequestUri!.PathAndQuery, body);
        _calls.Add(call);
        var route = _routes.FirstOrDefault(r => r.Method == call.Method && r.Path == call.Path);
        return route.Respond == null
            ? Json(HttpStatusCode.NotFound, "{\"error\":\"no stub for " + call.Method + " " + call.Path + "\"}")
            : route.Respond(call);
    }
}

/// <summary>Response bodies in the shapes premium-billing-service and sponsor-service send (camelCase, enum names).</summary>
public static class BillingApiShapes
{
    public const string RunPending = """
        {"tenantId":"t1","id":"run-1","billingRunNumber":"BR-2026-03-001","description":"March","billingPeriod":"2026-03-01T00:00:00",
         "status":"Pending","criteria":{"groupNumbers":[],"lineOfBusiness":null,"billingFrequency":null},"invoiceIds":[],
         "totalInvoices":0,"totalPremiumAmount":0,"totalAdjustmentAmount":0,"totalMembers":0,"createdAt":"2026-02-25T10:00:00Z",
         "createdBy":"maker-1","createdByIsService":false,"executedBy":null,"executedByIsService":false,"cancelledBy":null,
         "executionStartedAt":null,"executionCompletedAt":null,"executionDurationSeconds":null,"errors":[],"warnings":[]}
        """;

    public const string RunCompleted = """
        {"tenantId":"t1","id":"run-2","billingRunNumber":"BR-2026-02-001","description":null,"billingPeriod":"2026-02-01T00:00:00",
         "status":"Completed","criteria":{"groupNumbers":["G1","G2"]},"invoiceIds":["inv-1","inv-2"],
         "totalInvoices":2,"totalPremiumAmount":12500.50,"totalAdjustmentAmount":-50,"totalMembers":40,"createdAt":"2026-01-25T10:00:00Z",
         "createdBy":"maker-1","createdByIsService":false,"executedBy":"maker-2","executedByIsService":false,
         "executionStartedAt":"2026-01-25T10:01:00Z","executionCompletedAt":"2026-01-25T10:02:00Z","executionDurationSeconds":60.2,
         "errors":["Sponsor G9: coverage-service answered 503"],
         "warnings":["Sponsor G3 has no approved bank account; needs attention"]}
        """;

    public static string Invoice(string id = "inv-1", string status = "Sent", string? suspension = null) => $$$"""
        {"tenantId":"t1","id":"{{{id}}}","invoiceNumber":"INV-G1-2026-02","billingRunId":"run-2","groupNumber":"G1","sponsorName":"Acme Corp",
         "billingPeriodStart":"2026-02-01T00:00:00","billingPeriodEnd":"2026-02-28T00:00:00","status":"{{{status}}}","dueDate":"2026-02-15T00:00:00",
         "lineItems":[{"memberId":"M1","memberName":"Pat Doe","coverageId":"C1","planId":"PLAN-A","coverageLevel":"EMP","insuranceLineCode":"HLT",
                       "subscriberPremium":100,"employerContribution":400,"totalPremium":500,"effectiveDate":"2026-01-01T00:00:00",
                       "terminationDate":null,"prorationFactor":1.0,"isRetroactive":false,"adjustmentReason":null}],
         "adjustments":[{"type":"Credit","description":"Goodwill","amount":-50,"relatedMemberId":null,"adjustmentDate":"2026-02-02T00:00:00"}],
         "payments":[],"subtotalPremium":500,"totalAdjustments":-50,"totalAmount":450,"totalPaid":0,"balanceDue":450,"memberCount":1,
         "gracePeriodDays":30,"gracePeriodExpires":"2026-03-17T00:00:00","isAptcSubsidized":false,"aptcMonthlyAmount":0,"graceType":"Standard",
         "createdAt":"2026-01-25T10:02:00Z","lastUpdatedAt":"2026-01-25T10:02:00Z","createdBy":"maker-2","lastUpdatedBy":null,
         "sponsorSuspension":{{{suspension ?? "null"}}} }
        """;

    public const string FailedSuspension = """
        {"state":"Failed","attempts":2,"lastAttemptAt":"2026-03-20T08:00:00Z","lastStatusCode":503,
         "lastError":"sponsor-service unavailable","lastAttemptBy":"fin-1","suspendedAt":null}
        """;

    public const string Aging = """
        {"currentAmount":1000,"currentCount":2,"thirtyDayAmount":450,"thirtyDayCount":1,"sixtyDayAmount":0,"sixtyDayCount":0,
         "ninetyPlusDayAmount":0,"ninetyPlusDayCount":0,"totalOutstanding":1450,"totalCount":3}
        """;

    public const string DelinquencyFailed = """
        {"delinquentCount":3,"sponsorsSuspended":1,"suspensionRetries":1,
         "suspensionFailures":[{"invoiceId":"inv-7","invoiceNumber":"INV-G7-2026-01","groupNumber":"G7","statusCode":403,"error":"Forbidden by sponsor-service"}],
         "message":"3 invoices marked delinquent; 1 sponsor suspension(s) FAILED in sponsor-service (recorded on the invoices and retried on the next run)"}
        """;

    public const string DelinquencyOk = """
        {"delinquentCount":2,"sponsorsSuspended":2,"suspensionRetries":0,"suspensionFailures":[],"message":"2 invoices marked delinquent"}
        """;

    public static string Draft(string invoiceId = "inv-1", string status = "Submitted") => $$"""
        {"tenantId":"t1","id":"d-{{invoiceId}}","invoiceId":"{{invoiceId}}","invoiceNumber":"INV-{{invoiceId}}","groupNumber":"G1","amount":450,
         "method":"Nacha","status":"{{status}}","traceNumber":"091000010000001","stripePaymentIntentId":null,"nachaBatchId":"B1",
         "nachaFileReference":"NACHA-1","routingNumberLast4":"0019","accountNumberLast4":"6789","createdAt":"2026-02-16T09:00:00Z",
         "submittedAt":"2026-02-16T09:00:00Z","expectedSettlementDate":"2026-02-18T00:00:00","settledAt":null,"returnCode":null,
         "returnReason":null,"returnedAt":null,"retryCount":0,"maxRetries":2,"initiatedBy":"approver-1","lastUpdatedBy":null,
         "lastUpdatedAt":"2026-02-16T09:00:00Z","errorMessage":null}
        """;

    public const string BatchWithAttention = """
        {"totalInvoices":2,"draftsInitiated":1,"skipped":0,"errors":1,"totalAmount":450,"draftIds":["d-inv-1"],
         "errorMessages":["Invoice INV-G3-2026-02: sponsor G3 has no approved bank account"],"nachaFile":null,
         "needsAttention":[{"invoiceId":"inv-2","draftId":null,"groupNumber":"G3","reason":"No approved bank account for sponsor G3"}]}
        """;

    /// <summary>Full numbers that must never appear on a page.</summary>
    public const string FullAccountNumber = "000123456789";
    public const string FullRoutingNumber = "091000019";

    /// <summary>premium-billing-service sent the file to the bank: masked summary and receipt.</summary>
    public const string NachaResult = """
        {"fileReference":"NACHA-20260216","fileName":"ACH-1234567890-20260216-090000.ach",
         "entryCount":1,"totalAmount":450,"totalDebitAmount":450,"totalCreditAmount":0,"generatedAt":"2026-02-16T09:00:00Z",
         "transmissionStatus":"Transmitted","transmissionError":null,"heldUntil":null,
         "receipt":{"tenantId":"t1","fileReference":"NACHA-20260216","remoteFileName":"ACH-1234567890-20260216-090000.ach",
           "destination":"sftp://sftp.bank.example:22/inbound","byteSize":940,
           "sha256":"9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08","entryCount":1,
           "totalDebitAmount":450,"totalCreditAmount":0,"transmittedAt":"2026-02-16T09:00:05Z","transmittedBy":"approver-1",
           "runId":null,"batchId":"NACHA-20260216"},
         "entries":[{"draftId":"d-1","invoiceId":"inv-1","groupNumber":"G1","accountHolderName":"ACME CORP",
           "routingNumberLast4":"0019","accountNumberLast4":"6789","amount":450,"traceNumber":"091000010000001"}],
         "needsAttention":[{"invoiceId":null,"draftId":"d-9","groupNumber":"G4","reason":"Sponsor G4 bank account refused by sponsor-service"}]}
        """;

    /// <summary>The bank could not be reached: the file is held for a platform admin or a second approver.</summary>
    public const string NachaAwaitingRetrieval = """
        {"fileReference":"NACHA-HELD0001","fileName":"ACH-1234567890-20260216-091500.ach",
         "entryCount":1,"totalAmount":450,"totalDebitAmount":450,"totalCreditAmount":0,"generatedAt":"2026-02-16T09:15:00Z",
         "transmissionStatus":"AwaitingRetrieval","transmissionError":"The upload to the bank's SFTP server failed (SshConnectionException).",
         "heldUntil":"2026-02-23T09:15:00Z","receipt":null,
         "entries":[{"draftId":"d-1","invoiceId":"inv-1","groupNumber":"G1","routingNumberLast4":"0019","accountNumberLast4":"6789","amount":450}],
         "needsAttention":[]}
        """;

    public const string HeldFiles = """
        [{"fileReference":"NACHA-HELD0001","fileName":"ACH-1.ach","status":"AwaitingRetrieval",
          "reason":"The upload to the bank's SFTP server failed (SshConnectionException).","entryCount":1,
          "totalDebitAmount":450,"totalCreditAmount":0,"byteSize":940,"sha256":"ab","runId":"run-2","releasedBy":"approver-1",
          "createdAt":"2026-02-16T09:15:00Z","expiresAt":"2026-02-23T09:15:00Z","attempts":0,"lastAttemptBy":null,"retrievalCount":0}]
        """;

    public const string SeparationOfDuties = """
        {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Separation of duties","status":403,
         "detail":"User maker-2 executed billing run BR-2026-02-001 and cannot release its debits."}
        """;

    public const string SponsorSeparationOfDuties = """
        {"type":"https://cloudhealthoffice.com/problems/bank-account-separation-of-duties","title":"Separation of duties","status":403,
         "detail":"The user who proposed a bank-account change cannot approve or reject it."}
        """;

    public const string Stale = """{"error":"The active account changed after this change was proposed; propose again."}""";

    /// <summary>A masked read. Includes full numbers as a misbehaving service might; the portal must not show them.</summary>
    public static string BankAccountView(string requestedBy = "proposer-1", bool withLeak = false) => $$$"""
        {"groupNumber":"G1",
         "active":{"eftEnabled":true,"preferredMethod":"Nacha","accountType":"Checking","accountHolderName":"Acme Corp",
                   {{{(withLeak ? $"\"routingNumber\":\"{FullRoutingNumber}\",\"accountNumber\":\"{FullAccountNumber}\"," : "")}}}
                   "stripeCustomerId":null,"stripePaymentMethodId":null,"routingNumberLast4":"0019","accountNumberLast4":"6789"},
         "activeApprovedBy":"approver-1","activeApprovedAt":"2026-01-10T12:00:00Z",
         "pending":{"id":"chg-2","groupNumber":"G1","status":"Pending",
                    "proposed":{"eftEnabled":true,"preferredMethod":"StripeAch","accountType":"Savings","accountHolderName":"Acme Corp",
                                "routingNumberLast4":"0248","accountNumberLast4":"4321"},
                    "numbersCarriedOver":false,"previousAccount":null,"requestedBy":"{{{requestedBy}}}","requestedAt":"2026-03-01T12:00:00Z",
                    "decidedBy":null,"decidedAt":null,"reason":null}}
        """;

    public const string BankAccountNone = """{"groupNumber":"G5","active":null,"activeApprovedBy":null,"activeApprovedAt":null,"pending":null}""";

    public const string ChangeHistory = """
        [{"id":"chg-2","groupNumber":"G1","status":"Pending","proposed":{"eftEnabled":true,"preferredMethod":"StripeAch","accountType":"Savings",
           "routingNumberLast4":"0248","accountNumberLast4":"4321"},"numbersCarriedOver":false,"previousAccount":null,
           "requestedBy":"proposer-1","requestedAt":"2026-03-01T12:00:00Z","decidedBy":null,"decidedAt":null,"reason":null},
         {"id":"chg-1","groupNumber":"G1","status":"Approved","proposed":{"eftEnabled":true,"preferredMethod":"Nacha","accountType":"Checking",
           "routingNumberLast4":"0019","accountNumberLast4":"6789"},"numbersCarriedOver":false,"previousAccount":null,
           "requestedBy":"proposer-0","requestedAt":"2026-01-09T12:00:00Z","decidedBy":"approver-1","decidedAt":"2026-01-10T12:00:00Z","reason":"verified"}]
        """;

    public const string ChangeApproved = """
        {"id":"chg-2","groupNumber":"G1","status":"Approved","proposed":{"eftEnabled":true,"preferredMethod":"StripeAch","accountType":"Savings",
         "routingNumberLast4":"0248","accountNumberLast4":"4321"},"numbersCarriedOver":false,"previousAccount":null,
         "requestedBy":"proposer-1","requestedAt":"2026-03-01T12:00:00Z","decidedBy":"approver-1","decidedAt":"2026-03-02T12:00:00Z","reason":null}
        """;
}
