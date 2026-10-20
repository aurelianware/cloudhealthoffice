# Claims adjudication service identity (D-36 / D-40)

Claims adjudication owns these upstream reads, including examiner approval
readjudication:

| Client | Request |
| --- | --- |
| `HttpCoverageClient` | `GET /api/v1/coverage/member/{memberId}/cob` |
| `HttpCoverageResolver` | `GET /api/v1/coverage/member/{memberId}/active` |
| `HttpBenefitPlanResolver` | `GET /api/v1/plans/{planId}` |

Each request now asks the existing `ChoOutboundTokenHandler` for
`UseChoServiceIdentity`, as prior-authorization validation already does. This
replaces the forwarded examiner token with the claims-service token for the
adjudication tenant. Stored user roles do not need downstream `coverage:read`
or `benefits:read` grants to make these internal pipeline calls. Direct user
API permissions and examiner identity in the claims workflow remain intact.

## Tenant and credential boundaries

The existing handler mints a service token only for an allowlisted CHO host.
Inside an authenticated request it requires a resolved caller tenant and refuses
to attach credentials for a different tenant. Background calls use the tenant
named by trusted pipeline code in `X-Tenant-ID`. Token-source failure does not
fall back to the examiner token. The receiving services still validate the token
and bind repository/adapter calls to its tenant; a disagreeing tenant header is
refused.

No shared authentication policy, user role catalogue, token issuer configuration,
service-client registration or deployment secret is changed by this fix. A target
deployment still needs a working claims-service token source, matching trusted
issuer/audience and workload registration, and correct internal host routing.
No deployment or live identity-provider verification was performed here.

## Failure behavior

Existing lookup meanings are preserved: COB 404 is an empty list (confirmed no
other coverage); active-coverage and plan 404 are not found. Other COB/active
lookup failures remain null/degraded; plan refusal/outage remains
`BenefitPlanUnavailableException`. Existing stage policies decide the claim
outcome. This fix does not change enforcement modes, error classification or
turn a failed lookup into a valid coverage/plan result.

## Plan-flagged prior authorization remains a separate rollout

`ClaimsAdjudication:PriorAuth:UsePlanFlaggedCodes` remains false by default.
G-07 and the D-36/D-40 code prerequisites are implemented, but this change does
not expand PA scope. G-64 still tracks enabling it in the intended deployment,
including tenant plan content and claims-to-authorization line mapping. Repeated
procedure codes, line identity and unit consumption require their own validation.
The existing institutional Medicaid inpatient floor remains in place.

## Verification

`AdjudicationLookupsCarryServiceIdentityTests` exercises the real factory client
and shared outbound handler for examiner/supervisor and background contexts. It
covers foreign/missing caller tenants, untrusted hosts, token-source outages,
403/503 responses and the existing 404 meanings. Its readjudicator tests use the
real approval orchestrator and COB stage to resolve a missing plan, load plan PA
flags and avoid the caller-token-induced COB pend. The observation stage is a
test seam; this is not a complete payment-posting or live-payer integration test.

`CoveragePipelineAuthTests` and `ClaimsPlanReadAuthenticationTests` exercise the
real receiving HTTP/authentication pipelines with repository/adapter test doubles.
They prove token acceptance and token-derived tenancy, including tenant-header
mismatch rejection and unchanged direct-user restrictions on plan reads.

## .NET 8 validation prerequisite

The intervening dependency update #1415 introduced Azure.Core 1.63 and a new
shared-infrastructure reference, causing NU1605 against the existing Microsoft
Extensions 8.x graph. The accompanying compatibility repair restores the earlier
Azure.Core/System.Text.Json central versions and removes that new direct
reference. Compatible Kafka/OpenAPI updates are retained. Both NuGet Dependabot
groups hold the incompatible upgrades for coordinated review. No downgrade
warnings are suppressed, and the SDK/application target framework remains .NET 8.

The dependency review workflow also stops enabling automatic merge. GitHub
Actions merged #1415 despite failing checks because main did not enforce the
required CI gates. Automatic review remains; dependency PRs stay open for
verification of their exact-head results before an authorized merge. This is a
workflow change, not a change to repository branch-protection settings.
