# FHIR fixture validation modes

The existing regression mode remains unchanged: it disables terminology by
default and requires the observed error set to match the dated baseline.
A green regression result is **not** zero-error conformance.

A separate qualification invocation is now available:

```bash
FHIR_VALIDATION_STRICT=1 \
FHIR_TERMINOLOGY_SERVER=https://tx.fhir.org/r4 \
  ./scripts/interop/fhir-validate.sh
```

Strict mode refuses disabled/unconfigured terminology, rejects every error
including baseline entries, and rejects a nonzero validator exit or a run that
produces no verdict. The validator jar remains checksum-pinned. This invocation
contacts the configured terminology service: use synthetic fixtures only, or
an approved service/agreement for any real data. Never send PHI to a public
terminology service.

Both modes use the profiles/packages named by the golden resources and
`conformance/validator-manifest.json`. These pins are product conformance
targets, not assertions that every guide/version is legally mandated.

Strict success would establish only the checked fixtures under the selected
packages and terminology configuration. It would not establish deployed
availability, SMART/OAuth, tenant/member confinement, source completeness,
reference resolution on a live server, timeframes, public reporting or full
CMS-0057-F compliance. External Inferno/server scenarios remain necessary.

Mode regression tests use a synthetic validator to test the shell gates:

```bash
python3 scripts/interop/tests/test_fhir_validate_modes.py
```

Those tests do not validate FHIR resources. An unavailable package server or
terminology service is a blocked run, never a passing conformance result.
Keep the actual logs, selected package pins and source commit with the
acceptance evidence.
