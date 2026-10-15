#!/usr/bin/env bash
# reset-demo.sh — Clear the claims activity for one tenant on the GKE demo, so a
# fresh claim load (run-mcc.sh) starts from a clean slate.
#
# Dry run by default: prints what would be deleted. Pass --yes to delete.
#
#   ./scripts/demo/gcp/reset-demo.sh                # dry run, tenant "demo"
#   ./scripts/demo/gcp/reset-demo.sh --yes
#   ./scripts/demo/gcp/reset-demo.sh --tenant other --yes
#
# Clears, for that tenant only:
#   claims and adjudication: Claims, AiExaminationAudit, MassAdjudicationRuns,
#     MassAdjudicationClaimResults, claim-import-transactions
#   accumulators: Accumulators, AccumulatorEvents, AccumulatorSnapshots,
#     AccumulatorProcessedClaims, and the Redis accumulator hashes (accum:<tenant>:*)
#   payments: Payments, PaymentRuns, PaymentClaimReservations, PaymentReservationAudit,
#     EraEnvelopes, ReversalRuns, ProviderReceivables
#   work and follow-up on claims: WorkItems, WorkAudit, Appeals, AppealEvents, RfaiCases
#
# Keeps: the tenant, users and roles, members, benefit plans, providers,
# contracts, fee schedules, prior-auth rules, and the work queues, teams and
# memberships (supervisor setup). General-ledger and AR postings are not
# touched. Needs kubectl pointed at the demo cluster.

set -euo pipefail

NAMESPACE="cloudhealthoffice"
TENANT="demo"
APPLY=false
while (( $# )); do
  case "$1" in
    --yes) APPLY=true ;;
    --tenant) TENANT="${2:?--tenant needs a value}"; shift ;;
    -h|--help) sed -n '2,24p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Unknown argument: $1 (see --help)" >&2; exit 1 ;;
  esac
  shift
done
[[ "$TENANT" =~ ^[A-Za-z0-9_-]+$ ]] || { echo "Tenant id must be letters, digits, - or _" >&2; exit 1; }

context="$(kubectl config current-context 2>/dev/null || true)"
[[ -n "$context" ]] || { echo "kubectl has no current context; run gcloud container clusters get-credentials first" >&2; exit 1; }
kubectl get statefulset mongodb -n "$NAMESPACE" >/dev/null 2>&1 \
  || { echo "No MongoDB in namespace $NAMESPACE on $context; is this the demo cluster?" >&2; exit 1; }

MONGO_USER="$(kubectl get secret mongodb-auth -n "$NAMESPACE" -o jsonpath='{.data.username}' | base64 -d)"
MONGO_PASS="$(kubectl get secret mongodb-auth -n "$NAMESPACE" -o jsonpath='{.data.password}' | base64 -d)"

if [[ "$APPLY" == true ]]; then
  echo "Deleting claims activity for tenant '$TENANT' on $context"
else
  echo "Dry run for tenant '$TENANT' on $context (pass --yes to delete)"
fi

# Every database is searched, so this works whichever database each service uses.
kubectl exec -n "$NAMESPACE" mongodb-0 -- mongosh --quiet \
  --username "$MONGO_USER" --password "$MONGO_PASS" --authenticationDatabase admin --eval "
const tenant = '$TENANT';
const apply = $APPLY;
const targets = [
  'Claims', 'AiExaminationAudit', 'MassAdjudicationRuns', 'MassAdjudicationClaimResults', 'claim-import-transactions',
  'Accumulators', 'AccumulatorEvents', 'AccumulatorSnapshots', 'AccumulatorProcessedClaims',
  'Payments', 'PaymentRuns', 'PaymentClaimReservations', 'PaymentReservationAudit', 'EraEnvelopes', 'ReversalRuns', 'ProviderReceivables',
  'WorkItems', 'WorkAudit', 'Appeals', 'AppealEvents', 'RfaiCases',
];
// MassAdjudicationRuns keep the tenant inside the run (Run.TenantId).
const filter = { \$or: [{ tenantId: tenant }, { TenantId: tenant }, { 'Run.TenantId': tenant }, { 'run.tenantId': tenant }] };
let total = 0;
for (const { name: dbName } of db.adminCommand({ listDatabases: 1, nameOnly: true }).databases) {
  if (['admin', 'config', 'local'].includes(dbName)) continue;
  const d = db.getSiblingDB(dbName);
  const present = new Set(d.getCollectionNames());
  for (const c of targets) {
    if (!present.has(c)) continue;
    const n = apply ? d.getCollection(c).deleteMany(filter).deletedCount : d.getCollection(c).countDocuments(filter);
    if (n > 0) print('  ' + (apply ? 'deleted ' : 'would delete ') + n + '  ' + dbName + '.' + c);
    total += n;
  }
}
print('  ' + (apply ? 'deleted ' : 'would delete ') + total + ' documents in total');
"

# Accumulator hashes in Redis carry the commit journal and claim fences; clear
# them with the Mongo accumulators so totals and fences start from zero.
redis_count="$(kubectl exec -n "$NAMESPACE" deploy/redis-dataprotection -- \
  redis-cli --scan --pattern "accum:${TENANT}:*" | wc -l | tr -d ' ')"
if [[ "$APPLY" == true && "$redis_count" -gt 0 ]]; then
  kubectl exec -n "$NAMESPACE" deploy/redis-dataprotection -- sh -c \
    "redis-cli --scan --pattern 'accum:${TENANT}:*' | xargs -r -n 500 redis-cli del" >/dev/null
  echo "  deleted $redis_count Redis accumulator hashes (accum:${TENANT}:*)"
else
  echo "  $( [[ "$APPLY" == true ]] && echo deleted || echo 'would delete' ) $redis_count Redis accumulator hashes (accum:${TENANT}:*)"
fi

if [[ "$APPLY" == true ]]; then
  echo "Done. Load fresh claims with: GAR_REPOSITORY=... IMAGE_TAG=... ./scripts/demo/gcp/run-mcc.sh"
fi
