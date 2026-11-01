# Prior-Auth-Required List Importer

Loads a plan's own list of items and services that need prior authorization
into the prior-auth rule store (`prior_auth_rules`), as the tenant's rule set
for one state, line of business and program. It sits next to the
[fee schedule](FEE-SCHEDULE-IMPORTER.md) and contract imports, and the benefit
plan templates: a plan loaded from its own data also needs its own PA list.

## Who reads the rule set

| Reader | How |
|---|---|
| CRD (`order-select`, `order-sign`, ...) | `IPriorAuthRuleEngine` with the member's resolved state, LOB and program. A matching rule makes the card "authorization required". |
| PAS `$submit` | `PasAutoAdjudicator` Rule 5, same engine. |
| CMS-0057-F public list | `GET /api/v1/prior-auth-rules/required-services` (`format=csv` for posting). |
| Claims adjudication | **Not yet.** Claims still use the plan's `benefits[].priorAuthRequired` + `cptCodes` (behind `ClaimsAdjudication:PriorAuth:UsePlanFlaggedCodes`, off by default) and the Medicaid inpatient POS 21 floor. Pointing claims at this rule set is the next step. |

CRD's per-tenant code classification (`PUT /api/v1/crd/code-classification`)
is still read as well: a code it lists as auth-required, documentation-required
or auto-approved keeps that answer when no rule matches.

## Endpoint

`POST /api/v1/prior-auth-rules/import/csv` (benefit-plan-service), multipart
field `file` (CSV, or a `.zip` holding one). Permission `settings:manage`.
**Dry run by default**: nothing is written until `dryRun=false`.

| Query parameter | Required | Meaning |
|---|---|---|
| `stateCode` | yes | Two-letter state (`TX`). |
| `lob` | yes | `Commercial`, `Medicare`, `Medicaid`, `Exchange`, `TRICARE`, `VA`. |
| `program` | no | Program code (`STAR`). Without it the set covers every program of the state and LOB **that has no program set of its own** (see warnings). |
| `keepExemptions` | no (default `true`) | Copy the exemption rules (gold card, provider type, member age) of the rule set this import replaces into the imported set. |
| `includeCodes` | no | Return every code of every rule in the report, not the first 25. |
| `dryRun` | no (default `true`) | `false` writes. |

## The file

The header row is found by alias in the first 30 rows, so title rows above it
are fine. Names compare ignoring case, spaces and punctuation.

| Role | Aliases | Notes |
|---|---|---|
| codes (**required**, or code ranges) | `codes`, `Code`, `Procedure Code`, `CPT`, `HCPCS`, `CPT/HCPCS`, `Service Code`, `CDT` | 5-character CPT, HCPCS or CDT codes. A cell may hold several (space, comma, semicolon or pipe), ranges `70551-70553` / `70551 thru 70553` (same letters, at most 1,000 codes) and prefixes `K*` (1 to 4 characters). |
| code ranges | `code_ranges`, `Code Range`, `Range` | Same token rules as codes. |
| name | `name`, `Category`, `Service Category`, `Service Type`, `Service`, `Group` | Rows with the same name (and the same condition, places of service, diagnoses, limits) become one rule. |
| rule id | `rule_id` | Groups rows instead of the name. Stored as `import:<id>`. |
| PA required | `PA Required`, `Prior Auth Required`, `Auth Required`, `Requires Prior Authorization` | Rows saying `N`, `No`, `False`, `0`, `Not required` are skipped. |
| condition | `condition`, `When` | `Always`, `WithDiagnosis`, `AboveQuantityLimit`. Blank: inferred (limits → AboveQuantityLimit, diagnoses → WithDiagnosis, else Always). |
| places of service | `places_of_service`, `Place of Service`, `POS` | 1–2 digit codes. Applies to `Always` rules. |
| diagnoses | `diagnoses`, `Diagnosis Codes`, `ICD-10`, `Dx` | ICD-10-CM, dots optional. Required for `WithDiagnosis`. |
| unit / visit limit | `unit_limit`, `visit_limit` | Positive whole numbers; at least one for `AboveQuantityLimit`. |
| denied without review | `denied_without_review`, `Not Covered` | `true` makes an `Always` rule deny outright instead of pending for review. |
| all services | `all_services` | `true` with no codes: the rule covers every item and service. |

The CSV that `required-services?format=csv` returns uses exactly these
columns, so a list exported from one environment imports into another.

Rule types written: `Always` → `ProcedureRequiresAuth`, `WithDiagnosis` →
`DiagnosisRequired`, `AboveQuantityLimit` → `QuantityLimit`, with the engine's
own category and priority for each.

## What a write does

- The file is the **whole list** for its scope. Rules of an earlier import of
  the same tenant, state, LOB and program that the file no longer has are
  disabled (`rulesDisabled`). Rules this importer did not write are untouched.
- The rule set resolver uses the most specific **non-empty** set and does not
  merge: tenant + program, then platform + program, then tenant + any, then
  platform + any. The imported set therefore **replaces** whatever set the
  scope resolved to before. The report names it (`replacesRuleSet`) and the
  rules that stop applying (`rulesNoLongerApplied`).
- With `keepExemptions` (default), the replaced set's exemption rules are
  copied into the tenant set as `import:keep:<rule id>` and refreshed from the
  source on every import, so loading a plan's list does not switch off a
  state's gold-card law.
- Drug codes (HCPCS J) are loaded and applied by CRD and PAS, and reported in
  `drugCodes`; the CMS-0057-F public list leaves drugs out.
- Rule caches: benefit-plan-service invalidates on write. Other services
  (fhir-service) see the change at once when the cache backend is Redis, and
  within `PriorAuthRuleEngine:RuleSetCacheTtl` (15 minutes) when it is
  in-memory.

### Warnings

- A program-less import when the platform has program sets for the same state
  and LOB: requests naming those programs keep using the platform set. Import
  once per program.
- Drug codes present.
- The import replaces another rule set.

## Example

```bash
# Dry run: what would be loaded
curl -sS -X POST "$CHO/api/v1/prior-auth-rules/import/csv?stateCode=TX&lob=Medicaid&program=STAR" \
  -H "Authorization: Bearer $TOKEN" -F file=@acme-star-pa-list.csv | jq '.rules, .skipped, .warnings'

# Write it, then check the published list
curl -sS -X POST "$CHO/api/v1/prior-auth-rules/import/csv?stateCode=TX&lob=Medicaid&program=STAR&dryRun=false" \
  -H "Authorization: Bearer $TOKEN" -F file=@acme-star-pa-list.csv
curl -sS "$CHO/api/v1/prior-auth-rules/required-services?stateCode=TX&lob=Medicaid&program=STAR&format=csv" \
  -H "Authorization: Bearer $TOKEN"
```

## Not covered yet

- **Claims adjudication** reading this rule set (next PR), so CRD, PAS, the
  public list and claim edits all give the same answer.
- **Delegating the question** to the plan's UM or core system (Itiliti, QNXT)
  in real time, per plan, for plans whose list lives there.
- Effective dates on rules: the import is the list as of now; load the new
  list on the day it takes effect.
