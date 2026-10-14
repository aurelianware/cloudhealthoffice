## Episode metadata
- Series: Scaling a Healthcare Payer Core Admin and Interoperability Platform in Google Cloud
- Episode title: Part One — Same Code, Different Cloud
- Source article: article.txt
- Published article: /insights/gcp-scaling/part-1-same-code-different-cloud
- Status: draft (pending author review and the end-to-end smoke test)

## Summary

The Google Cloud leg the Azure series promised. Over two days in October 2026 the platform went
from nothing on Google Cloud to every deployment ready on GKE, with no application rewrite:
Terraform (Artifact Registry, GKE, keyless GitHub Actions deploys through Workload Identity
Federation), Kafka under Strimzi, and in-cluster MongoDB, Redis and Postgres. The deploy renders the
same manifests used locally and on AKS.

Walls, in order:
1. NU1605 restore break from a grouped Dependabot bump (#1313, fixed in #1315), then a second one
   from Azure.Storage.Blobs 12.30.1 requiring Azure.Core >= 1.60 (held at 12.29.2 in #1319).
2. New billing account quotas (250 GB SSD, 12 vCPU, 4 IPs) left Autopilot pods unschedulable. The
   increase was declined even after upgrading to a paid account, for lack of usage history.
3. Redesign: one zonal GKE Standard e2-standard-8 node on pd-standard, plus a small resource profile
   (CPU requests capped, limits unchanged, one replica each; ~2.2 vCPU of service requests).
   About $7/day. (#1317)
4. terraform validate passed but plan failed: enable_autopilot=false conflicts with
   remove_default_node_pool (#1319).
5. First deploy: 33 of 35 platform services ready. Two shared-manifest bugs, also present locally
   and on AKS (#1325):
   - pricing-api crash-looped on mongodb://localhost: Program.cs copies
     PricingApi:MongoConnectionString over MongoDb:*, so the manifest's MongoDb__ConnectionString
     was overwritten.
   - benefit-plan-service never became ready: its accumulator-integrity readiness check requires
     Redis maxmemory-policy noeviction, and the shared manifest said volatile-lru.
6. Redeploy: all 35 services plus Redis ready (MongoDB, Postgres and Kafka already up).

Scope: portability, not scale. No GCP throughput numbers are claimed.

## Planned series arc

- Part One: Google Cloud. Standing it up, the quota redesign, the shared-manifest bugs.
- Part Two: Google Cloud. With quota, Autopilot or a larger pool at full reservations, then a
  Million Claim Challenge run on GKE with the same corpus and correctness checks.
