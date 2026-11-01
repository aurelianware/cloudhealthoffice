# Claims and the plan's PA rule set

`ClaimsAdjudication:PriorAuth:UseRuleSet` (claims-service, **off by default**)
makes claims adjudication check prior authorization on the lines the plan's PA
rule set puts in scope. It is the same rule set CRD, PAS `$submit` and the
CMS-0057-F required-services list answer from: the prior-auth rule store
(`prior_auth_rules`) resolved for the plan's program, i.e. its state, line of
business and program code. A plan loads its own list with the
[prior-auth list importer](../integration/PRIOR-AUTH-RULES-IMPORTER.md); without
one the platform state set applies where there is one.

With the setting on, a claim edit for a missing authorization and a CRD answer
to the ordering provider come from one source.

## How it works

1. claims-service reads `GET /api/v1/prior-auth-rules/plans/{planId}/required-services?includeDrugs=true`
   from benefit-plan-service under its own service identity, and caches the
   answer per tenant and plan for 5 minutes (like the plan lookup).
2. benefit-plan-service finds the plan's program in tenant-service and resolves
   the rule set exactly as the engine does (`PaRuleSetResolver`).
3. A line is **in scope** when an entry of the list matches it:
   - `Always`: the procedure code (or a prefix such as `K*`), at the listed
     places of service if any (the line's POS, else the claim's);
   - `WithDiagnosis`: the code, billed with one of the listed diagnoses.
4. Lines in scope are checked against their authorization exactly like the
   other sources' lines (2400 REF*G1, else 2300; `/validate` per line).

The rule set only **adds** lines. The inpatient Medicaid POS 21 floor and the
plan benefit flags (`UsePlanFlaggedCodes`) are unchanged.

## Outcomes

| Situation | Result |
|---|---|
| Plan has no program, unknown program, or no rule set | Nothing added; the other sources decide. |
| Rule set lookup fails (benefit-plan-service or tenant-service down) | Pend `PAUNAVAIL`, transient, cleared by the re-run like other PA lookup failures. |
| Line in scope, valid authorization | Continues. |
| Line in scope, no or invalid authorization | Deny CARC 197, **unless** the line is in scope only through the rule set and the rule set has exemptions (see below). |
| ...and the rule set has exemptions | Pend `NOAUTH` ("Missing Authorization" queue) for an examiner. |

### Exemptions

Rule sets can waive PA for some requests: the Texas gold-card exemption, a
provider-type exemption, a member-age rule. Claims cannot evaluate them yet
(no gold-card status or provider taxonomy at this point of the pipeline), so a
claim from an exempt provider must not be denied for a missing authorization.
A missing or invalid authorization on a line that only the rule set put in
scope therefore pends `NOAUTH` for review when the set has any exemption. The
pend reason names the lines, the rules and the exemption kinds.

### Not applied yet

- `AboveQuantityLimit` rules: claims do not count a member's use against the
  limit yet.
- Exemptions are not evaluated (above).

## Turning it on

Same prerequisites as `UsePlanFlaggedCodes` (see
[CLAIMS-ADJUDICATION-SERVICE-IDENTITY.md](CLAIMS-ADJUDICATION-SERVICE-IDENTITY.md)):
claims-service's service identity must be accepted by benefit-plan-service with
`benefits:read`, and benefit-plan-service needs `Services:TenantService` (default
`http://tenant-service`) to read programs. Then check the tenant's published
list (`GET /api/v1/prior-auth-rules/required-services?...`) before setting
`ClaimsAdjudication__PriorAuth__UseRuleSet=true`.
