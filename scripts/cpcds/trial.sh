#!/usr/bin/env bash
# trial.sh — Load a plan's CPCDS extracts (and UM authorizations) into a CHO
# the plan runs itself, check what loaded, and validate its CMS-0057-F APIs.
# The plan-hosted ("Mode A") kit: docs/integration/CPCDS-TRIAL-KIT.md.
#
#   ./scripts/cpcds/trial.sh check    <folder>   # dry run every file; writes nothing
#   ./scripts/cpcds/trial.sh load     <folder>   # load them, in dependency order
#   ./scripts/cpcds/trial.sh validate            # Inferno US Core, CARIN BB, Plan-Net
#
# <folder> holds the extracts, named as the daily drop names them:
#   *member*.csv         CPCDS members (one file)
#   *coverage*.csv       CPCDS coverages (one file, optional)
#   *claim*.csv          CPCDS claims (any number)
#   *auth*.csv, *278*    UM authorizations, CSV or X12 278 (any number, optional)
#
# Settings (environment):
#   CHO_TOKEN                 a tenant administrator's CHO token (required unless --local)
#   CHO_CLAIMS_URL            claims-service           (default http://localhost:5001)
#   CHO_ENROLLMENT_URL        enrollment-import-service (default http://localhost:5011)
#   CHO_AUTHORIZATION_URL     authorization-service    (default http://localhost:5005)
#   CHO_SOURCE_SYSTEM         the core's name, recorded on each claim (e.g. qnxt)
#   CHO_LINE_OF_BUSINESS      Commercial (default), Medicare, Medicaid, Exchange, TRICARE or VA
#   CHO_TRIAL_OUT             where the import reports go (default ./cpcds-trial-out)
#
# --local signs a development token for --tenant (default "demo") with
# scripts/dev-token.sh: only a stack running with ASPNETCORE_ENVIRONMENT=Development
# accepts it. "validate" reads CHO_INTEROP_TARGET_* (see scripts/interop/run.sh).
#
# Exit status: 0 when every file was read and nothing was refused, 1 when a
# file or some rows were refused (the reports say which), 2 on a usage or
# connection error.

if [ "${BASH_VERSINFO[0]:-0}" -lt 4 ]; then
  echo "error: needs bash 4 or newer (macOS: brew install bash); found ${BASH_VERSION:-unknown}." >&2
  exit 2
fi
set -euo pipefail

usage() { sed -n '2,31p' "$0" | sed 's/^# \{0,1\}//'; exit 2; }

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
COMMAND="${1:-}"; [[ -n "$COMMAND" ]] || usage; shift
FOLDER=""
LOCAL=false
TENANT="demo"
while [[ $# -gt 0 ]]; do
  case $1 in
    --local)  LOCAL=true; shift ;;
    --tenant) TENANT="$2"; shift 2 ;;
    -h|--help) usage ;;
    -*) echo "unknown option: $1" >&2; usage ;;
    *) FOLDER="$1"; shift ;;
  esac
done

CLAIMS_URL="${CHO_CLAIMS_URL:-http://localhost:5001}"
ENROLLMENT_URL="${CHO_ENROLLMENT_URL:-http://localhost:5011}"
AUTHORIZATION_URL="${CHO_AUTHORIZATION_URL:-http://localhost:5005}"
SOURCE_SYSTEM="${CHO_SOURCE_SYSTEM:-}"
LINE_OF_BUSINESS="${CHO_LINE_OF_BUSINESS:-Commercial}"
OUT="${CHO_TRIAL_OUT:-./cpcds-trial-out}"

command -v curl >/dev/null || { echo "curl is required" >&2; exit 2; }
command -v jq >/dev/null || { echo "jq is required" >&2; exit 2; }

token() {
  if $LOCAL; then
    "$ROOT/scripts/dev-token.sh" --tenant "$TENANT"
  elif [[ -n "${CHO_TOKEN:-}" ]]; then
    printf '%s' "$CHO_TOKEN"
  else
    echo "Set CHO_TOKEN to a tenant administrator's token, or pass --local for a development stack." >&2
    exit 2
  fi
}

