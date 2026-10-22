#!/usr/bin/env bash
# export-mcc-evidence.sh — publish a Million Claim Challenge run's evidence.
#
# Reads the run that the MCC platform validator persisted through
# claims-service (MongoDB collections MassAdjudicationRuns and
# MassAdjudicationClaimResults) and writes it to the repository, so every
# headline number can be checked claim by claim:
#
#   docs/million-claim-challenge/runs/<tenant>/run.json
#       the run summary: counts, timings, per-scenario breakdown, payment gate
#   docs/million-claim-challenge/runs/<tenant>/not-matched.json
#       every scored claim that did not match: Mismatched, ObservationTimeout
#       and Unsupported, with expected and observed outcome, denial code and
#       payment
#   docs/million-claim-challenge/runs/<tenant>/validator.log   (when LOG_FILE is given)
#
# The corpus is synthetic (seeded generator, no member data), so these files
# are safe to publish.
#
# Usage:
#   TENANT=mcc-1m-confirmation-v2 scripts/export-mcc-evidence.sh
#   TENANT=... KUBE_CONTEXT=docker-desktop OUT_DIR=/tmp/x scripts/export-mcc-evidence.sh
#
# Environment:
#   TENANT        required: the --tenant the run used
#   RUN_ID        optional: a specific run id (default: the tenant's latest run)
#   NAMESPACE     default cloudhealthoffice
#   KUBE_CONTEXT  default: current kubectl context (local runs: docker-desktop)
#   MONGO_POD     default mongodb-0
#   MONGO_USER / MONGO_PASS  default admin / local-dev-only (scripts/deploy-local.sh)
#   OUT_DIR       default docs/million-claim-challenge/runs/<tenant>
#   LOG_FILE      optional: a validator log to copy alongside

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TENANT="${TENANT:?TENANT is required (the --tenant the run used)}"
RUN_ID="${RUN_ID:-}"
NAMESPACE="${NAMESPACE:-cloudhealthoffice}"
MONGO_POD="${MONGO_POD:-mongodb-0}"
MONGO_USER="${MONGO_USER:-admin}"
MONGO_PASS="${MONGO_PASS:-local-dev-only}"
OUT_DIR="${OUT_DIR:-$ROOT_DIR/docs/million-claim-challenge/runs/$TENANT}"

if [[ ! "$TENANT" =~ ^[A-Za-z0-9._-]+$ ]]; then
  echo "TENANT may contain only letters, digits, '.', '_' and '-'" >&2
  exit 2
fi
if [[ -n "$RUN_ID" && ! "$RUN_ID" =~ ^[A-Za-z0-9]+$ ]]; then
  echo "RUN_ID may contain only letters and digits" >&2
  exit 2
fi

kubectl_args=(-n "$NAMESPACE")
[[ -n "${KUBE_CONTEXT:-}" ]] && kubectl_args=(--context "$KUBE_CONTEXT" "${kubectl_args[@]}")

mongo() {
  # The query reads TENANT and RUN_ID from the pod environment, never from
  # interpolated script text.
  kubectl "${kubectl_args[@]}" exec "$MONGO_POD" -- \
    env CHO_TENANT="$TENANT" CHO_RUN_ID="$RUN_ID" \
    mongosh --username "$MONGO_USER" --password "$MONGO_PASS" --authenticationDatabase admin \
    --quiet --eval "$1"
}

mkdir -p "$OUT_DIR"

# Strip MongoDB's EJSON wrappers so the files read as plain JSON.
plain_json() {
  python3 -c '
import json, sys
def plain(v):
    if isinstance(v, dict):
        if len(v) == 1:
            (k, inner), = v.items()
            if k == "$date":
                return inner.get("$numberLong", inner) if isinstance(inner, dict) else inner
            if k in ("$numberLong", "$numberInt"):
                return int(inner)
            if k == "$numberDouble":
                return float(inner)
            if k == "$numberDecimal":
                return inner  # money: keep the exact decimal text
        return {k: plain(x) for k, x in v.items()}
    if isinstance(v, list):
        return [plain(x) for x in v]
    return v
json.dump(plain(json.load(sys.stdin)), sys.stdout, indent=2)
print()
'
}

run_json="$(mongo '
const d = db.getSiblingDB("CloudHealthOffice");
const filter = { "Run.TenantId": process.env.CHO_TENANT };
if (process.env.CHO_RUN_ID) filter._id = process.env.CHO_RUN_ID;
const run = d.MassAdjudicationRuns.find(filter, { ClaimResults: 0, LifecycleTimings: 0 })
  .sort({ CreatedAtUtc: -1 }).limit(1).toArray()[0];
print(run ? EJSON.stringify(run, { relaxed: true }) : "null");
')"

if [[ "$run_json" == "null" || -z "$run_json" ]]; then
  echo "No MCC run found for tenant ${TENANT}${RUN_ID:+ (run ${RUN_ID})} in ${NAMESPACE}/${MONGO_POD}." >&2
  echo "Check the kubectl context (KUBE_CONTEXT=docker-desktop for local runs) and the tenant name." >&2
  exit 1
fi

run_id="$(python3 -c 'import json,sys; print(json.load(sys.stdin)["_id"])' <<<"$run_json")"
plain_json <<<"$run_json" > "$OUT_DIR/run.json"

RUN_ID="$run_id"
mongo '
const d = db.getSiblingDB("CloudHealthOffice");
const rows = d.MassAdjudicationClaimResults.find(
  { TenantId: process.env.CHO_TENANT, RunId: process.env.CHO_RUN_ID,
    ValidationStatus: { $in: ["Mismatched", "ObservationTimeout", "Unsupported"] } },
  { _id: 0, AdjudicationStepMilliseconds: 0, TenantId: 0 })
  .sort({ ValidationScenario: 1, GeneratedClaimId: 1 }).toArray();
print(EJSON.stringify(rows, { relaxed: true }));
' | plain_json > "$OUT_DIR/not-matched.json"

if [[ -n "${LOG_FILE:-}" && -f "$LOG_FILE" ]]; then
  cp "$LOG_FILE" "$OUT_DIR/validator.log"
fi

python3 - "$OUT_DIR" <<'PY'
import collections, json, sys, pathlib
out = pathlib.Path(sys.argv[1])
run = json.loads((out / "run.json").read_text())
rows = json.loads((out / "not-matched.json").read_text())
print(f"Run {run.get('_id')}: {run.get('Processed')}/{run.get('TotalClaims')} processed, "
      f"workflow {run.get('WorkflowMatches')}/{run.get('WorkflowScenarios')} matched, "
      f"payments {run.get('PaymentMatches')}/{run.get('PaymentComparisons')} exact")
by = collections.Counter((r.get("ValidationStatus"), r.get("ValidationScenario")) for r in rows)
for (status, scenario), n in sorted(by.items(), key=lambda kv: (-kv[1], str(kv[0]))):
    print(f"  {n:>5}  {status:<18} {scenario}")
print(f"Wrote {out}/run.json and {out}/not-matched.json ({len(rows)} claims)")
PY
