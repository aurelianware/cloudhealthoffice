# State Medicaid manual PA rules (TMPPM)

CHO ingests the Texas Medicaid Provider Procedures Manual (TMPPM) monthly
with `tools/CloudHealthOffice.TmppmIngestionService` into terminology-service
(`tmppm_pa_rules`, browsable in the portal's PA Rule Explorer). CRD, PAS
`$submit`, the CMS-0057-F published list and claims adjudication do **not**
read that collection: they read the PA rule store (`prior_auth_rules`). This
step promotes the manual's rules into that store for a tenant, so all of them
answer from the manual.

## Endpoint

`POST /api/v1/prior-auth-rules/import/state-manual` (benefit-plan-service).
Permission `settings:manage`; writes the token tenant's rule set only.
**Dry run by default**: the report is the review. Nothing is written until
`dryRun=false`.

| Query parameter | Default | Meaning |
|---|---|---|
| `stateCode` | `TX` | The manual's state. Texas is the only one ingested today. |
| `lob` | `Medicaid` | Line of business of the target rule set. |
| `program` | none | Program code (`STAR`, `STARPLUS`, ...). Promote once per program the platform has a program set for (see the importer's warnings). |
| `keepExemptions` | `true` | As for the CSV importer. |
| `includeCodes` | `false` | Every code in the report, not the first 25. |
| `dryRun` | `true` | `false` writes. |

benefit-plan-service reads `GET api/v1/tmppm/rules?state=TX` from
terminology-service with its own service identity (`Services:TerminologyServiceUrl`,
default `http://terminology-service.cloudhealthoffice:5010/`). If that fails
the endpoint answers 503 and writes nothing.

## Which manual rules become rules

A manual rule is promoted when it says authorization is required, names at
least one procedure code, is not a "not covered" rule, and is in effect today
(`effectiveDate` on or before, `endDate` absent or not before). Each promoted
section becomes one `ProcedureRequiresAuth` rule, id `import:tmppm:<section id>`,
named after the section reference and category. The rest are counted in
`manualRulesLeftOut`.

Diagnosis, age and unit conditions extracted from a section are **not**
applied: the codes need authorization whenever billed. Those sections are
listed in `conditionsNotApplied` for review. Mapping them to conditions is a
follow-up once the extraction is checked against the manual.

## Beside the plan's own list

The promotion runs through the [prior-auth list importer](PRIOR-AUTH-RULES-IMPORTER.md)
under the `tmppm` namespace:

- The plan's own imported list in the same scope stays in place; the rule set
  is the union of both. Re-importing either list leaves the other alone.
- Promoting a new edition disables manual sections that dropped out
  (`import.rulesDisabled`).
- Like any tenant set, it replaces the platform set the scope resolved to
  (`import.replacesRuleSet`, `import.rulesNoLongerApplied`), with exemptions
  kept by default.

The MCO's own PA list takes precedence in practice: Texas Medicaid managed care
plans set their own requirements, and the TMPPM is the fee-for-service manual.
Use the manual as the baseline and load the plan's list for its differences.

## Review before writing

Check, in the dry run:

- `warnings`: sections with more than 200 codes, and rules from more than one
  edition. The extractor takes every code-shaped token in a section's text,
  so a large section may include codes the manual mentions without requiring
  authorization. Sections removed from the manual are never deleted from
  terminology-service, so they stay until a new extraction overwrites them.
- `import.rules` with `includeCodes=true` against the manual sections.
- `import.skipped`: codes the importer could not read.

```bash
# Dry run for STAR
curl -sS -X POST "$CHO/api/v1/prior-auth-rules/import/state-manual?stateCode=TX&lob=Medicaid&program=STAR" \
  -H "Authorization: Bearer $TOKEN" | jq '.manualRulesPromoted, .manualRulesLeftOut, .warnings, .import.rules'

# Write it
curl -sS -X POST "$CHO/api/v1/prior-auth-rules/import/state-manual?stateCode=TX&lob=Medicaid&program=STAR&dryRun=false" \
  -H "Authorization: Bearer $TOKEN"
```

## California: Medi-Cal TAR and Non-Benefit List

California's fee-for-service source is the Medi-Cal Provider Manual's Part 2
"TAR and Non-Benefit List" sections: one PDF per CPT range (codes 10000 thru
19999 ... 80000 thru 89999), each a table of Code | Description | Benefit
Restrictions. Managed care plans still set their own lists; load those with
the CSV importer, and the two apply side by side.

`POST /api/v1/prior-auth-rules/import/medi-cal-tar` (benefit-plan-service),
multipart `files` (the PDFs, or a `.zip` of them). Permission
`settings:manage`. **Dry run by default.** Always CA.

| Query parameter | Default | Meaning |
|---|---|---|
| `lob` | `Medicaid` | Line of business of the target rule set. |
| `program` | none | Program code. |
| `nonBenefitNeedsTar` | `true` | Load "Non-Benefit" codes as needing a TAR: DHCS denies them unless an approved TAR shows medical necessity. `false` leaves them out. |
| `keepExemptions`, `includeCodes`, `dryRun` | as above | |

- "Requires TAR" rows become one `ProcedureRequiresAuth` rule per range,
  `import:medi-cal:tar-<from>-thru-<to>`; Non-Benefit rows become
  `import:medi-cal:non-benefit-<from>-thru-<to>`.
- "Assistant Surgeon services not payable" alone is a billing rule: counted in
  `noTar`, not loaded.
- Rows with no recognised restriction are warned about, not loaded.
- **Upload every range each time.** The upload is the whole Medi-Cal list for
  the scope: ranges left out are disabled. The plan's own CSV list is not.
- Not covered: TAR requirements written into other manual sections (inpatient,
  DME, some HCPCS), and limits stated in the description text.

The reader groups the PDF's words into lines by baseline. It has been tested on
generated PDFs laid out like the published ones, not yet on the DHCS files
themselves: review the first dry run (`files[]` counts per range, `warnings`,
`import.rules` with `includeCodes=true`) against the PDFs.

## Other states

Texas (TMPPM) and California (TAR list) are covered. Another state needs its
own source: a code-level list can go through the CSV importer or a reader
like the Medi-Cal one; a narrative manual needs an extractor like the TMPPM
tool feeding terminology-service (the promotion step above takes any state).
