# FQHC and RHC encounter pricing (Medicaid managed care)

Code: `src/engines/CloudHealthOffice.FeeScheduleEngine/Medicaid/ClinicEncounterPricer.cs`.

Federal law (SSA §1902(bb)) entitles a federally qualified health center or
rural health clinic to its per-visit prospective payment (PPS) rate, or a
state alternative, for each qualifying visit. When the visit is for a managed
care member, the money reaches the clinic in one of two ways, set by
`ClinicPaymentMode`:

| Mode | Plan pays | State pays |
| --- | --- | --- |
| `ContractedRateStateWraps` (default) | Its contracted rate for the visit | The difference up to PPS (the "wrap") |
| `PlanPaysPps` | The full per-visit rate on the encounter line, with the visit's other lines at $0 | Nothing |

In both modes the pricer records each encounter's expected state wrap, which is
the rate minus the plan payment, never below zero. The plan then has it for
encounter reporting and for clinic questions about wrap reconciliation.

## How a claim is priced

1. **Encounters.** Lines are grouped by service date. A date has an encounter
   when a line carries an encounter code (`T1015` by default).
2. **Categories.** An encounter belongs to a category (medical by default;
   dental or behavioral when the policy maps a modifier or revenue code to
   one). Each category gets at most `MaxEncountersPerDayPerCategory`
   encounters (default 1). Extra encounter lines become part of the visit.
3. **What a visit covers.** It is all-inclusive. The category's other lines
   that day belong to it. Lines whose category has no encounter that day fold
   into the day's first encounter.
4. **Rates.** The rate is the clinic's rate in force on the service date. A
   category-specific rate wins over a general one.
5. **Days without an encounter.** Lines on a date with no encounter line price
   by the contract alone and earn no wrap.

Anything missing (no rate, a line with no contracted amount, duplicate line
numbers) leaves the claim unpriced with a reason, so it pends.

## Texas

SPA TX-21-0009 (effective 1 Feb 2021) says FQHCs choose PPS or APPS. APPS is
the PPS rate with an MEI + 0.5% update and is never less than PPS. It also
says: "In the event an FQHC is not paid the PPS or APPS rate by a managed care
organization …, the state will ensure the FQHC is paid the full amount",
at least quarterly. So the default is `ContractedRateStateWraps`. Set
`ClinicEncounterRate.Method` to `APPS` for a clinic on the alternative.

## Medi-Cal

W&I §14087.325(d) requires a plan to pay a contracted clinic no less than it
pays other providers for the same services. DHCS pays the wrap, first as an
interim per-visit payment and then through annual reconciliation. From
**1 July 2026**, AB 116 (W&I §14132.100(c)) ends the wrap for state-only
services, meaning services with no federal match. Those stay at the negotiated
plan rate. Set `ClinicClaim.StateOnlyServices` from the member's aid code.
The California policy turns the wrap off for those claims from that date.

## To verify with a plan

- **Encounter codes and categories.** Check which codes and categories mark an
  encounter on the plan's clinic claims. `T1015` is the common one, but some
  plans pay by E/M or CDT code with a clinic indicator.
- **Same-day visits.** Check how many encounters a day the state allows by
  category, for example medical plus dental plus behavioral.
- **Payment mode.** Check whether any clinic contract requires `PlanPaysPps`.
- **RHC rules.** Rural health clinics use the same mechanics, with their own
  rate letters.

## Status

These are the pricing rules only. Still to come:

- a clinic rate importer (state rate letters);
- wiring into `PricingStage`;
- a wrap report.

Until then, no claim is priced by this code.
