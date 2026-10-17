# Service-client resilience

**Status:** implemented.
- **Code:** `CloudHealthOffice.Infrastructure.Http.ChoServiceClientResilience`.
- **Where it runs:** every factory-built `HttpClient` in every service that
  calls `AddChoAuthentication`, plus token-service and the portal.

## What every client gets

| Strategy | Default | Notes |
|---|---|---|
| Attempt timeout | The client's own `HttpClient.Timeout`; 10 s if it sets none | See [Timeouts](#timeouts-follow-each-clients-own-httpclienttimeout). |
| Total timeout | The larger of 30 s and attempt × (retries + 1) plus twice the nominal back-off | Covers every attempt. |
| Retry | 2 retries, exponential back-off from 250 ms with jitter, `Retry-After` honoured | Only transient outcomes: 408, 429, 5xx, transport errors and timeouts. **Only idempotent requests**, see below. |
| Circuit breaker | Opens when 50 % of at least 10 calls in 30 s fail transiently; stays open 15 s | Per client name. State survives handler rotation. |

**Which requests are retried.** GET, HEAD, OPTIONS, TRACE, PUT and DELETE are
retried. So is any request carrying an `Idempotency-Key` header. A POST or
PATCH is never retried unless one of these opts in:
- the client sets `RetryNonIdempotent`. The Anthropic Messages client does,
  because a repeated completion has no side effect;
- the request sets the `ChoServiceClientResilience.AllowRetry` option.

A PUT or DELETE that is not idempotent can opt out with `NoRetry`.

**How failures surface.** They use the exceptions callers already handle:
- **Open circuit:** `HttpRequestException`, `StatusCode` 503, with an inner
  `BrokenCircuitException`.
- **Timeout:** `TaskCanceledException` with an inner `TimeoutException`, as
  `HttpClient.Timeout` produces.

The pipeline is the outermost handler, so `ChoOutboundTokenHandler` runs once
per attempt.

## Timeouts follow each client's own `HttpClient.Timeout`

Before this was fixed, every client got a 10 s attempt and a 30 s total
timeout, whatever it had set for itself. A Stedi call (30 s), a FHIR
Payer-to-Payer call (up to 300 s) or a 15 s benefit-plan lookup was cut at
10 s, and a client's own `HttpClient.Timeout` still capped all attempts
together, so a retry after a `Retry-After` wait could not finish either.

Now:
- **Attempt timeout.** The pipeline reads the timeout the client's own
  configuration sets (`AddHttpClient(name, c => c.Timeout = ...)`,
  `ConfigureHttpClient(...)`). When it is first built, it runs that
  configuration against a client that sends nothing. That timeout becomes
  the attempt timeout. A client that sets none gets 10 s. A client set to
  `Timeout.InfiniteTimeSpan` gets no pipeline timeout.
- **Total timeout.** The larger of 30 s and attempt × (retries + 1), plus
  twice the nominal exponential back-off to allow for jitter. A
  `Retry-After` longer than that back-off can still run into the total.
- **`HttpClient.Timeout`** is then raised to one second past the total, so
  the pipeline's timeouts govern. It still bounds reading a buffered
  response body after the headers arrive.
- **Explicit settings win.** `ConfigureChoResilience(o => o.AttemptTimeout = ...)`
  or `o.TotalTimeout = ...` replace the derived values.
- **A typed client must not set `HttpClient.Timeout` in its constructor.**
  The constructor runs after the factory, so the pipeline cannot see that
  value, and the constructor would overwrite the raised one. The Anthropic
  client and the Anthropic connectivity check did this. Both now set it at
  registration.

`ChoResiliencePipelines.Timeouts(clientName)` returns the values a client
gets.

### Inventory

These are the clients that set `HttpClient.Timeout`, with the timeouts they
get now. Unless the client sets other values, there are 2 retries from
250 ms, so attempt *a* gives a total of max(30 s, 3*a* + 1.5 s). Retries
happen only for idempotent requests. `HttpClient.Timeout` is the total plus
1 s.

