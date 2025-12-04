# Managed Services Deployment Matrix

The Cloud Health Office Kubernetes deployment favors managed services for production workloads handling PHI. Use the matrix below to map each environment to its external dependencies and the Kubernetes identities that access them.

| Environment | Kubernetes Namespace | Service Accounts / IRSA Role | Object Storage | Secrets Platform | Kafka / Messaging | Monitoring & Logging | Notes |
|-------------|----------------------|------------------------------|----------------|------------------|-------------------|----------------------|-------|
| Development | `cloudhealthoffice`  | `sa/argo-workflow-sa` bound to `arn:aws:iam::123456789012:role/irsa-cloudhealthoffice-dev` | `s3://hipaa-attachments-dev` (`us-east-1`) with SSE-S3 | AWS Secrets Manager path `arn:aws:secretsmanager:us-east-1:123456789012:secret:cloudhealthoffice/dev/*` | Amazon MSK `b-1.dev-msk.coh.local:9092` (`TLS/SASL`) | Amazon Managed Prometheus `ws-AMP-DEV` + Amazon Managed Grafana `g-DEV` | Allow egress to VPC endpoints; relaxed quotas for functional testing |
| Staging     | `cloudhealthoffice`  | `sa/argo-workflow-sa` bound to `arn:aws:iam::123456789012:role/irsa-cloudhealthoffice-stg` | `s3://hipaa-attachments-stg` (`us-east-1`) with SSE-KMS (`alias/hipaa-attachments`) | AWS Secrets Manager `...:secret:cloudhealthoffice/stg/*` with 60-day rotation | Amazon MSK `b-1.stg-msk.coh.local:9092` (mirrors prod topology) | Amazon Managed Prometheus `ws-AMP-STG` + Grafana `g-STG` with prod dashboards | Mirror prod capacity planning; enable canary Argo workflows before promotion |
| Production  | `cloudhealthoffice`  | `sa/argo-workflow-sa` bound to `arn:aws:iam::123456789012:role/irsa-cloudhealthoffice-prod` | `s3://hipaa-attachments` (`us-east-1`) with SSE-KMS (`key-id: abcdef01-2345-6789-abcd-ef0123456789`) + object lock | AWS Secrets Manager `...:secret:cloudhealthoffice/prod/*` with 30-day rotation & audit logging | Amazon MSK `b-1.prod-msk.coh.local:9092` (3 AZ, TLS/SASL) + DLQ topic | Amazon Managed Prometheus `ws-AMP-PROD` + Grafana `g-PROD` + CloudWatch export to SOC | Enforce network policy egress only to VPC endpoints; ensure DR bucket replication to secondary region |

## How to Use

- Update the ARN, endpoint, and bucket placeholders with the exact values for your AWS account or cloud provider of choice. If you deploy to Azure, adjust the columns to reference Blob Storage, Key Vault, Event Hubs (Kafka API), and Azure Monitor.
- Reflect any additional namespaces or service accounts that need managed-service access (for example, `argo-events-sa`). The IRSA role column should capture every bound IAM role or workload identity.
- Extend the table with extra columns (e.g., "PrivateLink Endpoint" or "Failover Region") as your governance requirements evolve.
- Keep this document in sync with Helm `values` overrides, `NetworkPolicy` resources, and platform runbooks to ensure operational parity across environments.
