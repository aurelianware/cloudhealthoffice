# ADR 029: Preserve PA source text and explicit terminology translations

Status: proposed implementation, October 24, 2026. Extends ADR 008's owning
service projection; does not change authorization or payment decisions.

Strict HL7 validation with terminology enabled found that a submitter's
`ProcedureDescription` is not necessarily a valid canonical CPT display.
Project that description as `CodeableConcept.text`, preserving the code/system
but omitting an unverified `Coding.display`. Do not replace it with a copied
licensed descriptor merely to make a fixture pass.

The recorded `NOTMEDNEC` review reason means the requested service was found
not medically necessary. Its explicit projection crosswalk adds X12 CARC 50,
whose meaning is a payer's medical-necessity noncoverage determination. Keep
the original local coding and human-readable explanation alongside the CARC.
Use the CARC system already used by claims-service. This is a semantic
translation at read time, with source evidence retained, not a rewrite of the
stored decision, a new denial policy or an 835 payment adjustment.

Only exact `NOTMEDNEC` is mapped. Do not infer a CARC from arbitrary prose,
case-fold unknown vocabularies or use a generic fallback for other reasons.
`UNITSRED`, for example, does not say enough to choose a specific CARC.
G-17 therefore remains partial until the plan's full reason taxonomy and
versioned crosswalk are verified. Historical records retain the source code;
rolling back removes the additional projection coding but rewrites no data.

Acceptance: behavior tests preserve text and source codings, map the explicit
case and leave unknowns unmapped; regenerate the PA golden from the actual
projector; run strict PDex fixture validation with terminology. Do not erase
residual errors, change clinical fixtures to avoid them or weaken the profile.
Fixture success is not complete deployed API conformance.

Source reviewed October 24, 2026:
[X12 Claim Adjustment Reason Codes, code 50](https://x12.org/codes/claim-adjustment-reason-codes).

## Claim procedure vocabulary correction

Strict CARIN validation also found HCPCS J1745 and CDT D2740 incorrectly
labelled AMA CPT by the shared claim/EOB scaffold. Use one projection helper
for billed Claim, financial EOB and nonfinancial EOB: recognized D+four-digit
codes use ADA CDT, other letter+four-digit codes use CMS HCPCS, numeric CPT
and numeric-prefix/alphabetic-suffix CPT codes use AMA CPT. Free-text
descriptions stay `CodeableConcept.text` on all three paths. Unknown shapes
retain a CHO-local system rather than falsely claiming CPT.

The legacy claim model does not retain an explicit source procedure system/
qualifier. This inference is a bounded correction, not a universal code-system
resolver: ambiguous HIPPS/local codes require preserving the intake qualifier
in a separate model/intake change. It does not establish validity of a code
within its vocabulary. Strict terminology validation remains required.

The system URIs are verified against the pinned CARIN BB 2.2.0 procedure
and ADA value sets. Claim/benefit/payment values and stored records are
unchanged; only the FHIR projection's vocabulary and description placement
change. Regenerate and review all affected goldens from the owning projectors.