| Service | Client | Own timeout | Attempt | Total |
|---|---|---|---|---|
| claims-service | `MemberService`, `CoverageService`, `AuthorizationService`, `ClaimsDefault` (tenant config) | 5 s | 5 s | 30 s |
| claims-service | `BenefitPlanService` | `Services:BenefitPlanServiceTimeoutSeconds` (5 s; 15 s in Development) | same | 30 s (46.5 s at 15 s) |
| claims-service | `ProviderService`, `ReferenceDataService`, `TerminologyService` | 10 s | 10 s | 31.5 s |
| claims-examiner-service | `IAnthropicClient` | `Anthropic:TimeoutSeconds` (60 s) | 60 s | 268 s (4 attempts, back-off from 2 s) |
| claims-examiner-service | `AnthropicConnectivityCheck` | max(`Anthropic:TimeoutSeconds`, 5 s) | 60 s | 181.5 s |
| claims-examiner-service | `IClaimsServiceClient` | 15 s | 15 s | 46.5 s |
| eligibility-service | `EligibilityDefault`, `IEligibilityService` | 5 s | 5 s | 30 s |
| eligibility-service, provider-eligibility-api | Stedi: `StediHealthcare` (270/271, 837, 276/277, 835), `StediClaims`, `StediClaimAttachmentUpload`, `StediCore`, `StediPayerDirectory` | `Stedi:TimeoutSeconds` (30 s) | 30 s | 91.5 s |
| fhir-service | Payer-to-Payer remote | `PayerToPayer` transport `TimeoutSeconds` (30 s, 1–300 s) | same | 91.5 s (901.5 s at 300 s) |
| fhir-service | `HttpFhirAppealAdapter`, `ProviderService`, `BenefitPlanService`, `ClaimsService` | 30 s | 30 s | 91.5 s |
| fhir-service | SMART issuer metadata | 10 s | 10 s | 31.5 s |
| fhir-service | `ProviderVerificationService` | 5 s | 5 s | 30 s |
| payment-service | claims-service client | 30 s | 30 s | 91.5 s |
| payment-service | GL event sink | 15 s | 15 s | 46.5 s |
| payment-service | provider payee accounts, trading partners | 10 s | 10 s | 31.5 s |
| premium-billing-service | coverage, member, sponsor, sponsor bank accounts | 30 s | 30 s | 91.5 s |
| capitation-service | `CoverageService`, `ProviderService`, provider bank accounts, `RiskAdjustmentService` | 30 s | 30 s | 91.5 s |
| capitation-service | tenant payment controls | 10 s | 10 s | 31.5 s |
| provider-verification-service | NPPES | 30 s | 30 s | 91.5 s |
| provider-verification-service | exclusion dataset download | `DownloadTimeout` + 1 min (11 min) | 11 min | 33 min |
| provider-verification-service | SAM.gov exclusions API | infinite (the screener times each request) | none | none |
| provider-service | `IProviderVerificationClient` | `ProviderVerification:TimeoutSeconds` (30 s) | 30 s | 91.5 s |
| provider-service | tenant config | 5 s | 5 s | 30 s |
| benefit-plan-service | tenant config, operating mode, provider integrity (provider-service), terminology crosswalk | 5 s | 5 s | 30 s |
| benefit-plan-service | claims accumulator source, provider integrity (verification) | 10 s | 10 s | 31.5 s |
| work-management-service | claims work source | 30 s | 30 s | 91.5 s |
| encounter-submission-service | `ClaimsService` | 30 s | 30 s | 91.5 s |
| encounter-submission-service | `ReferenceDataService` | 10 s | 10 s | 31.5 s |
| authorization-service | claims usage | 15 s | 15 s | 46.5 s |
| authorization-service | RFAI request gateway | 10 s | 10 s | 31.5 s |
| enrollment-import-service | member, sponsor, benefit-plan, coverage | 10 s | 10 s | 31.5 s |
| personal-representative-service | tenant controls, member documents | 10 s | 10 s | 31.5 s |
| idcard-service | `IdCardDefault`, adapter client | 10 s | 10 s | 31.5 s |
| attachment-service | trading-partner lookup, per-service clients | 10 s | 10 s | 31.5 s |
| member-service | typed service clients | 10 s | 10 s | 31.5 s |
| token-service | tenant directory | 10 s | 10 s | 31.5 s |
| every service (shared) | token-service token client, NACHA settings source | 10 s | 10 s | 31.5 s |

These are outside the pipeline, so their own `HttpClient.Timeout` applies
unchanged:
- `new HttpClient { Timeout = ... }`: the two token-service signing-key
  fetchers (10 s) and `HttpDependencyHealthCheck` (5 s);
- the capitation contract-split migration's own service provider (30 s);
- `src/engines/cho-enrollment-wiring` hosts, which do not call
  `AddChoAuthentication`;
- tools (`mcc-platform-validator`, `migration-wizard`).

## Per-client settings

```csharp
services.AddHttpClient<IAnthropicClient, AnthropicClient>()
    .ConfigureChoResilience(o => { o.RetryNonIdempotent = true; o.MaxRetryAttempts = 3; }); // attempt timeout: the client's HttpClient.Timeout

services.AddHttpClient("vendor").WithoutChoResilience().AddStandardResilienceHandler(); // brings its own
```

These clients have their own settings:
- **ProviderEnrollment engine (5 clients):** they keep their
  `AddStandardResilienceHandler` and opt out of the default.
- **claims-examiner-service Anthropic client:**
  - 429 and 529 are retried (3 times, from 2 s), POST included;
  - one attempt may take the configured model timeout (`Anthropic:TimeoutSeconds`,
    set as the client's `HttpClient.Timeout` at registration). The total covers
    all four attempts.

## Adjudication: an outage pends, it is never denied

When a dependency is unreachable, times out or has an open circuit, the
claims stage pends with **`ClaimAdjudicationStageResult.CheckUnavailable`**.
The stage sets that flag itself, and it is recorded on the run context. An
examiner approval never overrides such a pend; the re-run retries the check.
`AnyCheckFailed` and the override check read the flag, plus the transient
pend codes, and never the reason text. Before, these decisions
substring-matched the reason against a list of phrases, so rewording a
message could make an outage overridable.

## Not covered

- `HttpClient` instances created with `new HttpClient()`, which bypass the
  factory.
- gRPC and SDK clients that do not use `IHttpClientFactory`, such as Cosmos,
  Service Bus and Kafka.