# kind <file>: the drop's own naming rule (CpcdsDropFileKinds), so a folder that
# loads here loads the same way when the core starts writing to the drop.
kind() {
  local name; name="$(basename "$1")"; name="${name%.*}"; name="$(printf '%s' "$name" | tr '[:upper:]' '[:lower:]')"
  if [[ $name == *auth* || $name == *278* ]]; then echo authorizations
  elif [[ $name == *coverage* ]]; then echo coverages
  elif [[ $name == *member* ]]; then echo members
  elif [[ $name == *claim* ]]; then echo claims
  else echo unknown; fi
}

STATUS=0
note_refusals() { [[ "$1" -gt 0 ]] && STATUS=1 || true; }

# post <label> <url> <report-file> <curl form args...>: sets HTTP_CODE.
# Runs in this shell (never in $(...)) so its exit and STATUS take effect.
HTTP_CODE=""
post() {
  local label="$1" url="$2" report="$3"; shift 3
  local code
  code=$(curl -sS -o "$report" -w '%{http_code}' -X POST "$url" \
    -H "Authorization: Bearer $TOKEN" "$@") || { echo "$label: could not reach $url" >&2; exit 2; }
  case $code in
    200|422) ;;
    401|403) echo "$label: $url answered $code; the token is missing, expired or lacks the import permission." >&2; exit 2 ;;
    *) echo "$label: $url answered $code: $(head -c 300 "$report")" >&2; exit 2 ;;
  esac
  [[ $code == 422 ]] && STATUS=1
  HTTP_CODE="$code"
}

