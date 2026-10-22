# Provider Access: the CARIN BB non-financial ExplanationOfBenefit

## What this is, and why it is not the Patient Access EOB

CMS-0057-F requires four APIs. The rule itself names only FHIR R4, US Core and
SMART App Launch; the implementation guides are recommended rather than
mandated. But the guide CMS points Provider Access at — Da Vinci PDex — is
specific about the payload, and it is not what CHO was serving.

PDex carries Provider Access claims **and encounters** inside CARIN Blue
Button's **non-financial** `ExplanationOfBenefit` profiles. There is no FHIR
`Claim` and no FHIR `Encounter` in a Provider Access response.

CARIN BB publishes two parallel families:

| Family | Requires | Who it is for |
| --- | --- | --- |
| `C4BB-ExplanationOfBenefit-*` | Everything, amounts included | Patient Access: a member seeing what their plan paid |
| `C4BB-ExplanationOfBenefit-*-Basis` | Everything **but** the amounts | A payer sending another provider the record of an encounter |

The Basis family exists for exactly this case. The plan's financial terms are
between the plan and the member; a provider treating that member needs the
clinical and administrative record, not the contracted rate. So the Provider
Access projection emits no `total`, no `item.net` or `unitPrice`, and no
adjudication amount of any kind. An acceptance scenario asserts the absence on
the serialized JSON rather than on a parsed resource, so an amount cannot slip
through under an element the test did not think to name.

## Where the Claim and Encounter projections stand now

ADR 024 added CHO-native FHIR `Claim` and `Encounter` projections, and the gap
matrix recorded them as the Provider Access payload. That was wrong, and the
correction is a narrowing rather than a retraction: both projections remain the
CHO-native **source records** behind the Provider Access payload, and `Claim`
stays the right answer to "what did the provider bill" for any caller who asks
that question directly. They are simply not what PDex puts on the Provider
Access wire.

## Which profile an instance declares

`CarinNonFinancialEobProjector` picks from the claim type:

| CHO claim type | Profile |
| --- | --- |
| Professional (837P) | `C4BB-ExplanationOfBenefit-Professional-NonClinician-Basis` |
| Institutional (837I), inpatient | `C4BB-ExplanationOfBenefit-Inpatient-Institutional-Basis` |
| Institutional (837I), outpatient | `C4BB-ExplanationOfBenefit-Outpatient-Institutional-Basis` |
| Dental (837D) | `C4BB-ExplanationOfBenefit-Oral-Basis` |

Inpatient versus outpatient comes from the UB-04 type of bill. CHO stores its
first two digits in `InstitutionalClaimDetails.FacilityTypeCode`: the first is
the facility type and the second the bill classification, where 1 and 2 are
inpatient (Part A and Part B) and 3 upward are outpatient and other. A claim
with no institutional detail at all is treated as **outpatient**, deliberately:
the inpatient profile additionally requires an admission period, so an unknown
bill classification must not land on the stricter profile and claim conformance
it cannot back.

The four variants differ in more than the profile URL, which is why there are
four goldens rather than one:

- The inpatient profile requires an admission period and a revenue code on every
  line.
- The outpatient institutional profile narrows `item.serviced[x]` to a date, so a
  period there is a type error rather than extra precision.
- The professional and oral profiles require a CMS place of service in
  `item.location[x]` — which is where place of service belongs. The `Claim`
  projection carries it as a CHO-local extension only because R4 `Claim` has
  nowhere else to put it.
- The professional and oral profiles require `benefitpaymentstatus` on each item;
  the institutional profiles require it at the header; the oral profile requires
  it in both places and adds a rendering network status.

## How the request reaches it

```
fhir-service   ExplanationOfBenefitController
     │         SMART scope, attribution, ProviderAccess consent, member confinement
     │         IsProviderAccessRequest decides which upstream route
     │ HTTP: GET /fhir/ProviderAccess/ExplanationOfBenefit[/{id}]
claims-service FhirExplanationOfBenefitController
     └───────→ CarinNonFinancialEobProjector
```

`IsProviderAccessRequest` is true when the request carries no patient-bound
token but `ProviderAccessAuthorizationFilter` has authorized it for a member.
A patient-bound token keeps the Patient Access route and the adjudicated EOB.

Both payloads are served by **one pair of controller actions** in
claims-service, selected by the request path. That is deliberate: everything
else about the two is identical — the chain-head read, the rule that stops an
`_id` lookup tunnelling to another member, the paging — and a second controller
would be a second chance to get one of those wrong on only one of the paths.

The resource id is the same chain-stable `ClaimVersionId` the `Claim` and
Patient Access EOB projections use, so `Claim/{id}`,
`ExplanationOfBenefit/{id}` and the Provider Access read all name one claim.

## What is not yet conformant

Validated in CI by the HL7 validator against CARIN BB 2.2.0
(`scripts/interop/fhir-validate.sh`). Each of the four goldens reports exactly
one error, and it is the same error:

**`resolve()` has nothing to read in a single file.** Every CARIN BB care-team
role but `purchasedservice` carries an invariant that the provider `resolve()`
to a `Practitioner`.

This was first recorded as "CHO publishes no `Practitioner` resource", which was
wrong. provider-service has projected `us-core-practitioner` keyed by NPI since
capability 5.7, and fhir-service proxies it at `fhir/r4/Practitioner/{id}` — the
same base this EOB is served from — so `careTeam.provider` carries a literal
`Practitioner/{npi}` and resolves against a live server. What cannot resolve is a
relative reference inside a standalone instance, which is all the validator is
given here, and `-tx n/a` leaves it no way to fetch one.

So the line stays in `conformance/validator-baseline.txt` as a limit of the
harness, and the thing the harness cannot show is shown elsewhere:
`The_care_team_reference_resolves_to_a_practitioner_the_directory_serves` in
`tests/Cms0057Acceptance.Tests` takes the reference off the payload and reads it
back from provider-service's `Practitioner` endpoint. Clearing the validator
line itself needs a run over a `Bundle`, or Inferno against a running server
(gap G-60). It is not worked around by choosing a role the invariant ignores:
the 837's rendering provider is a rendering provider.

The header `provider` is a different case and is deliberately left as an
identifier reference. It comes from the billing NPI, which may belong to a
group, and C4BB allows either an `Organization` or a `Practitioner` there. CHO
cannot tell which from the claim alone, so it says what it knows rather than
asserting a type that would 404 for every group practice. `insurer` and
`insurance.coverage` are identifier-and-display references for the plainer
reason that there is no `Organization` or `Coverage` resource to point at.

What the validator cannot see at all is recorded in
`scripts/interop/fhir-validate.sh`: terminology checking is off, so the required
bindings on procedure codes, place of service and network status go unverified,
and a single instance resolves no reference target. Both need Inferno (gap
G-60).

## Encounters

PDex puts encounters in the same non-financial EOB. This projection covers
claims only; encounter-service's records are still served as FHIR `Encounter`.
That is the next piece of the same work, and the gap matrix says so rather than
implying Provider Access is complete.
