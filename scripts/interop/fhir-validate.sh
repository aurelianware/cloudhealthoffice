#!/usr/bin/env bash
#
# Validates the golden FHIR projections in conformance/golden/ with the HL7
# validator, pinned in interop/versions.json under the key "hl7-validator".
#
#   ./scripts/interop/fhir-validate.sh                 # every golden
#   ./scripts/interop/fhir-validate.sh prior-authorization-eob
#
# Why out of process: the Firely SDK this repository pins (Hl7.Fhir.R4 5.10.2)
# ships no validator, and the current Firely.Fhir.Validation needs SDK 6. The
# HL7 validator is the same engine Inferno and Touchstone run, so a projection
# that passes here is judged by the tool the certification kits use.
#
# What this proves: structure, cardinality, slicing and invariants of the
# profile each instance DECLARES in meta.profile.
#
# What it does NOT prove, and why:
#   * Value-set bindings. Terminology checking is off (-tx n/a) so the run needs
#     no terminology server. CARC codes on a denial, for one, go unchecked.
#   * Reference targets. insurer/provider carry US Core targetProfiles, and a
#     single instance resolves to nothing, so the target profile is never read.
# Closing those two is Inferno's job, not this script's.
#
# Expected issues live in conformance/validator-baseline.txt, one "file: issue"
# line each. The script fails on any error that is not baselined, and fails just
# as loudly on a baselined error that no longer occurs — a stale baseline hides
# the next regression.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
VERSIONS="$REPO_ROOT/interop/versions.json"
GOLDEN_DIR="$REPO_ROOT/conformance/golden"
MANIFEST="$REPO_ROOT/conformance/validator-manifest.json"
BASELINE="$REPO_ROOT/conformance/validator-baseline.txt"
CACHE_DIR="${FHIR_VALIDATOR_CACHE:-$REPO_ROOT/interop/.external/hl7-validator}"
OUT_DIR="${FHIR_VALIDATE_OUT:-$REPO_ROOT/interop/.external/hl7-validator/out}"

read -r TAG ASSET SHA256 URL < <(python3 - "$VERSIONS" <<'PY'
import json, sys
versions = json.load(open(sys.argv[1]))
target = next(t for t in versions["targets"] if t["key"] == "hl7-validator")
pin = target["pin"]
print(pin["tag"], pin["asset"], pin["sha256"], pin["reference"])
PY
)

JAR="$CACHE_DIR/validator_cli-$TAG.jar"

command -v java >/dev/null || { echo "java is required (JRE 11 or later)" >&2; exit 2; }

# ── the pinned jar ────────────────────────────────────────────────────────────
mkdir -p "$CACHE_DIR" "$OUT_DIR"
if [[ ! -f "$JAR" ]]; then
  echo "==> Fetching $ASSET $TAG"
  curl -fsSL "$URL" -o "$JAR.tmp"
  mv "$JAR.tmp" "$JAR"
fi

ACTUAL="$(sha256sum "$JAR" | cut -d' ' -f1)"
if [[ "$ACTUAL" != "$SHA256" ]]; then
  echo "validator_cli.jar does not match the pin in interop/versions.json" >&2
  echo "  expected $SHA256" >&2
  echo "  actual   $ACTUAL" >&2
  exit 1
fi

# ── which goldens ─────────────────────────────────────────────────────────────
if [[ $# -gt 0 ]]; then
  FILES=()
  for name in "$@"; do FILES+=("$GOLDEN_DIR/$name.json"); done
else
  mapfile -t FILES < <(find "$GOLDEN_DIR" -name '*.json' | sort)
fi
[[ ${#FILES[@]} -gt 0 ]] || { echo "no goldens in $GOLDEN_DIR" >&2; exit 1; }

FAILED=0
OBSERVED="$OUT_DIR/observed.txt"
: > "$OBSERVED"

for file in "${FILES[@]}"; do
  [[ -f "$file" ]] || { echo "missing golden: $file" >&2; FAILED=1; continue; }
  name="$(basename "$file" .json)"
  log="$OUT_DIR/$name.log"

  # The instance names its own profile, and the manifest names the packages the
  # validator loads to resolve it. Neither is guessed from the file name.
  PROFILES=()
  while IFS= read -r line; do [[ -n "$line" ]] && PROFILES+=("$line"); done < <(
    python3 -c '
import json, sys
print("\n".join(json.load(open(sys.argv[1])).get("meta", {}).get("profile", [])))' "$file")

  IGS=()
  while IFS= read -r line; do [[ -n "$line" ]] && IGS+=("$line"); done < <(
    python3 -c '
import json, sys
manifest = json.load(open(sys.argv[1]))
entry = manifest["goldens"].get(sys.argv[2])
if entry is None:
    sys.exit("golden " + sys.argv[2] + " is not in conformance/validator-manifest.json")
print("\n".join(entry["igs"]))' "$MANIFEST" "$name")

  args=(-jar "$JAR" "$file" -version 4.0.1 -tx n/a -output-style compact)
  for ig in "${IGS[@]}"; do args+=(-ig "$ig"); done
  for profile in "${PROFILES[@]}"; do args+=(-profile "$profile"); done

  if [[ ${#PROFILES[@]} -eq 0 ]]; then
    echo "==> $name (base FHIR R4 only — the instance declares no profile)"
  else
    echo "==> $name against ${PROFILES[*]}"
  fi

  set +e
  java "${args[@]}" >"$log" 2>&1
  set -e

  # The exit code reports errors found, so it cannot distinguish a validated
  # instance from a crashed run. The summary line can: no summary, no verdict.
  if ! grep -qE '^\s*(Success|\*FAILURE\*|Validate )' "$log"; then
    echo "   the validator produced no verdict for $name — see $log" >&2
    tail -5 "$log" >&2
    FAILED=1
    continue
  fi

  # Issue lines read "[line, col] path: Severity - message". Only errors gate;
  # warnings and information are in the log for a human to read.
  grep -E ': Error - ' "$log" \
    | sed -E 's/^\[[0-9]+, [0-9]+\] //' \
    | sed "s|^|$name: |" >> "$OBSERVED" || true
  echo "   $(grep -cE ': Error - ' "$log" || true) error(s), $(grep -cE ': Warning - ' "$log" || true) warning(s)"
done

# ── baseline comparison ───────────────────────────────────────────────────────
sort -u "$OBSERVED" -o "$OBSERVED"
touch "$BASELINE"
EXPECTED="$OUT_DIR/expected.txt"
grep -vE '^\s*(#|$)' "$BASELINE" | sort -u > "$EXPECTED" || true

NEW="$(comm -23 "$OBSERVED" "$EXPECTED" || true)"
GONE="$(comm -13 "$OBSERVED" "$EXPECTED" || true)"

if [[ -n "$NEW" ]]; then
  echo
  echo "Validation errors that are not in conformance/validator-baseline.txt:"
  echo "$NEW"
  FAILED=1
fi
if [[ -n "$GONE" ]]; then
  echo
  echo "Baselined errors that no longer occur — remove them from conformance/validator-baseline.txt:"
  echo "$GONE"
  FAILED=1
fi

[[ $FAILED -eq 0 ]] && echo && echo "All goldens match the recorded validator baseline. Logs: $OUT_DIR"
exit $FAILED