members_and_coverage() {
  local dry="$1" members="$2" coverages="$3"
  local report="$OUT/members-and-coverage.json"
  local args=(-F "members=@$members" -F "dryRun=$dry")
  [[ -n "$coverages" ]] && args+=(-F "coverages=@$coverages")
  post "members and coverage" "$ENROLLMENT_URL/api/v1/enrollment/import/cpcds" "$report" "${args[@]}"
  local code="$HTTP_CODE"
  jq -r --arg code "$code" '
    "  members and coverage (\($code)): \(.membersInFile // 0) members, \(.coverageRows // 0) coverage rows" +
    (if .import then ", \(.import.membersCreated // 0) created, \(.import.membersUpdated // 0) updated, \(.import.failedCount // 0) failed" else "" end) +
    "\n    unmapped plan codes: \((.planCodes.unmapped // []) | length)" +
    ((.planCodes.unmapped // [])[:10] | map("\n      " + (.externalPlanCode // .groupNumber // "?")) | join("")) +
    ((.fileErrors // []) + (.rowErrors // []) | .[:10] | map("\n    ! " + .) | join(""))' "$report"
  local refused
  refused=$(jq '((.fileErrors // []) | length) + ((.rowErrors // []) | length) + (.import.failedCount // 0) + ((.planCodes.unmapped // []) | length)' "$report")
  note_refusals "$refused"
}

claims() {
  local dry="$1" file="$2"
  local report="$OUT/claims-$(basename "$file").json"
  local args=(-F "file=@$file" -F "dryRun=$dry" -F "lineOfBusiness=$LINE_OF_BUSINESS")
  [[ -n "$SOURCE_SYSTEM" ]] && args+=(-F "sourceSystem=$SOURCE_SYSTEM")
  post "claims $(basename "$file")" "$CLAIMS_URL/api/v1/claims/import/cpcds" "$report" "${args[@]}"
  local code="$HTTP_CODE"
  jq -r --arg code "$code" --arg name "$(basename "$file")" '
    "  claims \($name) (\($code)): \(.claimsInFile // 0) claims, \(.created // 0) created, \(.updated // 0) updated, \(.unchanged // 0) unchanged, \(.rejected // 0) refused" +
    ((.fileErrors // []) | .[:5] | map("\n    ! " + .) | join("")) +
    ([(.claims // [])[] | select((.errors // []) | length > 0) | "\(.claimId): \(.errors | join("; "))"] | .[:10] | map("\n    ! " + .) | join(""))' "$report"
  note_refusals "$(jq '(.rejected // 0) + ((.fileErrors // []) | length)' "$report")"
}

authorizations() {
  local dry="$1" file="$2"
  local report="$OUT/authorizations-$(basename "$file").json"
  local endpoint=csv
  # The drop routes by content too: an X12 interchange starts with ISA.
  if head -c 512 "$file" | sed '1s/^\xEF\xBB\xBF//' | grep -q '^[[:space:]]*ISA'; then endpoint=x12-278; fi
  local args=(-F "file=@$file" -F "dryRun=$dry" -F "lineOfBusiness=$LINE_OF_BUSINESS")
  [[ -n "$SOURCE_SYSTEM" ]] && args+=(-F "sourceSystem=$SOURCE_SYSTEM")
  post "authorizations $(basename "$file")" "$AUTHORIZATION_URL/api/v1/authorizations/import/$endpoint" "$report" "${args[@]}"
  local code="$HTTP_CODE"
  jq -r --arg code "$code" --arg name "$(basename "$file")" --arg format "$endpoint" '
    "  authorizations \($name) [\($format)] (\($code)): \(.authorizationsInFile // 0) authorizations, \(.created // 0) created, \(.updated // 0) updated, \(.unchanged // 0) unchanged, \(.rejected // 0) refused" +
    ((.fileErrors // []) | .[:5] | map("\n    ! " + .) | join("")) +
    ([(.authorizations // [])[] | select((.errors // []) | length > 0) | "\(.authorizationNumber): \(.errors | join("; "))"] | .[:10] | map("\n    ! " + .) | join(""))' "$report"
  note_refusals "$(jq '(.rejected // 0) + ((.fileErrors // []) | length)' "$report")"
}

import_folder() {
  local dry="$1"
  [[ -n "$FOLDER" && -d "$FOLDER" ]] || { echo "Give the folder that holds the extracts." >&2; usage; }
  mkdir -p "$OUT"

  local members=() coverages=() claim_files=() auth_files=() unknown=()
  while IFS= read -r -d '' file; do
    case $(kind "$file") in
      members) members+=("$file") ;;
      coverages) coverages+=("$file") ;;
      claims) claim_files+=("$file") ;;
      authorizations) auth_files+=("$file") ;;
      *) unknown+=("$file") ;;
    esac
  done < <(find "$FOLDER" -maxdepth 1 -type f ! -name '.*' -print0 | sort -z)

  [[ ${#members[@]} -le 1 && ${#coverages[@]} -le 1 ]] || {
    echo "One member file and at most one coverage file per load; found ${#members[@]} and ${#coverages[@]}." >&2; exit 2; }
  [[ ${#members[@]} -eq 1 || ${#coverages[@]} -eq 0 ]] || {
    echo "A coverage file loads with its member file; add the member file." >&2; exit 2; }
  for file in "${unknown[@]}"; do echo "  skipped $(basename "$file"): its name says neither member, coverage, claim, auth nor 278"; done

  TOKEN="$(token)"
  echo "$([[ $dry == true ]] && echo 'Dry run (nothing is written)' || echo 'Loading') from $FOLDER; reports in $OUT"
  # The order the drop uses: claims and authorizations name members, so members first.
  [[ ${#members[@]} -eq 1 ]] && members_and_coverage "$dry" "${members[0]}" "${coverages[0]:-}"
  for file in "${claim_files[@]}"; do claims "$dry" "$file"; done
  for file in "${auth_files[@]}"; do authorizations "$dry" "$file"; done

  if [[ $STATUS -ne 0 ]]; then
    echo "Some files or rows were refused; the reports in $OUT say which and why."
  elif [[ $dry == true ]]; then
    echo "Clean. Run '$(basename "$0") load $FOLDER' to load."
  else
    echo "Loaded. Run '$(basename "$0") validate' with CHO_INTEROP_TARGET_* set to check the APIs."
  fi
  exit $STATUS
}

case $COMMAND in
  check) import_folder true ;;
  load) import_folder false ;;
  validate) exec "$ROOT/scripts/interop/run.sh" deployment ;;
  *) usage ;;
esac
