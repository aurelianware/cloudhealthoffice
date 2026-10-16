# Service-client resilience

**Status:** implemented.
- **Code:** `CloudHealthOffice.Infrastructure.Http.ChoServiceClientResilience`.
- **Where it runs:** every factory-built `HttpClient` in every service that
  calls `AddChoAuthentication`, plus token-service and the portal.

## What every client gets

| Strategy | Default | Notes |
|---|---|---|
| Total timeout | 30 s | Covers every attempt. The client's own `HttpClient.Timeout` still applies on top: a 5 s client stays a 5 s client. |
| Attempt timeout | 10 s | |
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

## Per-client settings

```csharp
services.AddHttpClient<IAnthropicClient, AnthropicClient>()
    .ConfigureChoResilience(o => { o.RetryNonIdempotent = true; o.AttemptTimeout = TimeSpan.FromSeconds(60); });

services.AddHttpClient("vendor").WithoutChoResilience().AddStandardResilienceHandler(); // brings its own
```

These clients have their own settings:
- **ProviderEnrollment engine (5 clients):** they keep their
  `AddStandardResilienceHandler` and opt out of the default.
- **claims-examiner-service Anthropic client:**
  - 429 and 529 are retried (3 times, from 2 s), POST included;
  - one attempt may take the configured model timeout (`Anthropic:TimeoutSeconds`).

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
