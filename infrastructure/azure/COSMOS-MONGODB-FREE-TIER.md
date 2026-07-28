# Cosmos DB for MongoDB free-tier benchmark

CloudHealthOffice can use an Azure Cosmos DB for MongoDB account as the
persistence layer for the Million Claim Challenge while the application
services continue to run in local Kubernetes.

The deployment is intentionally separate from
`modules/cosmos-db.bicep`, which provisions the Cosmos DB for NoSQL API.
The MongoDB module is opt-in, enables the lifetime free-tier discount when
the account is created, uses the MongoDB 5.0 API required by the repository's
MongoDB.Driver version, and fixes shared database throughput at 1,000 RU/s.

Azure permits one free-tier Cosmos DB account per subscription. The free
allowance covers the first 1,000 RU/s and 25 GB of storage. Throughput or
storage beyond those limits is billable.

## Provision the account

```bash
RESOURCE_GROUP=cho-mcc \
LOCATION=westus2 \
BASE_NAME=cho-mcc \
./scripts/azure/provision-cosmos-mongodb-free-tier.sh
```

The script creates the resource group when needed, checks for another
free-tier account, deploys the Bicep module, and prints the generated account
name. It never prints or saves account keys.

To include the account in the full Azure deployment instead, set:

```json
"enableCosmosDb": {
  "value": false
},
"enableCosmosMongoFreeTier": {
  "value": true
}
```

The MongoDB option defaults to `false`. The existing `enableCosmosDb` option
controls a separate, non-free-tier NoSQL API account and defaults to `true`;
disable it when the MongoDB free-tier account is the only Cosmos resource you
want.

The module defaults to 1,000 RU/s, but `throughput` can be raised for a
billable benchmark deployment. The lifetime-free discount still applies to
the first 1,000 RU/s.

## Connect local Kubernetes

Use the account name printed by the provision command:

```bash
COSMOS_MONGODB_ACCOUNT=<account-name> \
RESOURCE_GROUP=cho-mcc \
./scripts/azure/bootstrap-local-cosmos-mongodb.sh
```

The bootstrap reads the primary MongoDB connection string directly from
Azure and installs it as `cosmos-mongodb-secret` and `database-secret` in the
`cloudhealthoffice` namespace. It then restarts the installed services used
by the Million Claim Challenge so environment-variable secret references are
reloaded.

The local MongoDB StatefulSet is left intact. To switch the services back to
local MongoDB, clear `LOCAL_COSMOS_MONGODB_ACCOUNT` and
`LOCAL_COSMOS_MONGODB_RESOURCE_GROUP` from `.env.local`, then run:

```bash
./scripts/deploy-local.sh --skip-build
```

## Deploy local Kubernetes directly against Cosmos DB

Set both values in `.env.local` before running `deploy-local.sh`:

```bash
LOCAL_COSMOS_MONGODB_ACCOUNT=<account-name>
LOCAL_COSMOS_MONGODB_RESOURCE_GROUP=cho-mcc
```

The deployment retrieves the connection string without printing or storing it
on disk. If the variables are omitted, local Kubernetes continues to use its
MongoDB StatefulSet.

## Benchmark interpretation

The free tier is a useful throttling and retry-resilience exercise, but its
1,000 RU/s ceiling is not a maximum-throughput comparison with local MongoDB.
Report initial observation timeouts, reconciled completions, unresolved
claims, Mongo error `16500`, and overall completion time alongside the normal
Million Claim Challenge correctness score.

For a temporary benchmark, use the guarded wrapper. It records the current
manual throughput, scales up, runs the supplied command, and restores the
original value on success, failure, interruption, or terminal disconnect:

```bash
COSMOS_MONGODB_ACCOUNT=<account-name> \
RESOURCE_GROUP=cho-mcc \
TARGET_THROUGHPUT=10000 \
./scripts/azure/with-cosmos-mongodb-benchmark-throughput.sh -- \
env CLAIMS=1000 MAX_CLAIMS=1000 PARALLELISM=12 \
  ./scripts/run-mcc-local-k8s.sh
```

The wrapper rejects values above 20,000 RU/s unless `MAX_THROUGHPUT` is
explicitly increased.

As a planning baseline, the July 2026 local benchmark database held about
60.5 GB of logical document data for 9.6 million retained claims plus their
members, providers, and event history. That suggests a clean one-million-claim
run should fit inside 25 GB, but accumulated runs should be deleted or deployed
to a fresh database before the free storage allowance is approached.
